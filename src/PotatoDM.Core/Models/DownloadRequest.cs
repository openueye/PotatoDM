using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Models;

/// <summary>Options for starting a brand-new download job.</summary>
public sealed class DownloadRequest
{
    public required Uri Url { get; init; }

    /// <summary>
    /// Target file path or directory. When a directory is given, the file name
    /// suggested by the server is used. When null, the current directory is used.
    /// </summary>
    public string? TargetPath { get; init; }

    public int Connections { get; init; } = 8;

    public long MinSegmentSize { get; init; } = 1024 * 1024;

    /// <summary>Resources at or below this size are transferred over one connection.</summary>
    public long MinBytesForSegmented { get; init; } = 2 * 1024 * 1024;

    /// <summary>
    /// Let a connection that finishes early take over half of another in-flight
    /// range. Off by default because it makes the segment map less predictable.
    /// </summary>
    public bool DynamicSegmentation { get; init; }

    /// <summary>
    /// Create the working file as a sparse file so ranges that have not been
    /// written yet occupy no disk space. Ignored when the filesystem refuses the
    /// sparse flag. Off by default because the final file also stays sparse.
    /// </summary>
    public bool SparseWorkingFile { get; init; }

    public int MaxConnectionsPerHost { get; init; } = 8;

    /// <summary>
    /// Ceiling on the connections this one job may hold at the same time,
    /// regardless of <see cref="Connections"/>. It is a per-job cap, not a
    /// process-wide one: three concurrent jobs may each use this many
    /// connections. See <see cref="ResumeOptions.MaxConnectionsPerJob"/>.
    /// </summary>
    public int MaxConnectionsPerJob { get; init; } = 64;

    /// <summary>
    /// Optional shared pacer. Pass the same instance to every concurrent job to
    /// enforce one process-wide bandwidth cap.
    /// </summary>
    public IBandwidthLimiter? BandwidthLimiter { get; init; }

    public string? StatePath { get; init; }

    /// <summary>
    /// Optional directory that receives process artifacts instead of the target's
    /// own folder: the sidecar, the working file and HLS/DASH segment caches.
    /// When null, artifacts stay beside the target file (CLI-compatible layout).
    /// </summary>
    public string? ProcessDirectory { get; init; }

    /// <summary>Restart from zero when the remote validator changes. Otherwise fail.</summary>
    public bool RestartOnChange { get; init; }

    /// <summary>Discard an existing sidecar/working file and start over.</summary>
    public bool Restart { get; init; }

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxRetries { get; init; } = 5;

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional ffmpeg executable used to mux HLS/DASH tracks that store audio
    /// and video separately. When null, ffmpeg is looked up on PATH.
    /// </summary>
    public string? FfmpegPath { get; init; }

    public RequestOptions RequestOptions { get; init; } = new();
}
