namespace PotatoDM.Core.Models;

/// <summary>Successful completion summary.</summary>
public sealed record DownloadResult(
    string TargetPath,
    long TotalBytes,
    DownloadMode Mode,
    TimeSpan Duration);
