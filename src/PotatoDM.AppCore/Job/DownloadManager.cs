using System.Collections.ObjectModel;
using System.Security;
using System.Text.Json;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;
using PotatoDM.Torrent;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Core.Transfer;

namespace PotatoDM.AppCore.Job;

/// <summary>
/// Orchestrates the jobs shown by the UI. The engine sidecar stays authoritative
/// for progress; this class only owns the list, lifetime and cancellation.
/// Public mutating members are expected to be called from the UI thread.
/// </summary>
public sealed class DownloadManager
{
    private const int BrowserResultLimit = 128;
    private const int TorrentChunkCount = 48;
    private static readonly TimeSpan BrowserResultLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions CookieJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IUiDispatcher _ui;
    private readonly JobRegistryStore _registry;
    private readonly IDownloadEngineFactory _engines;
    private readonly IDownloadStateStore _stateStore;
    private readonly AppSettings _settings;
    private readonly BandwidthLimiter _bandwidthLimiter;
    private readonly ICookieProtector _cookieProtector;
    private readonly Func<string> _defaultFolder;
    private readonly TimeSpan _browserProbeTimeout;
    private readonly Func<
        TorrentDownloadRequest,
        Action<TorrentProgress>?,
        CancellationToken,
        Task<TorrentDownloadResult>> _torrentDownloader;
    private readonly ObservableCollection<DownloadJob> _jobs = new();
    private readonly List<JobRecord> _records = new();
    private readonly Dictionary<string, DownloadJob> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly List<PendingStart> _queue = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservedTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (BrowserAddResult Result, DateTimeOffset At)> _browserResults =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();

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
            Task<TorrentDownloadResult>>? torrentDownloader = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        // One instance for every job: the cap is process-wide, and the provider
        // picks up settings changes without recreating in-flight requests.
        _bandwidthLimiter = new BandwidthLimiter(() => _settings.MaxBytesPerSecond);
        _cookieProtector = cookieProtector ?? throw new ArgumentNullException(nameof(cookieProtector));
        _stateStore = stateStore ?? new JsonDownloadStateStore();
        _defaultFolder = defaultFolder ?? (() => AppPaths.DefaultDownloadFolder);
        _browserProbeTimeout = browserProbeTimeout ?? TimeSpan.FromSeconds(8);
        _torrentDownloader = torrentDownloader
            ?? ((request, progress, token) =>
                new TorrentDownloader().DownloadAsync(request, progress, token));
        Jobs = new ReadOnlyObservableCollection<DownloadJob>(_jobs);
    }

    public ReadOnlyObservableCollection<DownloadJob> Jobs { get; }

    /// <summary>Restores the job list from the registry and each engine sidecar.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var records = await _registry.LoadAsync(cancellationToken);
        var keptRecords = new List<JobRecord>();
        var restoredJobs = new List<DownloadJob>();

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Id) ||
                string.IsNullOrWhiteSpace(record.Url) ||
                string.IsNullOrWhiteSpace(record.TargetPath) ||
                string.IsNullOrWhiteSpace(record.StatePath))
            {
                continue;
            }

            var job = await RestoreAsync(record, cancellationToken);
            if (job is null)
            {
                continue;
            }

            keptRecords.Add(record);
            restoredJobs.Add(job);
        }

        _records.Clear();
        _records.AddRange(keptRecords);
        _jobs.Clear();
        _byId.Clear();

        foreach (var job in restoredJobs)
        {
            _jobs.Add(job);
            _byId[job.Id] = job;
        }

        lock (_sync)
        {
            _reservedTargets.Clear();
            foreach (var job in restoredJobs)
            {
                _reservedTargets.Add(job.TargetPath);
            }

            _browserResults.Clear();
        }

        if (keptRecords.Count != records.Count)
        {
            await SaveRegistryBestEffortAsync(cancellationToken);
        }
    }

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
        Track(job);

        var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_sync)
        {
            _cancellations[job.Id] = probeCts;
        }

        try
        {
            var engine = _engines.Create();
            var probe = await engine.ProbeAsync(url, CreateRequestOptions(), probeCts.Token);
            var isMedia = IsMediaRequest(url, probe);

            if (probe.TotalLength is null && !isMedia)
            {
                throw new ProbeException("服务器没有提供文件长度，无法分段下载。");
            }

            if (IsRemoved(job.Id))
            {
                return job;
            }

            // The UI always passes a folder. Creating it first makes the
            // resolver treat it as a directory instead of a file name.
            Directory.CreateDirectory(folder);
            var suggestedName = isMedia
                ? ProvisionalMediaFileName(probe.SuggestedFileName, probe.FinalUri)
                : probe.SuggestedFileName;
            var layout = ReserveLayout(folder, suggestedName);
            await RegisterJobAsync(
                job,
                layout,
                probe.TotalLength ?? 0,
                isMedia,
                isTorrent: false,
                metadata: null,
                cancellationToken);

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
                PostFailure(job, "已取消。");
            }
        }
        catch (Exception ex)
        {
            if (!IsRemoved(job.Id))
            {
                CleanupAfterFailure(job);
                PostFailure(job, ex.Message);
            }
        }
        finally
        {
            RemoveProbeCancellation(job.Id, probeCts);
            probeCts.Dispose();
        }

        return job;
    }

    /// <summary>
    /// Adds a magnet link. Validation is synchronous and cheap, so the job is
    /// queued immediately and peer discovery happens in the worker.
    /// </summary>
    public async Task<DownloadJob> AddTorrentAsync(
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

        Track(job);
        await RegisterJobAsync(
            job,
            layout,
            0,
            isMedia: false,
            isTorrent: true,
            metadata: null,
            cancellationToken);

        if (!IsRemoved(job.Id))
        {
            Enqueue(job, _settings.Connections, isResume: false);
        }

        return job;
    }

    /// <summary>
    /// Accepts a hand-off from the browser extension. The returned result is
    /// the complete answer: the placeholder job is removed on rejection so no
    /// failed row is left behind.
    /// </summary>
    public async Task<BrowserAddResult> AddFromBrowserAsync(
        BrowserDownloadContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var isMagnet =
            string.Equals(context.Kind, "magnet", StringComparison.OrdinalIgnoreCase) ||
            context.Url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase);

        if (isMagnet
                ? !context.Url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
                : context.Url.Scheme is not ("http" or "https"))
        {
            return BrowserAddResult.Reject(
                "invalid_request",
                isMagnet ? "磁力链接无效。" : "仅支持 http/https 下载。");
        }

        if (TryGetCachedBrowserResult(context.RequestId) is { } cached)
        {
            return cached;
        }

        var folder = SelectBrowserFolder();
        if (folder is null)
        {
            return CacheBrowserResult(
                context.RequestId,
                BrowserAddResult.Reject(
                    "storage_unavailable",
                    "保存目录不可用，已交给浏览器下载。"));
        }

        var metadata = new JobRequestMetadata
        {
            Referer = context.Referer,
            UserAgent = context.UserAgent,
            Cookies = context.Cookies,
            ExtraHeaders = context.ExtraHeaders,
        };

        if (isMagnet)
        {
            return await AddBrowserMagnetAsync(context, folder, metadata, cancellationToken);
        }

        var job = new DownloadJob(Guid.NewGuid().ToString("N"), context.Url.ToString(), DateTimeOffset.UtcNow)
        {
            RequestMetadata = metadata,
        };

        Track(job);

        var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(_browserProbeTimeout);

        lock (_sync)
        {
            _cancellations[job.Id] = probeCts;
        }

        try
        {
            var engine = _engines.Create();
            var probe = await engine.ProbeAsync(
                context.Url,
                CreateRequestOptions(metadata),
                probeCts.Token);

            if (IsRemoved(job.Id))
            {
                return BrowserAddResult.Reject("cancelled", "下载已被取消。");
            }

            var isMedia = IsMediaRequest(context.Url, probe);

            if (probe.TotalLength is null && !isMedia)
            {
                RemovePlaceholder(job);
                return CacheBrowserResult(
                    context.RequestId,
                    BrowserAddResult.Reject(
                        "no_length",
                        "服务器未提供文件长度，已交给浏览器下载。"));
            }

            var fileName = ChooseFileName(probe.SuggestedFileName, context.SuggestedFileName);
            if (isMedia)
            {
                fileName = ProvisionalMediaFileName(fileName, probe.FinalUri);
            }

            var layout = ReserveLayout(folder, fileName);
            await RegisterJobAsync(
                job,
                layout,
                probe.TotalLength ?? 0,
                isMedia,
                isTorrent: false,
                metadata,
                cancellationToken);

            if (IsRemoved(job.Id))
            {
                return BrowserAddResult.Reject("cancelled", "下载已被取消。");
            }

            var connections = _settings.AutomaticConnections && !isMedia && probe.TotalLength is not null
                ? DownloadConnectionPolicy.Select(probe.TotalLength.Value, probe.SupportsRanges)
                : _settings.Connections;
            Enqueue(job, connections, isResume: false);

            return CacheBrowserResult(context.RequestId, BrowserAddResult.Accept(job));
        }
        catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
        {
            CleanupAfterFailure(job);
            RemovePlaceholder(job);

            var cancelled = cancellationToken.IsCancellationRequested || IsRemoved(job.Id);

            return CacheBrowserResult(
                context.RequestId,
                cancelled
                    ? BrowserAddResult.Reject("cancelled", "下载已被取消。")
                    : BrowserAddResult.Reject(
                        "probe_timeout",
                        "探测下载地址超时，已交给浏览器下载。"));
        }
        catch (Exception ex)
        {
            CleanupAfterFailure(job);
            RemovePlaceholder(job);
            return CacheBrowserResult(context.RequestId, MapBrowserFailure(ex));
        }
        finally
        {
            RemoveProbeCancellation(job.Id, probeCts);
            probeCts.Dispose();
        }
    }

    /// <summary>
    /// Queues a magnet hand-off straight away: there is nothing to probe, and
    /// a malformed link is rejected before any row appears in the list.
    /// </summary>
    private async Task<BrowserAddResult> AddBrowserMagnetAsync(
        BrowserDownloadContext context,
        string folder,
        JobRequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        DownloadJob? job = null;

        try
        {
            var source = context.Url.OriginalString;
            TorrentSource.Validate(source);

            var layout = ReserveLayout(folder, TorrentSource.SuggestedName(source));
            job = new DownloadJob(Guid.NewGuid().ToString("N"), source, DateTimeOffset.UtcNow)
            {
                RequestMetadata = metadata,
            };
            job.SetDisplayName(TorrentSource.SuggestedName(source));

            Track(job);
            await RegisterJobAsync(
                job,
                layout,
                0,
                isMedia: false,
                isTorrent: true,
                metadata,
                cancellationToken);

            if (IsRemoved(job.Id))
            {
                return CacheBrowserResult(
                    context.RequestId,
                    BrowserAddResult.Reject("cancelled", "下载已被取消。"));
            }

            Enqueue(job, _settings.Connections, isResume: false);
            return CacheBrowserResult(context.RequestId, BrowserAddResult.Accept(job));
        }
        catch (OperationCanceledException)
        {
            return CacheBrowserResult(
                context.RequestId,
                BrowserAddResult.Reject("cancelled", "下载已被取消。"));
        }
        catch (Exception ex)
        {
            if (job is not null)
            {
                CleanupAfterFailure(job);
                _records.RemoveAll(record => record.Id == job.Id);
                RemovePlaceholder(job);
            }

            return CacheBrowserResult(context.RequestId, MapBrowserFailure(ex));
        }
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

        Enqueue(job, _settings.Connections, isResume: false, restart: true);
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(DownloadJob job, bool deletePartialData)
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

        _jobs.Remove(job);
        _byId.Remove(job.Id);
        _records.RemoveAll(record => record.Id == job.Id);

        ReleaseReservations(job);

        // Removing a job always removes its process artifacts. Unfinished
        // payloads go too; a completed file stays unless the caller explicitly
        // removes it elsewhere.
        if (deletePartialData && job.State != DownloadJobState.Completed)
        {
            DeleteTaskDirectory(job);
        }
        else
        {
            DeleteProcessArtifacts(job);
        }

        await SaveRegistryBestEffortAsync();
    }

    /// <summary>Cancels every active job and waits for the sidecars to be saved.</summary>
    public async Task PauseAllAsync(TimeSpan timeout)
    {
        List<Task> tasks;
        List<DownloadJob> queued;

        lock (_sync)
        {
            queued = _queue.Select(entry => entry.Job).ToList();
            _queue.Clear();

            foreach (var cancellation in _cancellations.Values)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            tasks = _running.Values.ToList();
        }

        foreach (var job in queued)
        {
            _ui.Post(job.MarkPaused);
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        await SaveRegistryBestEffortAsync();
    }

    public Task ResumeAllAsync()
    {
        foreach (var job in _jobs.Where(job => job.CanResume).ToArray())
        {
            ScheduleResume(job);
        }

        return Task.CompletedTask;
    }

    /// <summary>Starts queued jobs after the concurrency limit was raised.</summary>
    public void RefreshQueue() => PumpQueue();

    /// <summary>Waits for running and queued transfers; useful for tests.</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            Task[] tasks;

            lock (_sync)
            {
                if (_running.Count == 0 && _queue.Count == 0)
                {
                    return;
                }

                tasks = _running.Values.ToArray();
            }

            if (tasks.Length == 0)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return;
                }

                // Queued work with no worker can only mean a slot was freed
                // without a completion callback; pump once and re-check.
                PumpQueue();
                await Task.Yield();
                continue;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void Enqueue(DownloadJob job, int connections, bool isResume, bool restart = false)
    {
        if (connections <= 0)
        {
            connections = Math.Max(1, _settings.Connections);
        }

        lock (_sync)
        {
            _queue.RemoveAll(entry => entry.Job.Id == job.Id);
            _queue.Add(new PendingStart(job, connections, isResume, restart));
            job.PendingFreshStart = !isResume;
            job.PendingConnections = connections;
            job.StartOverOnResume = false;
        }

        _ui.Post(job.MarkQueued);
        PumpQueue();
    }

    private void PumpQueue()
    {
        lock (_sync)
        {
            // A completed worker is removed by its continuation; pruning here
            // closes the gap so a freed slot is usable immediately.
            foreach (var completed in _running
                         .Where(pair => pair.Value.IsCompleted)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _running.Remove(completed);
            }

            var limit = Math.Max(1, _settings.MaxConcurrentJobs);

            while (_queue.Count > 0 && _running.Count < limit)
            {
                var next = _queue[0];
                _queue.RemoveAt(0);

                if (_removed.Contains(next.Job.Id))
                {
                    continue;
                }

                StartLocked(next);
            }
        }
    }

    private void StartLocked(PendingStart start)
    {
        var job = start.Job;

        if (_running.TryGetValue(job.Id, out var existing) && !existing.IsCompleted)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        job.Cancellation = cancellation;
        _cancellations[job.Id] = cancellation;

        var task = Task.Run(() =>
            RunAsync(job, start.Connections, start.IsResume, start.Restart, cancellation));
        _running[job.Id] = task;

        // Never run the continuation inline. OnTransferCompleted re-enters this
        // lock and pumps the queue again, so an inline continuation would run the
        // next job's start — and its completion — on the finishing thread's stack,
        // recursing once per queued job.
        //
        // That is latent rather than active, but only by scheduling luck. Inlining
        // needs the started task to be already complete here; measured on .NET 8
        // for ExecuteSynchronously, that lands 16.3% of registrations for a
        // Task.Run(Func<Task>) whose inner task completed synchronously, 6.6% for
        // Task.Run(Action), and 0% when the inner task faults or genuinely awaits.
        // Nothing guarantees RunAsync cannot finish synchronously, so the safety
        // of this call site must not rest on it.
        _ = task.ContinueWith(
            _ => OnTransferCompleted(job, task, cancellation),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void OnTransferCompleted(
        DownloadJob job,
        Task task,
        CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (_running.TryGetValue(job.Id, out var currentTask) &&
                ReferenceEquals(currentTask, task))
            {
                _running.Remove(job.Id);
            }

            if (_cancellations.TryGetValue(job.Id, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _cancellations.Remove(job.Id);
            }
        }

        if (ReferenceEquals(job.Cancellation, cancellation))
        {
            job.Cancellation = null;
        }

        cancellation.Dispose();
        PumpQueue();
    }

    private void ScheduleResume(DownloadJob job)
    {
        if (job.IsTorrent)
        {
            // Torrent payload files and the metadata cache live in the task
            // folder, so a fresh engine can verify and continue existing pieces.
            Enqueue(job, _settings.Connections, isResume: false);
        }
        else if (job.IsMedia)
        {
            // Manifest downloads have no engine sidecar; MediaDownloader reuses
            // the fingerprinted cache inside the task's process folder instead.
            Enqueue(job, _settings.Connections, isResume: false);
        }
        else if (job.StartOverOnResume)
        {
            // The failed attempt's artifacts were deleted, so this is a new transfer.
            Enqueue(
                job,
                job.PendingConnections > 0 ? job.PendingConnections : _settings.Connections,
                isResume: false);
        }
        else if (File.Exists(job.StatePath))
        {
            Enqueue(job, _settings.Connections, isResume: true);
        }
        else if (job.PendingFreshStart)
        {
            Enqueue(job, job.PendingConnections, isResume: false);
        }
        else
        {
            job.MarkFailed("找不到状态文件，无法继续。");
        }
    }

    private async Task RunAsync(
        DownloadJob job,
        int connections,
        bool isResume,
        bool restart,
        CancellationTokenSource cancellation)
    {
        if (job.IsTorrent)
        {
            await RunTorrentAsync(job, cancellation);
            return;
        }

        var engine = _engines.Create();
        var requestOptions = CreateRequestOptions(job.RequestMetadata);
        job.PendingFreshStart = false;

        void OnProgress(object? sender, DownloadProgress progress) =>
            _ui.Post(() => job.ApplyProgress(progress));

        engine.ProgressChanged += OnProgress;

        try
        {
            _ui.Post(job.MarkDownloading);

            DownloadResult result;

            if (isResume)
            {
                var options = new ResumeOptions
                {
                    MaxConnectionsPerHost = _settings.MaxConnectionsPerHost,
                    BandwidthLimiter = _bandwidthLimiter,
                    DynamicSegmentation = _settings.DynamicSegmentation,
                    SparseWorkingFile = _settings.SparseWorkingFile,
                    RequestOptions = requestOptions,
                };

                result = await engine.ResumeAsync(job.StatePath, options, cancellation.Token);
            }
            else
            {
                var request = new DownloadRequest
                {
                    Url = new Uri(job.Url),
                    TargetPath = job.TargetPath,
                    // Keep every sidecar/working file in the task's process folder.
                    StatePath = job.StatePath,
                    ProcessDirectory = NullIfEmpty(job.ProcessDirectory),
                    Connections = connections,
                    MaxConnectionsPerHost = _settings.MaxConnectionsPerHost,
                    BandwidthLimiter = _bandwidthLimiter,
                    DynamicSegmentation = _settings.DynamicSegmentation,
                    SparseWorkingFile = _settings.SparseWorkingFile,
                    FfmpegPath = _settings.FfmpegPath,
                    RequestOptions = requestOptions,
                    // The engine deletes the old sidecar/working file inside its
                    // state lock; AppCore must not race it by deleting first.
                    Restart = restart,
                };

                result = await engine.DownloadAsync(request, cancellation.Token);
            }

            PostCompletion(job, result.TargetPath, result.TotalBytes);

            await _ui.InvokeAsync(() => SaveRegistryBestEffortAsync());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _ui.Post(job.MarkPaused);
        }
        catch (Exception ex)
        {
            CleanupAfterTransferFailure(job);
            PostFailure(job, ex.Message);
        }
        finally
        {
            engine.ProgressChanged -= OnProgress;
        }
    }

    private async Task RunTorrentAsync(DownloadJob job, CancellationTokenSource cancellation)
    {
        var downloadDirectory = job.TaskDirectory;
        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            downloadDirectory = Path.GetDirectoryName(job.TargetPath) ?? _defaultFolder();
        }

        job.PendingFreshStart = false;

        try
        {
            _ui.Post(job.MarkDownloading);

            var result = await _torrentDownloader(
                new TorrentDownloadRequest
                {
                    Source = job.Url,
                    DownloadDirectory = downloadDirectory,
                    ProcessDirectory = NullIfEmpty(job.ProcessDirectory),
                    EnableDht = true,
                    EnablePeerExchange = true,
                },
                progress => _ui.Post(() =>
                {
                    job.SetDisplayName(progress.Name);
                    job.SetTorrentChecking(progress.IsChecking);
                    job.ApplyProgress(new DownloadProgress(
                        progress.BytesDownloaded,
                        progress.TotalBytes > 0 ? progress.TotalBytes : null,
                        progress.DownloadBytesPerSecond,
                        EstimateEta(
                            progress.BytesDownloaded,
                            progress.TotalBytes,
                            progress.DownloadBytesPerSecond),
                        progress.ConnectedPeers,
                        BuildTorrentChunks(progress.BytesDownloaded, progress.TotalBytes)));
                }),
                cancellation.Token);

            // Fill the chunk bar completely; some engines stop reporting right
            // before the last byte reaches the file.
            await _ui.InvokeAsync(() =>
            {
                job.ApplyProgress(new DownloadProgress(
                    result.TotalBytes,
                    result.TotalBytes,
                    0,
                    TimeSpan.Zero,
                    0,
                    BuildTorrentChunks(result.TotalBytes, result.TotalBytes)));
                return Task.CompletedTask;
            });

            PostCompletion(job, result.TargetPath, result.TotalBytes);

            await _ui.InvokeAsync(() => SaveRegistryBestEffortAsync());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _ui.Post(job.MarkPaused);
        }
        catch (Exception ex)
        {
            // libtorrent re-hashes whatever is already on disk, so a failed torrent
            // is resumable too; keep its payload.
            CleanupAfterTransferFailure(job);
            PostFailure(job, ex.Message);
        }
    }

    private RequestOptions CreateRequestOptions(JobRequestMetadata? metadata = null) => new()
    {
        UseProxy = _settings.UseProxy,
        Referer = metadata?.Referer,
        UserAgent = string.IsNullOrWhiteSpace(metadata?.UserAgent)
            ? "PotatoDM/1.0"
            : metadata.UserAgent,
        Cookies = metadata?.Cookies,
        ExtraHeaders = metadata?.ExtraHeaders,
    };

    /// <summary>
    /// libtorrent (via csdl) exposes only an overall fraction, not a piece
    /// map, so torrents are drawn as fixed chunks filled by byte progress.
    /// </summary>
    private static IReadOnlyList<DownloadSegmentProgress>? BuildTorrentChunks(
        long downloaded,
        long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return null;
        }

        var count = (int)Math.Max(1, Math.Min(TorrentChunkCount, totalBytes));
        var baseSize = totalBytes / count;
        var remainder = totalBytes % count;
        var chunks = new DownloadSegmentProgress[count];
        var offset = 0L;

        for (var index = 0; index < count; index++)
        {
            var length = baseSize + (index < remainder ? 1 : 0);
            chunks[index] = new DownloadSegmentProgress(
                index,
                Math.Clamp(downloaded - offset, 0, length),
                length);
            offset += length;
        }

        return chunks;
    }

    private static TimeSpan? EstimateEta(long downloaded, long totalBytes, long bytesPerSecond)
    {
        if (totalBytes <= 0 || bytesPerSecond <= 0)
        {
            return null;
        }

        var remaining = totalBytes - downloaded;
        return remaining <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remaining / (double)bytesPerSecond);
    }

    private async Task<DownloadJob?> RestoreAsync(
        JobRecord record,
        CancellationToken cancellationToken)
    {
        JobState? state;

        try
        {
            state = await _stateStore.LoadAsync(record.StatePath, cancellationToken);
        }
        catch (StateException)
        {
            state = null;
        }

        var taskDirectory = !string.IsNullOrWhiteSpace(record.TaskDirectory)
            ? record.TaskDirectory
            : StatePaths.TaskDirectoryFor(record.TargetPath, record.StatePath);

        var job = new DownloadJob(record.Id, record.Url, record.CreatedUtc)
        {
            IsMedia = record.IsMedia,
            IsTorrent = record.IsTorrent,
        };
        if (record.IsTorrent && TorrentSource.IsMagnet(record.Url))
        {
            job.SetDisplayName(TorrentSource.SuggestedName(record.Url));
        }
        job.BindTarget(record.TargetPath, record.StatePath, taskDirectory);
        job.RequestMetadata = new JobRequestMetadata
        {
            Referer = record.Referer,
            UserAgent = record.UserAgent,
            Cookies = UnprotectCookies(record.ProtectedCookies),
            ExtraHeaders = record.ExtraHeaders,
        };

        if (record.IsTorrent)
        {
            job.SetHasStateFile(false);

            if (record.IsCompleted)
            {
                var payloadBytes = PayloadSize(record.TargetPath);

                if (payloadBytes > 0)
                {
                    job.MarkCompleted(payloadBytes);
                }
                else
                {
                    job.StartOverOnResume = true;
                    job.MarkFailed("下载文件已被移动或删除。");
                }

                return job;
            }

            // libtorrent preallocates the payload, so the file/directory can
            // exist while the transfer is far from done. Only the persisted
            // completion flag proves the job finished.
            job.MarkPaused();
            return job;
        }

        if (record.IsMedia)
        {
            if (File.Exists(record.TargetPath))
            {
                job.MarkCompleted(new FileInfo(record.TargetPath).Length);
                return job;
            }

            // Partially downloaded segments survive here, so the job can resume.
            if (Directory.Exists(MediaCacheDirectory(job)) ||
                Directory.Exists(record.TargetPath + MediaDownloader.CacheSuffix))
            {
                job.SetHasStateFile(false);
                job.MarkPaused();
                return job;
            }

            CleanupAfterFailure(job);
            return null;
        }

        if (state is null)
        {
            if (!File.Exists(record.TargetPath))
            {
                CleanupAfterFailure(job);
                return null;
            }

            job.MarkCompleted(new FileInfo(record.TargetPath).Length);
            return job;
        }

        if (!string.IsNullOrWhiteSpace(state.TargetFile))
        {
            job.BindTarget(state.TargetFile, record.StatePath);
        }

        job.SetHasStateFile(true);
        job.ApplyProgress(new DownloadProgress(
            state.BytesDownloaded,
            state.TotalLength,
            0,
            null,
            0,
            state.Segments
                .Select(segment => new DownloadSegmentProgress(
                    segment.Index,
                    segment.BytesDone,
                    segment.Length))
                .ToArray()));

        switch (state.Status)
        {
            case JobStatus.Completed:
                if (File.Exists(job.TargetPath))
                {
                    job.MarkCompleted(state.TotalLength);
                }
                else
                {
                    job.MarkFailed("目标文件已被移动或删除。");
                }

                break;

            case JobStatus.Failed:
                // The sidecar and partial payload outlived the failure, so the row
                // comes back resumable instead of losing its progress on restart.
                job.SetHasStateFile(true);
                job.StartOverOnResume = false;
                job.MarkFailed("上次下载失败，可以继续或重新开始。");
                break;

            default:
                // Paused, or Downloading from a session that ended without a clean save.
                job.MarkPaused();
                break;
        }

        return job;
    }

    private string? SelectBrowserFolder()
    {
        var preferred = _settings.DownloadFolder;

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try
            {
                var full = Path.GetFullPath(preferred);
                if (Directory.Exists(full) && IsDirectoryWritable(full))
                {
                    return full;
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        try
        {
            var fallback = Path.GetFullPath(_defaultFolder());
            Directory.CreateDirectory(fallback);
            return IsDirectoryWritable(fallback) ? fallback : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".potatodm-write-" + Guid.NewGuid().ToString("N") + ".tmp");

            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private JobPaths ReserveLayout(string folder, string? suggestedFileName)
    {
        var layout = JobLayout.Create(folder, suggestedFileName, IsTargetReserved);

        lock (_sync)
        {
            _reservedTargets.Add(layout.TaskDirectory);
            _reservedTargets.Add(layout.TargetPath);
        }

        return layout;
    }

    private void ReleaseReservations(DownloadJob job)
    {
        lock (_sync)
        {
            _reservedTargets.Remove(job.TargetPath);

            if (!string.IsNullOrEmpty(job.TaskDirectory))
            {
                _reservedTargets.Remove(job.TaskDirectory);
            }
        }
    }

    private bool IsTargetReserved(string path)
    {
        lock (_sync)
        {
            return _reservedTargets.Contains(path);
        }
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

    private void UpdateRecordTarget(string jobId, string targetPath)
    {
        var record = _records.FirstOrDefault(item => item.Id == jobId);
        if (record is null)
        {
            return;
        }

        lock (_sync)
        {
            _reservedTargets.Remove(record.TargetPath);
            _reservedTargets.Add(targetPath);
        }

        record.TargetPath = targetPath;
    }

    private static string? ChooseFileName(string? probeName, string? browserName)
    {
        if (!string.IsNullOrWhiteSpace(probeName) &&
            !probeName.Equals("download.bin", StringComparison.OrdinalIgnoreCase))
        {
            return probeName;
        }

        return string.IsNullOrWhiteSpace(browserName) ? probeName : browserName;
    }

    private string? ProtectCookies(IReadOnlyList<RequestCookie>? cookies)
    {
        if (cookies is null || cookies.Count == 0)
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(cookies, CookieJsonOptions);
            return _cookieProtector.Protect(json);
        }
        catch (Exception)
        {
            // Losing persisted cookies is better than failing the download.
            return null;
        }
    }

    private IReadOnlyList<RequestCookie>? UnprotectCookies(string? protectedCookies)
    {
        if (string.IsNullOrWhiteSpace(protectedCookies))
        {
            return null;
        }

        try
        {
            var json = _cookieProtector.Unprotect(protectedCookies);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<List<RequestCookie>>(json, CookieJsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BrowserAddResult MapBrowserFailure(Exception exception) => exception switch
    {
        DrmProtectedException => BrowserAddResult.Reject(
            "drm_protected",
            "该媒体受 DRM 保护；土豆下载器不会绕过内容保护。"),
        MediaManifestException => BrowserAddResult.Reject(
            "media_unsupported",
            "无法解析或合并不受支持的媒体清单。"),
        MediaMergeException => BrowserAddResult.Reject(
            "media_merge_unavailable",
            "合并分离的音视频轨需要 ffmpeg，请先安装或在设置中指定路径。"),
        TorrentException => BrowserAddResult.Reject(
            "unsupported_torrent",
            "磁力链接无效或暂不受支持。"),
        UnsupportedContentEncodingException => BrowserAddResult.Reject(
            "unsupported_encoding",
            "服务器返回了不支持的压缩格式，已交给浏览器下载。"),
        HttpDownloadException { IsTransient: true } => BrowserAddResult.Reject(
            "probe_failed",
            "探测下载地址失败，已交给浏览器下载。"),
        HttpDownloadException => BrowserAddResult.Reject(
            "http_error",
            "服务器拒绝了下载请求，已交给浏览器下载。"),
        ProbeException => BrowserAddResult.Reject(
            "probe_failed",
            "探测下载地址失败，已交给浏览器下载。"),
        FileAccessException or DiskSpaceException or UnauthorizedAccessException =>
            BrowserAddResult.Reject(
                "storage_unavailable",
                "保存目录不可用，已交给浏览器下载。"),
        IOException => BrowserAddResult.Reject(
            "probe_failed",
            "网络请求失败，已交给浏览器下载。"),
        _ => BrowserAddResult.Reject(
            "internal_error",
            "客户端处理下载请求失败，已交给浏览器下载。"),
    };

    private BrowserAddResult? TryGetCachedBrowserResult(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        lock (_sync)
        {
            if (!_browserResults.TryGetValue(requestId, out var entry))
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - entry.At <= BrowserResultLifetime)
            {
                return entry.Result;
            }

            _browserResults.Remove(requestId);
            return null;
        }
    }

    private BrowserAddResult CacheBrowserResult(string? requestId, BrowserAddResult result)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return result;
        }

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var expired in _browserResults
                         .Where(pair => now - pair.Value.At > BrowserResultLifetime)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _browserResults.Remove(expired);
            }

            if (_browserResults.Count >= BrowserResultLimit)
            {
                _browserResults.Clear();
            }

            _browserResults[requestId] = (result, now);
        }

        return result;
    }

    private void RemovePlaceholder(DownloadJob job)
    {
        _jobs.Remove(job);
        _byId.Remove(job.Id);
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

    private async Task WaitForJobAsync(string id, TimeSpan timeout)
    {
        Task? task;

        lock (_sync)
        {
            task = _running.GetValueOrDefault(id);
        }

        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsRemoved(string id)
    {
        lock (_sync)
        {
            return _removed.Contains(id);
        }
    }

    private async Task SaveRegistryBestEffortAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _registry.SaveAsync(_records.ToList(), cancellationToken);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string MediaCacheDirectory(DownloadJob job) =>
        string.IsNullOrEmpty(job.TaskDirectory)
            ? job.TargetPath + MediaDownloader.CacheSuffix
            : Path.Combine(
                job.TaskDirectory,
                StatePaths.ProcessDirectoryName,
                StatePaths.MediaCacheDirectoryName);

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Removes the process folder but keeps the finished file (if any). Used
    /// when a completed task is removed from the list.
    /// </summary>
    private static void DeleteProcessArtifacts(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.TaskDirectory))
        {
            DeleteDirectory(Path.Combine(job.TaskDirectory, StatePaths.ProcessDirectoryName));
            DeleteDirectoryIfEmpty(job.TaskDirectory);
            return;
        }

        DeleteLegacyArtifacts(job);
    }

    /// <summary>Removes the whole task folder, including any partial payload.</summary>
    private static void DeleteTaskDirectory(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.TaskDirectory))
        {
            DeleteDirectory(job.TaskDirectory);
            return;
        }

        DeleteLegacyArtifacts(job);
    }

    /// <summary>
    /// Removes every artifact for a job that never produced resumable state —
    /// probe and registration failures, and records whose files are already gone.
    /// Transfer failures use <see cref="CleanupAfterTransferFailure"/> instead.
    /// </summary>
    private static void CleanupAfterFailure(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.TaskDirectory))
        {
            DeleteDirectory(job.TaskDirectory);
        }
        else
        {
            DeleteLegacyArtifacts(job);
        }

        job.StartOverOnResume = true;
    }

    /// <summary>
    /// A failed transfer keeps everything it can be resumed from: the sidecar, its
    /// backup and the partial payload. Throwing away a multi-gigabyte transfer that
    /// died at 95% is the most expensive thing this app can do to a user, and the
    /// engine already knows how to continue from a failed job. Only per-attempt
    /// scratch files go.
    /// </summary>
    private static void CleanupAfterTransferFailure(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.StatePath))
        {
            // Torn write from a crash mid-save; the real sidecar and its .bak are
            // authoritative and stay.
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".tmp");
        }

        job.StartOverOnResume = false;
    }

    /// <summary>Cleanup for records written before the per-task folder layout.</summary>
    private static void DeleteLegacyArtifacts(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.StatePath))
        {
            FileSystemHelpers.TryDeleteFile(job.StatePath);
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".bak");
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".tmp");
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".lock");
        }

        if (string.IsNullOrEmpty(job.TargetPath))
        {
            return;
        }

        FileSystemHelpers.TryDeleteFile(StatePaths.WorkingFor(job.TargetPath));

        if (job.IsMedia)
        {
            DeleteDirectory(job.TargetPath + MediaDownloader.CacheSuffix);
        }
        else if (job.IsTorrent)
        {
            // Torrent payload files are written directly; the metadata cache
            // was shared by every torrent in the folder in the old layout.
            if (Directory.Exists(job.TargetPath))
            {
                DeleteDirectory(job.TargetPath);
            }
            else
            {
                FileSystemHelpers.TryDeleteFile(job.TargetPath);
            }
        }
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (
                attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                // A handle may still be closing right after cancellation.
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private void Track(DownloadJob job)
    {
        _jobs.Add(job);
        _byId[job.Id] = job;
    }

    private async Task RegisterJobAsync(
        DownloadJob job,
        JobPaths layout,
        long totalBytes,
        bool isMedia,
        bool isTorrent,
        JobRequestMetadata? metadata,
        CancellationToken cancellationToken)
    {
        job.IsMedia = isMedia;
        job.IsTorrent = isTorrent;
        job.ResolveTarget(layout.TargetPath, layout.StatePath, totalBytes, layout.TaskDirectory);

        _records.Add(new JobRecord
        {
            Id = job.Id,
            Url = job.Url,
            TargetPath = layout.TargetPath,
            StatePath = layout.StatePath,
            TaskDirectory = layout.TaskDirectory,
            IsMedia = isMedia,
            IsTorrent = isTorrent,
            CreatedUtc = job.CreatedUtc,
            Referer = metadata?.Referer,
            UserAgent = metadata?.UserAgent,
            ProtectedCookies = ProtectCookies(metadata?.Cookies),
            ExtraHeaders = metadata?.ExtraHeaders is null
                ? null
                : new Dictionary<string, string>(metadata.ExtraHeaders),
        });

        await SaveRegistryBestEffortAsync(cancellationToken);
    }

    private void PostCompletion(DownloadJob job, string? targetPath, long totalBytes)
    {
        _ui.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(targetPath) &&
                !string.Equals(job.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                job.BindTarget(targetPath, job.StatePath);
                UpdateRecordTarget(job.Id, targetPath);
            }

            MarkRecordCompleted(job.Id);
            job.MarkCompleted(totalBytes);
        });
    }

    private void MarkRecordCompleted(string jobId)
    {
        var record = _records.FirstOrDefault(item => item.Id == jobId);

        if (record is not null)
        {
            record.IsCompleted = true;
        }
    }

    private static long PayloadSize(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (Directory.Exists(path))
            {
                return Directory
                    .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException)
        {
        }

        return 0;
    }

    private void PostFailure(DownloadJob job, string message)
    {
        // A failed transfer keeps its sidecar, so report what is actually on disk.
        // Hardcoding true would claim a sidecar for torrent/media jobs, which never
        // write one; hardcoding false would silently downgrade "resume" to "start
        // over" while still appearing to work.
        var hasStateFile = !string.IsNullOrEmpty(job.StatePath) && File.Exists(job.StatePath);

        _ui.Post(() =>
        {
            job.SetHasStateFile(hasStateFile);
            job.StartOverOnResume = false;
            job.MarkFailed(message);
        });
    }

    private readonly record struct PendingStart(
        DownloadJob Job,
        int Connections,
        bool IsResume,
        // Carried here rather than on the job: Enqueue clears StartOverOnResume
        // on the way in, so a job field could not survive the trip.
        bool Restart = false);
}
