namespace PotatoDM.AppCore.Models;

/// <summary>
/// A job known to the UI. The engine sidecar remains the source of truth for
/// progress; this record only remembers which jobs belong in the list.
/// </summary>
public sealed class JobRecord
{
    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string StatePath { get; set; } = string.Empty;

    /// <summary>
    /// Per-task folder for jobs created with the integrated layout. Null/empty
    /// for records written by older versions, which used flat sidecars.
    /// </summary>
    public string? TaskDirectory { get; set; }

    /// <summary>
    /// True for HLS/DASH jobs. They keep their progress in the per-target media
    /// cache instead of an engine sidecar, so they restore and resume differently.
    /// </summary>
    public bool IsMedia { get; set; }

    /// <summary>True for magnet/BitTorrent jobs; progress lives in the torrent's metadata cache.</summary>
    public bool IsTorrent { get; set; }

    /// <summary>
    /// True once the job finished successfully. Torrent payloads are
    /// preallocated, so an existing target file is not evidence of completion;
    /// records written before this flag existed restore as paused instead.
    /// </summary>
    public bool IsCompleted { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    // Browser hand-offs persist the request context so a resume still works.
    public string? Referer { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Protected JSON cookie list; null when the job has no cookies.</summary>
    public string? ProtectedCookies { get; set; }

    public Dictionary<string, string>? ExtraHeaders { get; set; }
}
