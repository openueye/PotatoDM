using System.Diagnostics;
using System.Runtime.ExceptionServices;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Internal;
using PotatoDM.Core.Media;
using PotatoDM.Core.Models;
using PotatoDM.Core.Planning;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Transfer;

public sealed class DownloadEngine : IDownloadEngine
{
    private const long DefaultMinSegmentSize = 1024 * 1024;
    private const long DefaultMinBytesForSegmented = 2 * 1024 * 1024;

    private readonly IDownloadStateStore _store;
    private readonly SegmentPlanner _planner;
    private readonly UrlProbe _probe;

    public DownloadEngine(
        IDownloadStateStore? store = null,
        SegmentPlanner? planner = null,
        UrlProbe? probe = null)
    {
        _store = store ?? new JsonDownloadStateStore();
        _planner = planner ?? new SegmentPlanner();
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

        using var stateLock = StateLock.Acquire(statePath);

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

        var segments = _planner.Plan(
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
            request.MaxGlobalConnections,
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

        var result = await new MediaDownloader()
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
        using var stateLock = StateLock.Acquire(statePath);

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
            state.Segments = _planner.Plan(
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
            options.MaxGlobalConnections,
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

    private async Task<DownloadResult> RunJobAsync(
        JobRuntime runtime,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await RunWorkersWithFallbackAsync(runtime, cancellationToken).ConfigureAwait(false);

            await runtime.File
                .CompleteAsync(runtime.TargetPath, CancellationToken.None)
                .ConfigureAwait(false);

            runtime.State.Status = JobStatus.Completed;
            await SaveStateBestEffortAsync(runtime).ConfigureAwait(false);

            RaiseProgress(new DownloadProgress(
                runtime.State.TotalLength,
                runtime.State.TotalLength,
                0,
                TimeSpan.Zero,
                0,
                runtime.SnapshotSegmentProgresses()));
            RaiseStateChanged(JobStatus.Completed);

            return new DownloadResult(
                runtime.TargetPath,
                runtime.State.TotalLength,
                runtime.State.Mode,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runtime.State.Status = JobStatus.Paused;
            await SaveStateBestEffortAsync(runtime).ConfigureAwait(false);
            RaiseStateChanged(JobStatus.Paused);
            throw;
        }
        catch (Exception)
        {
            runtime.State.Status = JobStatus.Failed;
            await SaveStateBestEffortAsync(runtime).ConfigureAwait(false);
            RaiseStateChanged(JobStatus.Failed);
            throw;
        }
    }

    private async Task RunWorkersWithFallbackAsync(
        JobRuntime runtime,
        CancellationToken cancellationToken)
    {
        var fallbackUsed = false;

        while (true)
        {
            try
            {
                await RunWorkersAsync(runtime, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RangeIgnoredException) when (!fallbackUsed)
            {
                fallbackUsed = true;
                await SwitchToSingleStreamAsync(runtime).ConfigureAwait(false);
            }
        }
    }

    private async Task RunWorkersAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        // A previous pass (e.g. the range-ignored fallback) must not leak its failure.
        runtime.ClearFatal();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var monitorTask = MonitorAsync(runtime, stop.Task, linked.Token);
        var workers = runtime.State.Segments
            .ToArray()
            .Select(segment => Task.Run(
                () => RunWorkerAsync(runtime, segment, linked.Token),
                linked.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            linked.Cancel();
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch
            {
                // Sibling cancellation is expected; the recorded fatal error is authoritative.
            }

            stop.TrySetResult();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Monitor stopped because the job is failing.
            }

            var fatal = runtime.FatalError;
            if (fatal is not null)
            {
                ExceptionDispatchInfo.Capture(fatal).Throw();
            }

            throw;
        }

        stop.TrySetResult();
        await monitorTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one connection. When dynamic segmentation is enabled, a worker that
    /// finishes its range takes half of the largest range still in flight
    /// instead of idling, so one slow response cannot hold up the whole job.
    /// </summary>
    private static async Task RunWorkerAsync(
        JobRuntime runtime,
        DownloadSegment first,
        CancellationToken cancellationToken)
    {
        var segment = first;

        while (true)
        {
            await SegmentWorker
                .RunAsync(runtime, segment, cancellationToken)
                .ConfigureAwait(false);

            if (!runtime.DynamicSegmentation)
            {
                return;
            }

            var helper = TrySteal(runtime);
            if (helper is null)
            {
                return;
            }

            segment = helper;
        }
    }

    private static DownloadSegment? TrySteal(JobRuntime runtime)
    {
        lock (runtime.SegmentsLock)
        {
            DownloadSegment? candidate = null;
            var bestRemaining = 0L;

            foreach (var segment in runtime.State.Segments)
            {
                if (segment.IsComplete)
                {
                    continue;
                }

                if (segment.BytesDone == 0)
                {
                    // This range still has a worker waiting for a connection
                    // permit. Splitting it would only add requests, not speed.
                    return null;
                }

                if (segment.Remaining > bestRemaining)
                {
                    bestRemaining = segment.Remaining;
                    candidate = segment;
                }
            }

            if (candidate is null || bestRemaining < 2 * runtime.MinSegmentSize)
            {
                return null;
            }

            var helper = SegmentSplitter.TrySplit(
                candidate,
                runtime.MinSegmentSize,
                runtime.TakeNextSegmentIndex());

            if (helper is null)
            {
                return null;
            }

            runtime.State.Segments.Add(helper);
            return helper;
        }
    }

    private async Task MonitorAsync(JobRuntime runtime, Task stop, CancellationToken cancellationToken)
    {
        var meter = new SpeedMeter();
        var lastSave = DateTimeOffset.UtcNow;

        while (!stop.IsCompleted)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stop.IsCompleted)
            {
                break;
            }

            var downloaded = runtime.ReadBytesDownloaded();
            var now = DateTimeOffset.UtcNow;
            meter.Add(downloaded, now);

            var speed = meter.BytesPerSecond;
            var remaining = runtime.State.TotalLength - downloaded;
            TimeSpan? eta = remaining <= 0
                ? TimeSpan.Zero
                : speed > 1
                    ? TimeSpan.FromSeconds(remaining / speed)
                    : null;

            RaiseProgress(new DownloadProgress(
                downloaded,
                runtime.State.TotalLength,
                speed,
                eta,
                Volatile.Read(ref runtime.ActiveConnections),
                runtime.SnapshotSegmentProgresses()));

            if (now - lastSave >= TimeSpan.FromSeconds(1))
            {
                lastSave = now;
                await SaveStateBestEffortAsync(runtime).ConfigureAwait(false);
            }
        }
    }

    private static async Task SwitchToSingleStreamAsync(JobRuntime runtime)
    {
        await runtime.File.DisposeAsync().ConfigureAwait(false);
        DeleteFileIfExists(runtime.State.WorkingFile);

        runtime.State.Segments.Clear();
        runtime.State.Segments.Add(new DownloadSegment
        {
            Index = 0,
            Start = 0,
            EndExclusive = runtime.State.TotalLength,
        });
        runtime.State.Mode = DownloadMode.SingleStream;
        runtime.Probe = runtime.Probe with { SupportsRanges = false };
        runtime.File = RandomAccessOutputFile.Create(
            runtime.State.WorkingFile,
            runtime.State.TotalLength,
            runtime.SparseWorkingFile);
    }

    private async Task SaveStateBestEffortAsync(JobRuntime runtime)
    {
        try
        {
            // Freeze the counters (and the segment map) first, then flush. Every
            // byte count in the snapshot was written before the flush, so the
            // sidecar can never claim data that a power loss would drop.
            var snapshot = runtime.SnapshotState();
            runtime.File.FlushToDisk();
            await _store
                .SaveAsync(runtime.StatePath, snapshot, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Progress snapshots are best effort; the failure path reports the real error.
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
        catch
        {
            // A faulty UI subscriber must never break the transfer.
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
        catch
        {
        }
    }

    private static long RequireKnownLength(ProbeResult probe)
    {
        if (probe.TotalLength is not { } total || total < 0)
        {
            throw new ProbeException(
                "The server did not provide Content-Length; a known resource length is required.");
        }

        return total;
    }

    private static bool ValidatorsMatch(JobState state, ProbeResult probe)
    {
        if (probe.TotalLength is { } total && total != state.TotalLength)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(state.ETag) && !string.IsNullOrEmpty(probe.ETag))
        {
            return string.Equals(state.ETag, probe.ETag, StringComparison.Ordinal);
        }

        if (state.LastModified is not null && probe.LastModified is { } lastModified)
        {
            return string.Equals(
                state.LastModified,
                lastModified.ToString("O"),
                StringComparison.Ordinal);
        }

        // No comparable validator on both sides; the length check above is the best available test.
        return true;
    }

    private static void ValidateSegments(JobState state)
    {
        if (state.TotalLength < 0)
        {
            throw new StateException("State contains a negative total length.");
        }

        if (state.Segments.Count == 0)
        {
            throw new StateException("State contains no segments.");
        }

        var expectedStart = 0L;

        // Dynamic splits append helper ranges with higher indices; offset order
        // is what defines coverage.
        foreach (var segment in state.Segments.OrderBy(s => s.Start))
        {
            if (segment.Start != expectedStart ||
                segment.EndExclusive < segment.Start ||
                segment.BytesDone < 0 ||
                segment.BytesDone > segment.Length)
            {
                throw new StateException("State contains an invalid segment map.");
            }

            expectedStart = segment.EndExclusive;
        }

        if (expectedStart != state.TotalLength)
        {
            throw new StateException(
                $"Segment map covers {expectedStart} bytes but the state length is {state.TotalLength}.");
        }
    }

    private static void ValidateRequest(DownloadRequest request)
    {
        if (request.Url is null)
        {
            throw new ArgumentException("A URL is required.", nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.Connections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MinSegmentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxConnectionsPerHost, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxGlobalConnections, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.MaxRetries);

        if (request.StallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.StallTimeout), "Stall timeout must be positive.");
        }
    }

    private static void ValidateResumeOptions(ResumeOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConnectionsPerHost, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxGlobalConnections, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);

        if (options.StallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.StallTimeout), "Stall timeout must be positive.");
        }
    }

    private static void EnsureDiskSpace(string targetPath, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new DiskSpaceException(
                    $"Not enough free space on '{drive.Name}': need {requiredBytes:N0} bytes, " +
                    $"{drive.AvailableFreeSpace:N0} available.");
            }
        }
        catch (ArgumentException)
        {
            // Unusual path form; preallocation will report the real problem.
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
