using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Models;

/// <summary>Options for resuming an existing job.</summary>
public sealed class ResumeOptions
{
    public bool RestartOnChange { get; init; }

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxRetries { get; init; } = 5;

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxConnectionsPerHost { get; init; } = 8;

    /// <summary>
    /// Ceiling on the connections this one job may hold at the same time,
    /// regardless of how the job was segmented. It is a per-job cap, not a
    /// process-wide one; the process-wide knob only limits how many jobs run
    /// at once, not how many connections they open.
    /// </summary>
    public int MaxConnectionsPerJob { get; init; } = 64;

    /// <summary>
    /// Let a connection that finishes early take over half of another in-flight
    /// range. Off by default because it makes the segment map less predictable.
    /// </summary>
    public bool DynamicSegmentation { get; init; }

    /// <summary>
    /// Mark the working file sparse while resuming. Existing zeros in ranges
    /// that were never written become holes; already-written bytes are kept.
    /// </summary>
    public bool SparseWorkingFile { get; init; }

    /// <summary>
    /// Optional shared pacer. Pass the same instance to every concurrent job to
    /// enforce one process-wide bandwidth cap.
    /// </summary>
    public IBandwidthLimiter? BandwidthLimiter { get; init; }

    public RequestOptions RequestOptions { get; init; } = new();
}
