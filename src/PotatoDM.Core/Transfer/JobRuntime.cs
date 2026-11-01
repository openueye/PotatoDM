using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Transfer;

/// <summary>Shared mutable context for the workers of one job.</summary>
internal sealed class JobRuntime
{
    public required JobState State { get; init; }

    public required ProbeResult Probe { get; set; }

    public required Uri FinalUri { get; init; }

    public required RandomAccessOutputFile File { get; set; }

    public required RequestOptions RequestOptions { get; init; }

    public required HttpClient Client { get; init; }

    public required ConnectionLimiter Limiter { get; init; }

    /// <summary>Shared pacer; null means no bandwidth cap.</summary>
    public required IBandwidthLimiter? BandwidthLimiter { get; init; }

    public required RetryPolicy Retry { get; init; }

    public required TimeSpan StallTimeout { get; init; }

    public required string StatePath { get; init; }

    public required string TargetPath { get; init; }

    public required IDownloadStateStore Store { get; init; }

    /// <summary>
    /// When true, a connection that finishes early takes over half of another
    /// in-flight range that is still large. Off by default because it changes
    /// the deterministic segment map.
    /// </summary>
    public bool DynamicSegmentation { get; init; }

    /// <summary>Keep the working file sparse when replacing its handle.</summary>
    public bool SparseWorkingFile { get; init; }

    /// <summary>Smallest range the planner, and therefore a split, may create.</summary>
    public long MinSegmentSize { get; init; } = 1024 * 1024;

    /// <summary>Highest segment index handed out so far; new helpers take the next one.</summary>
    public int NextSegmentIndex
    {
        get => Volatile.Read(ref _nextSegmentIndex);
        init => _nextSegmentIndex = value;
    }

    private int _nextSegmentIndex;

    /// <summary>Guards the segment list against concurrent splitting and snapshots.</summary>
    public object SegmentsLock { get; } = new();

    public int TakeNextSegmentIndex() => Interlocked.Increment(ref _nextSegmentIndex);

    public JobState SnapshotState()
    {
        lock (SegmentsLock)
        {
            return State.Snapshot();
        }
    }

    public long ReadBytesDownloaded()
    {
        lock (SegmentsLock)
        {
            return State.BytesDownloaded;
        }
    }

    /// <summary>Freezes the live range counters for a UI progress notification.</summary>
    public IReadOnlyList<DownloadSegmentProgress> SnapshotSegmentProgresses()
    {
        lock (SegmentsLock)
        {
            return State.Segments
                .Select(segment => new DownloadSegmentProgress(
                    segment.Index,
                    segment.BytesDone,
                    segment.Length))
                .ToArray();
        }
    }

    public int ActiveConnections;

    private Exception? _fatalError;

    public Exception? FatalError => Volatile.Read(ref _fatalError);

    public void RecordFatal(Exception exception)
    {
        Interlocked.CompareExchange(ref _fatalError, exception, null);
    }

    public void ClearFatal() => Interlocked.Exchange(ref _fatalError, null);
}
