namespace PotatoDM.Core.Models;

/// <summary>Aggregated progress snapshot emitted by the engine.</summary>
public sealed record DownloadProgress(
    long BytesDownloaded,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    int ActiveConnections,
    IReadOnlyList<DownloadSegmentProgress>? Segments = null)
{
    public double? Percent =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value * 100.0, 0, 100) : null;
}
