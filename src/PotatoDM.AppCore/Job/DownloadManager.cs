using System.Collections.ObjectModel;
using System.Globalization;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Core.Transfer;
using PotatoDM.Torrent;

namespace PotatoDM.AppCore.Job;

/// <summary>
/// Orchestrates the jobs shown by the UI. The engine sidecar stays authoritative
/// for progress; this class only owns the list, lifetime and cancellation.
/// Public mutating members are expected to be called from the UI thread.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The process-lifetime semaphore does not create a wait handle.")]
public sealed partial class DownloadManager
{
    private readonly IUiDispatcher _ui;
    private readonly JobRegistryStore _registry;
    private readonly IDownloadEngineFactory _engines;
    private readonly IDownloadStateStore _stateStore;
    private readonly AppSettings _settings;
    private readonly BandwidthLimiter _bandwidthLimiter;
    private readonly ICookieProtector _cookieProtector;
    private readonly Func<string> _defaultFolder;
    private readonly TimeSpan _browserProbeTimeout;
    private readonly TimeSpan _manualProbeTimeout;
    private readonly ILogSink _log;
    private readonly Func<
        TorrentDownloadRequest,
        Action<TorrentProgress>?,
        CancellationToken,
        Task<TorrentDownloadResult>> _torrentDownloader;
    private readonly ObservableCollection<DownloadJob> _jobs = new();
    private readonly List<JobRecord> _records = new();
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly List<PendingStart> _queue = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _manualProbeSlots = new(3, 3);
    private readonly Dictionary<string, (string Folder, int Connections)> _pendingProbes = new();

    public DownloadManager(
        IUiDispatcher ui,
        JobRegistryStore registry,
        IDownloadEngineFactory engines,
        AppSettings settings,
        ICookieProtector cookieProtector,
        IDownloadStateStore? stateStore = null,
        Func<string>? defaultFolder = null,
        TimeSpan? browserProbeTimeout = null,
        Func<
            TorrentDownloadRequest,
            Action<TorrentProgress>?,
            CancellationToken,
            Task<TorrentDownloadResult>>? torrentDownloader = null,
        TimeSpan? manualProbeTimeout = null,
        ILogSink? log = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? NullLogSink.Instance;
        // One instance for every job: the cap is process-wide, and the provider
        // picks up settings changes without recreating in-flight requests.
        _bandwidthLimiter = new BandwidthLimiter(() => _settings.MaxBytesPerSecond);
        _cookieProtector = cookieProtector ?? throw new ArgumentNullException(nameof(cookieProtector));
        _stateStore = stateStore ?? new JsonDownloadStateStore(_log);
        _defaultFolder = defaultFolder ?? (() => AppPaths.DefaultDownloadFolder);
        _browserProbeTimeout = browserProbeTimeout ?? TimeSpan.FromSeconds(8);
        // Deliberately much longer than the browser probe: a manual URL may point at
        // a slow or distant server, and a user who typed it can wait. The browser
        // path keeps its short timeout because the browser is blocked on us.
        _manualProbeTimeout = manualProbeTimeout ?? TimeSpan.FromSeconds(30);
        _torrentDownloader = torrentDownloader
            ?? ((request, progress, token) =>
                TorrentDownloader.DownloadAsync(request, progress, token));
        Jobs = new ReadOnlyObservableCollection<DownloadJob>(_jobs);
    }

    public ReadOnlyObservableCollection<DownloadJob> Jobs { get; }

    /// <summary>
    /// Probes the URL, resolves the target file, records the job and starts it.
    /// The returned task completes once the download has been started, not when
    /// it finishes.
    /// </summary>
    public async Task<DownloadJob> AddAsync(
        Uri url,
        string folder,
        int connections,
        CancellationToken cancellationToken = default)
    {
        var job = new DownloadJob(Guid.NewGuid().ToString("N"), url.ToString(), DateTimeOffset.UtcNow);
        _jobs.Add(job);
        _pendingProbes[job.Id] = (folder, connections);
        await ProbeManualAsync(job, url, folder, connections, cancellationToken);
        return job;
    }

