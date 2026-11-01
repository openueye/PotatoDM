namespace PotatoDM.Torrent;

/// <summary>One BitTorrent download request.</summary>
public sealed class TorrentDownloadRequest
{
    /// <summary>A <c>magnet:?xt=urn:btih:…</c> link or the path to a .torrent file.</summary>
    public required string Source { get; init; }

    /// <summary>Directory that receives the torrent's files.</summary>
    public required string DownloadDirectory { get; init; }

    /// <summary>
    /// Optional directory for the torrent metadata cache. When null the cache
    /// is written below <see cref="DownloadDirectory"/>.
    /// </summary>
    public string? ProcessDirectory { get; init; }

    /// <summary>TCP listen port; 0 lets the OS choose one.</summary>
    public int ListenPort { get; init; }

    public bool EnableDht { get; init; } = true;

    /// <summary>
    /// Enable peer exchange. Used by the MonoTorrent paths (metadata exchange
    /// and pinned peers); libtorrent enables PEX in its own defaults.
    /// </summary>
    public bool EnablePeerExchange { get; init; } = true;

    /// <summary>
    /// Add a small set of public trackers when the magnet or .torrent has none.
    /// Public torrents benefit; private torrents are never touched.
    /// </summary>
    public bool AddFallbackTrackers { get; init; } = true;

    public bool AllowPortForwarding { get; init; }

    /// <summary>Download cap in bytes per second; 0 means unlimited.</summary>
    public long MaximumDownloadBytesPerSecond { get; init; }

    /// <summary>Upload cap in bytes per second; 0 means unlimited.</summary>
    public long MaximumUploadBytesPerSecond { get; init; }

    /// <summary>Keep seeding after the download completes.</summary>
    public bool SeedAfterDownload { get; init; }

    /// <summary>
    /// Extra peers expressed as <c>ipv4://host:port</c> or <c>ipv6://[addr]:port</c>.
    /// Used for manual peers and loopback tests; discovery still runs normally.
    /// </summary>
    public IReadOnlyList<string> PeerEndPoints { get; init; } = Array.Empty<string>();
}

/// <summary>A snapshot of one torrent's transfer.</summary>
public readonly record struct TorrentProgress(
    string Name,
    long TotalBytes,
    long BytesDownloaded,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond,
    int ConnectedPeers,
    bool HasMetadata,
    bool IsChecking = false);

/// <summary>Where the downloaded payload ended up.</summary>
public sealed record TorrentDownloadResult(
    string TargetPath,
    bool IsDirectory,
    long TotalBytes,
    string Name);

/// <summary>A torrent could not be loaded, started, or completed.</summary>
public sealed class TorrentException : Exception
{
    public TorrentException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
