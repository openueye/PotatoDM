using System.Diagnostics;
using System.Runtime.ExceptionServices;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Internal;
using PotatoDM.Core.Models;
using PotatoDM.Core.Planning;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Transfer;

public sealed partial class DownloadEngine
{
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
        catch (Exception ex)
        {
            // Best effort: the transfer keeps running because a stale snapshot only
            // costs re-downloaded bytes after a crash. Logged at Warning because a
            // sidecar that silently stops advancing looks exactly like a healthy
            // download until the user closes the app and loses the progress.
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.Warning(
                    $"Could not write the progress snapshot for '{runtime.StatePath}'; " +
                    "progress since the last successful save may be re-downloaded.",
                    ex);
            }
        }
    }
}
