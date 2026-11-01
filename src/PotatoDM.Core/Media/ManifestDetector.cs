using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Media;

/// <summary>Recognizes HLS and DASH manifests by URL shape or content type.</summary>
public static class ManifestDetector
{
    public static ManifestKind? FromUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var path = url.AbsolutePath;

        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.Hls;
        }

        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.Dash;
        }

        return null;
    }

    public static ManifestKind? FromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var mediaType = contentType.Split(';')[0].Trim();

        return mediaType.ToLowerInvariant() switch
        {
            "application/vnd.apple.mpegurl" or
            "application/x-mpegurl" or
            "audio/mpegurl" or
            "audio/x-mpegurl" => ManifestKind.Hls,
            "application/dash+xml" => ManifestKind.Dash,
            _ => null,
        };
    }

    public static bool IsManifest(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && FromUrl(uri) is not null;
}

/// <summary>Small helpers shared by the CLI and the app for manifest target names.</summary>
public static class MediaNaming
{
    /// <summary>Removes a trailing .m3u8/.mpd so the container extension can replace it.</summary>
    public static string? StripManifestExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var stripped = fileName.Trim();
        foreach (var suffix in new[] { ".m3u8", ".mpd" })
        {
            if (stripped.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stripped = stripped[..^suffix.Length];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    /// <summary>Base name derived from a manifest URL, with no extension.</summary>
    public static string BaseNameFromUrl(Uri manifestUri)
    {
        var leaf = manifestUri.Segments.LastOrDefault()?.Trim('/');
        var decoded = leaf;

        if (!string.IsNullOrEmpty(leaf))
        {
            try
            {
                decoded = Uri.UnescapeDataString(leaf);
            }
            catch (UriFormatException)
            {
                decoded = leaf;
            }
        }

        var stripped = StripManifestExtension(decoded);
        return string.IsNullOrWhiteSpace(stripped) ? "media" : stripped;
    }
}