    private async Task ProbeManualAsync(DownloadJob job, Uri url, string folder, int connections, CancellationToken cancellationToken)
    {
        job.MarkProbing();
        var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Without this a server that accepts the connection and then never answers
        // leaves the row stuck at "探测中" forever: HttpClient's own default is
        // 100 seconds of *inactivity*, which a slow trickle of headers resets.
        lock (_sync)
        {
            _cancellations[job.Id] = probeCts;
        }

        var entered = false;
        try
        {
            await _manualProbeSlots.WaitAsync(probeCts.Token);
            entered = true;
            probeCts.CancelAfter(_manualProbeTimeout);
            var engine = _engines.Create();
            var probe = await engine.ProbeAsync(url, CreateRequestOptions(), probeCts.Token);
            var isMedia = IsMediaRequest(url, probe);

            if (probe.TotalLength is null && !isMedia)
            {
                throw new ProbeException("服务器没有提供文件长度，无法分段下载。");
            }

            if (IsRemoved(job.Id))
            {
                return;
            }

            // The UI always passes a folder. Creating it first makes the
            // resolver treat it as a directory instead of a file name.
            Directory.CreateDirectory(folder);
            var suggestedName = isMedia
                ? ProvisionalMediaFileName(probe.SuggestedFileName, probe.FinalUri)
                : probe.SuggestedFileName;
            var layout = ReserveLayout(folder, suggestedName);
            RegisterJob(
                job,
                layout,
                probe.TotalLength ?? 0,
                isMedia,
                isTorrent: false,
                metadata: null);

            _pendingProbes.Remove(job.Id);
            if (!IsRemoved(job.Id))
            {
                var selectedConnections = connections > 0
                    ? connections
                    : isMedia || probe.TotalLength is null
                        ? _settings.Connections
                        : DownloadConnectionPolicy.Select(probe.TotalLength.Value, probe.SupportsRanges);
                Enqueue(job, selectedConnections, isResume: false);
            }
        }
        catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
        {
            if (!IsRemoved(job.Id))
            {
                CleanupAfterFailure(job);

                // Two very different outcomes share this exception type. Reporting a
                // timeout as "已取消" would blame the user for a dead server, and a
                // user cancel as "超时" would hide that the button did what it said.
                if (cancellationToken.IsCancellationRequested)
                {
                    PostFailure(job, "已取消。");
                }
                else
                {
                    _log.Warning(
                        $"Manual probe of '{url}' timed out after " +
                        $"{_manualProbeTimeout.TotalSeconds:0.#}s without a response.");

                    PostFailure(
                        job,
                        "探测下载地址超时：" +
                        $"{_manualProbeTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}" +
                        " 秒内服务器没有响应。请检查网络或链接后重试。");
                }
            }
        }
        catch (Exception ex)
        {
            if (!IsRemoved(job.Id))
            {
                _log.Warning($"Manual probe of '{url}' failed.", ex);
                CleanupAfterFailure(job);
                PostFailure(job, JobFailure.Details(ex), JobFailure.Classify(ex));
            }
        }
        finally
        {
            if (entered) _manualProbeSlots.Release();
            RemoveProbeCancellation(job.Id, probeCts);
            probeCts.Dispose();
        }

    }

    /// <summary>
    /// Adds a magnet link. Validation is synchronous and cheap, so the job is
    /// queued immediately and peer discovery happens in the worker.
    /// </summary>
    public Task<DownloadJob> AddTorrentAsync(
        string source,
        string folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        TorrentSource.Validate(source);
        Directory.CreateDirectory(folder);

        var layout = ReserveLayout(folder, TorrentSource.SuggestedName(source));
        var job = new DownloadJob(Guid.NewGuid().ToString("N"), source, DateTimeOffset.UtcNow);
        job.SetDisplayName(TorrentSource.SuggestedName(source));

        _jobs.Add(job);
        RegisterJob(
            job,
            layout,
            0,
            isMedia: false,
            isTorrent: true,
            metadata: null);

        if (!IsRemoved(job.Id))
        {
            Enqueue(job, _settings.Connections, isResume: false);
        }

        return Task.FromResult(job);
    }

    public async Task PauseAsync(DownloadJob job)
    {
        if (!job.CanPause)
        {
            return;
        }

        if (job.State == DownloadJobState.Queued)
        {
            lock (_sync)
            {
                _queue.RemoveAll(entry => entry.Job.Id == job.Id);
            }

            // A queued job may have just been handed to a worker; cancel it too.
            job.RequestCancel();
            _ui.Post(job.MarkPaused);
            return;
        }

        job.RequestCancel();
        await WaitForJobAsync(job.Id, TimeSpan.FromSeconds(15));
    }

