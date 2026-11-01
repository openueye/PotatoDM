namespace PotatoDM.Core.Media;

/// <summary>The two manifest formats the media downloader understands.</summary>
public enum ManifestKind
{
    Hls = 0,
    Dash = 1,
}

/// <summary>An opaque byte range inside a resource; <see cref="EndInclusive"/> is inclusive.</summary>
public sealed record ByteRange(long Offset, long Length)
{
    public long EndInclusive => Offset + Length - 1;
}

/// <summary>An initialization section (HLS EXT-X-MAP or DASH Initialization).</summary>
public sealed record MediaInitialization(Uri Uri, ByteRange? Range);

/// <summary>
/// A key that the downloader is allowed to apply. DRM systems never reach this
/// type: the parsers throw <see cref="Exceptions.DrmProtectedException"/> first.
/// </summary>
public sealed record MediaKey(
    string Method,
    Uri? Uri,
    byte[]? Iv,
    string? KeyFormat)
{
    public bool IsIdentityFormat =>
        string.IsNullOrWhiteSpace(KeyFormat) ||
        KeyFormat.Equals("identity", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One media segment, already resolved against the playlist URI.</summary>
public sealed record MediaSegment(
    Uri Uri,
    ByteRange? Range,
    long Sequence,
    double DurationSeconds,
    MediaKey? Key,
    MediaInitialization? Initialization,
    bool Discontinuity,
    bool Gap = false);

/// <summary>An ordered track (video or audio) plus the container its bytes form.</summary>
public sealed class MediaTrackPlan
{
    public required string Kind { get; init; }

    public string? Language { get; init; }

    public required IReadOnlyList<MediaSegment> Segments { get; init; }

    /// <summary>Container of this track's bytes, e.g. ".ts" or ".mp4".</summary>
    public string ContainerExtension { get; init; } = ".mp4";
}

/// <summary>Everything needed to fetch and concatenate one manifest.</summary>
public sealed class MediaSourcePlan
{
    public required ManifestKind Kind { get; init; }

    public required IReadOnlyList<MediaTrackPlan> Tracks { get; init; }

    /// <summary>Extension of the merged result when no muxing is required.</summary>
    public required string ContainerExtension { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Stable digest of the playlist inputs; a cache with another digest is discarded.</summary>
    public string Fingerprint { get; init; } = string.Empty;

    public bool HasSeparateAudio =>
        Tracks.Any(track => string.Equals(track.Kind, "audio", StringComparison.Ordinal));
}
