using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Media;
using PotatoDM.Core.Models;
using PotatoDM.Core.Planning;
using PotatoDM.Core.Storage;

using static PotatoDM.Core.Transfer.DownloadValidation;

namespace PotatoDM.Core.Transfer;

public sealed partial class DownloadEngine : IDownloadEngine
{
    private const long DefaultMinSegmentSize = 1024 * 1024;
    private const long DefaultMinBytesForSegmented = 2 * 1024 * 1024;

    private readonly IDownloadStateStore _store;
    private readonly UrlProbe _probe;
    private readonly ILogSink _log;

    /// <param name="store">Sidecar store; defaults to a JSON store on disk.</param>
    /// <param name="probe">URL probe.</param>
    /// <param name="log">
    /// Optional sink, appended last so every existing call site keeps compiling.
    /// Defaults to <see cref="NullLogSink"/>.
    /// </param>
    public DownloadEngine(
        IDownloadStateStore? store = null,
        UrlProbe? probe = null,
        ILogSink? log = null)
    {
        _log = log ?? NullLogSink.Instance;
        _store = store ?? new JsonDownloadStateStore(_log);
        _probe = probe ?? new UrlProbe();
    }

    public event EventHandler<DownloadProgress>? ProgressChanged;

    public event EventHandler<JobStatus>? StateChanged;