    public Task ResumeAsync(DownloadJob job)
    {
        if (!job.CanResume)
        {
            return Task.CompletedTask;
        }

        ScheduleResume(job);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Downloads the file again from zero in the same task folder, discarding the
    /// saved progress. Deliberately not Remove+Add: a fresh job would collide with
    /// the existing folder, land in "name (1)", and leave a renamed copy of a file
    /// the user only wanted to retry.
    /// </summary>
    public Task RestartAsync(DownloadJob job)
    {
        if (!job.CanRestart)
        {
            return Task.CompletedTask;
        }

        if (_pendingProbes.TryGetValue(job.Id, out var pending))
        {
            return ProbeManualAsync(job, new Uri(job.Url), pending.Folder, pending.Connections, CancellationToken.None);
        }

        Enqueue(job, _settings.Connections, isResume: false, restart: true);
        return Task.CompletedTask;
    }

    public async Task<string?> RemoveAsync(DownloadJob job, bool deletePartialData)
    {
        CancellationTokenSource? probe;

        lock (_sync)
        {
            _removed.Add(job.Id);
            _queue.RemoveAll(entry => entry.Job.Id == job.Id);
            _cancellations.TryGetValue(job.Id, out probe);
        }

        // A job that is still probing has no download CTS yet; cancel the probe.
        try
        {
            probe?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        job.RequestCancel();
        await WaitForJobAsync(job.Id, TimeSpan.FromSeconds(15));

        _pendingProbes.Remove(job.Id);
        _jobs.Remove(job);

        lock (_sync)
        {
            _records.RemoveAll(record => record.Id == job.Id);
        }

        ReleaseReservations(job);

        // Removing a job always removes its process artifacts. Unfinished
        // payloads go too; a completed file stays unless the caller explicitly
        // removes it elsewhere.
        var deleteWholeTask = deletePartialData && job.State != DownloadJobState.Completed;
        await Task.Run(() =>
        {
            if (deleteWholeTask)
            {
                DeleteTaskDirectory(job);
            }
            else
            {
                DeleteProcessArtifacts(job);
            }
        });

        RequestRegistrySave();
        if (!string.IsNullOrEmpty(job.TaskDirectory))
        {
            var path = deleteWholeTask ? job.TaskDirectory : job.ProcessDirectory;
            return Directory.Exists(path) ? path : null;
        }

        var leftovers = new[] { job.StatePath, job.StatePath + ".bak", job.StatePath + ".tmp",
            job.StatePath + ".lock", StatePaths.WorkingFor(job.TargetPath),
            job.IsMedia ? job.TargetPath + MediaDownloader.CacheSuffix : null,
            job.IsTorrent ? job.TargetPath : null };
        return leftovers.FirstOrDefault(path => !string.IsNullOrEmpty(path) &&
            (File.Exists(path) || Directory.Exists(path)));

    }

    private static bool IsMediaRequest(Uri url, ProbeResult probe) =>
        ManifestDetector.FromUrl(probe.FinalUri) is not null ||
        ManifestDetector.FromUrl(url) is not null ||
        ManifestDetector.FromContentType(probe.ContentType) is not null;

    /// <summary>
    /// Manifest URLs have no useful container name. Reserve a placeholder .mp4
    /// so two jobs never pick the same stem; the engine renames the file to the
    /// real container (.ts/.mp4/.webm) once the manifest has been parsed.
    /// </summary>
    private static string ProvisionalMediaFileName(string? suggestedName, Uri finalUri)
    {
        var raw = string.IsNullOrWhiteSpace(suggestedName) ||
                  suggestedName.Equals("download.bin", StringComparison.OrdinalIgnoreCase)
            ? null
            : suggestedName.Trim();

        var stripped = MediaNaming.StripManifestExtension(raw);
        var baseName = string.IsNullOrWhiteSpace(stripped)
            ? MediaNaming.BaseNameFromUrl(finalUri)
            : Path.GetFileNameWithoutExtension(stripped);

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = MediaNaming.BaseNameFromUrl(finalUri);
        }

        return baseName + ".mp4";
    }

    private void RemoveProbeCancellation(string jobId, CancellationTokenSource probeCts)
    {
        lock (_sync)
        {
            if (_cancellations.TryGetValue(jobId, out var current) &&
                ReferenceEquals(current, probeCts))
            {
                _cancellations.Remove(jobId);
            }
        }
    }

    private bool IsRemoved(string id)
    {
        lock (_sync)
        {
            return _removed.Contains(id);
        }
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
