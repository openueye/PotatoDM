namespace PotatoDM.Core.Models;

/// <summary>Immutable progress snapshot for one real HTTP byte-range segment.</summary>
public sealed record DownloadSegmentProgress(
    int Index,
    long BytesDownloaded,
    long TotalBytes)
{
    public int DisplayIndex => Index + 1;

    public double Percent =>
        TotalBytes > 0
            ? Math.Clamp((double)BytesDownloaded / TotalBytes * 100.0, 0, 100)
            : 0;

    public bool IsComplete => TotalBytes > 0 && BytesDownloaded >= TotalBytes;
}