    public async Task<ProbeResult> ProbeAsync(
        Uri url,
        RequestOptions options,
        CancellationToken cancellationToken = default)
    {
        using var client = DownloadHttpClientFactory.Create(options);
        return await _probe.ProbeAsync(client, url, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var requestOptions = request.RequestOptions;
        using var client = DownloadHttpClientFactory.Create(requestOptions);
        var probe = await _probe
            .ProbeAsync(client, request.Url, requestOptions, cancellationToken)
            .ConfigureAwait(false);

        var manifestKind = ManifestDetector.FromUrl(probe.FinalUri)
                           ?? ManifestDetector.FromUrl(request.Url)
                           ?? ManifestDetector.FromContentType(probe.ContentType);

        if (manifestKind is { } kind)
        {
            return await DownloadMediaAsync(kind, client, request, probe, cancellationToken)
                .ConfigureAwait(false);
        }

        var totalLength = RequireKnownLength(probe);

        var targetPath = TargetPathResolver.Resolve(request.TargetPath, probe.SuggestedFileName);
        var processDirectory = string.IsNullOrWhiteSpace(request.ProcessDirectory)
            ? null
            : Path.GetFullPath(request.ProcessDirectory);
        var statePath = string.IsNullOrWhiteSpace(request.StatePath)
            ? processDirectory is null
                ? StatePaths.StateFor(targetPath)
                : StatePaths.StateIn(processDirectory, targetPath)
            : Path.GetFullPath(request.StatePath);
        var workingPath = processDirectory is null
            ? StatePaths.WorkingFor(targetPath)
            : StatePaths.WorkingIn(processDirectory, targetPath);

        using var stateLock = StateLock.Acquire(statePath, _log);

        if (request.Restart)
        {
            DeleteArtifacts(statePath, workingPath);
        }
        else if (File.Exists(statePath))
        {
            throw new StateException(
                $"A saved job already exists at '{statePath}'. Resume it, or restart with --restart.");
        }

        if (totalLength == 0)
        {
            EnsureParentDirectory(targetPath);
            await File.WriteAllBytesAsync(targetPath, Array.Empty<byte>(), cancellationToken)
                .ConfigureAwait(false);
            RaiseStateChanged(JobStatus.Completed);
            return new DownloadResult(targetPath, 0, DownloadMode.SingleStream, TimeSpan.Zero);
        }

        EnsureDiskSpace(targetPath, totalLength);

        var segments = SegmentPlanner.Plan(
            totalLength,
            probe.SupportsRanges,
            request.Connections,
            request.MinSegmentSize,
            request.MinBytesForSegmented);

        var now = DateTimeOffset.UtcNow;
        var state = new JobState
        {
            SchemaVersion = JobState.CurrentSchemaVersion,
            Url = request.Url.ToString(),
            FinalUrl = probe.FinalUri.ToString(),
            ETag = probe.ETag,
            LastModified = probe.LastModified?.ToString("O"),
            TotalLength = totalLength,
            Mode = segments.Count > 1 ? DownloadMode.Segmented : DownloadMode.SingleStream,
            TargetFile = targetPath,
            WorkingFile = workingPath,
            Segments = segments.ToList(),
            Status = JobStatus.Downloading,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        using var limiter = new ConnectionLimiter(
            request.MaxConnectionsPerJob,
            request.MaxConnectionsPerHost);

        var runtime = new JobRuntime
        {
            State = state,
            Probe = probe,
            FinalUri = probe.FinalUri,
            File = RandomAccessOutputFile.Create(workingPath, totalLength, request.SparseWorkingFile),
            SparseWorkingFile = request.SparseWorkingFile,
            RequestOptions = requestOptions,
            Client = client,
            Limiter = limiter,
            BandwidthLimiter = request.BandwidthLimiter,
            Retry = new RetryPolicy(request.MaxRetries, request.MaxRetryDelay),
            StallTimeout = request.StallTimeout,
            StatePath = statePath,
            TargetPath = targetPath,
            Store = _store,
            DynamicSegmentation = request.DynamicSegmentation,
            MinSegmentSize = request.MinSegmentSize,
            NextSegmentIndex = segments.Max(segment => segment.Index),
        };

        try
        {
            // Persist the initial snapshot before any bytes move, so a crash is always resumable.
            await _store.SaveAsync(statePath, state, cancellationToken).ConfigureAwait(false);
            RaiseStateChanged(JobStatus.Downloading);
            return await RunJobAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.File.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<DownloadResult> DownloadMediaAsync(
        ManifestKind kind,
        HttpClient client,
        DownloadRequest request,
        ProbeResult probe,
        CancellationToken cancellationToken)
    {
        RaiseStateChanged(JobStatus.Downloading);

        var result = await MediaDownloader
            .DownloadAsync(client, request, kind, probe, RaiseProgress, cancellationToken)
            .ConfigureAwait(false);

        RaiseStateChanged(JobStatus.Completed);
        return result;
    }

    public async Task<DownloadResult> ResumeAsync(
        string stateOrWorkingPath,
        ResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateResumeOptions(options);

        var statePath = StatePaths.ResolveStatePath(stateOrWorkingPath);
        using var stateLock = StateLock.Acquire(statePath, _log);

        var state = await _store.LoadAsync(statePath, cancellationToken).ConfigureAwait(false)
                    ?? throw new StateException($"No state file found at '{statePath}'.");

        var targetPath = state.TargetFile;
        var workingPath = state.WorkingFile;

        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(workingPath))
        {
            throw new StateException($"State file '{statePath}' is missing file paths.");
        }

        ValidateSegments(state);

        if (state.Status == JobStatus.Completed &&
            File.Exists(targetPath) &&
            !File.Exists(workingPath))
        {
            return new DownloadResult(targetPath, state.TotalLength, state.Mode, TimeSpan.Zero);
        }

        if (File.Exists(workingPath) && File.Exists(targetPath))
        {
            throw new StateException(
                $"Both the working file and the target file exist for '{targetPath}'. " +
                "Remove the stale file before resuming.");
        }

        if (!File.Exists(workingPath) &&
            File.Exists(targetPath) &&
            new FileInfo(targetPath).Length == state.TotalLength)
        {
            // The rename succeeded but the final state update was lost.
            state.Status = JobStatus.Completed;
            await _store.SaveAsync(statePath, state, cancellationToken).ConfigureAwait(false);
            RaiseStateChanged(JobStatus.Completed);
            return new DownloadResult(targetPath, state.TotalLength, state.Mode, TimeSpan.Zero);
        }

        if (!Uri.TryCreate(state.Url, UriKind.Absolute, out var url))
        {
            throw new StateException($"State file contains an invalid URL '{state.Url}'.");
        }

        var requestOptions = options.RequestOptions;
        using var client = DownloadHttpClientFactory.Create(requestOptions);
        var probe = await _probe
            .ProbeAsync(client, url, requestOptions, cancellationToken)
            .ConfigureAwait(false);
        var totalLength = RequireKnownLength(probe);

        var mismatch = totalLength != state.TotalLength || !ValidatorsMatch(state, probe);
        var rangesLost = !probe.SupportsRanges &&
                         (state.BytesDownloaded > 0 || state.Segments.Count > 1);

        if (mismatch && !options.RestartOnChange)
        {
            throw new ValidatorMismatchException(
                "The remote resource changed since this job was created. " +
                "Restart the download to fetch the new version.");
        }

        RandomAccessOutputFile file;

        if (mismatch || rangesLost)
        {
            DeleteFileIfExists(workingPath);

            var segmentCount = Math.Max(1, state.Segments.Count);
            state.Segments = SegmentPlanner.Plan(
                    totalLength,
                    probe.SupportsRanges,
                    segmentCount,
                    DefaultMinSegmentSize,
                    DefaultMinBytesForSegmented)
                .ToList();
            state.Mode = state.Segments.Count > 1 ? DownloadMode.Segmented : DownloadMode.SingleStream;
            state.TotalLength = totalLength;
            state.ETag = probe.ETag;
            state.LastModified = probe.LastModified?.ToString("O");
            state.FinalUrl = probe.FinalUri.ToString();

            file = RandomAccessOutputFile.Create(workingPath, totalLength, options.SparseWorkingFile);
        }
        else
        {
            file = RandomAccessOutputFile.OpenExisting(
                workingPath,
                totalLength,
                options.SparseWorkingFile);
        }

        state.Status = JobStatus.Downloading;

        using var limiter = new ConnectionLimiter(
            options.MaxConnectionsPerJob,
            options.MaxConnectionsPerHost);

        var runtime = new JobRuntime
        {
            State = state,
            Probe = probe,
            FinalUri = probe.FinalUri,
            File = file,
            RequestOptions = requestOptions,
            Client = client,
            Limiter = limiter,
            BandwidthLimiter = options.BandwidthLimiter,
            Retry = new RetryPolicy(options.MaxRetries, options.MaxRetryDelay),
            StallTimeout = options.StallTimeout,
            StatePath = statePath,
            TargetPath = targetPath,
            Store = _store,
            DynamicSegmentation = options.DynamicSegmentation,
            SparseWorkingFile = options.SparseWorkingFile,
            MinSegmentSize = DefaultMinSegmentSize,
            NextSegmentIndex = state.Segments.Max(segment => segment.Index),
        };

        try
        {
            await _store.SaveAsync(statePath, state, cancellationToken).ConfigureAwait(false);
            RaiseStateChanged(JobStatus.Downloading);
            return await RunJobAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.File.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RaiseProgress(DownloadProgress progress)
    {
        var handler = ProgressChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, progress);
        }
        catch (Exception ex)
        {
            // A faulty UI subscriber must never break the transfer. Debug, not
            // Warning: progress fires four times a second, so a permanently broken
            // subscriber would otherwise flood the log.
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.Debug("A ProgressChanged subscriber threw; the transfer continues.", ex);
            }
        }
    }

    private void RaiseStateChanged(JobStatus status)
    {
        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, status);
        }
        catch (Exception ex)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.Debug($"A StateChanged subscriber threw for status {status}.", ex);
            }
        }
    }

    private static void DeleteArtifacts(string statePath, string workingPath)
    {
        DeleteFileIfExists(statePath);
        DeleteFileIfExists(statePath + ".bak");
        DeleteFileIfExists(statePath + ".tmp");
        DeleteFileIfExists(workingPath);
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileAccessException($"Could not delete '{path}'.", ex);
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
