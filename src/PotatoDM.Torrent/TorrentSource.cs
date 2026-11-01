using MonoTorrent;

namespace PotatoDM.Torrent;

/// <summary>Helpers for validating and naming magnet links without exposing MonoTorrent types.</summary>
public static class TorrentSource
{
    public static bool IsMagnet(string? source) =>
        source is not null && source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    /// <summary>A display name from <c>dn=</c>, or a short info-hash prefix.</summary>
    public static string SuggestedName(string source)
    {
        var displayName = QueryValue(source, "dn");
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var cleaned = Sanitize(displayName);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        var hash = InfoHash(source);
        return string.IsNullOrWhiteSpace(hash) ? "torrent" : "torrent-" + hash[..Math.Min(12, hash.Length)];
    }

    /// <summary>Throws <see cref="TorrentException"/> when the magnet link is malformed.</summary>
    public static void Validate(string source)
    {
        if (!IsMagnet(source))
        {
            throw new TorrentException("A magnet: URI is required.");
        }

        try
        {
            _ = MagnetLink.Parse(source);
        }
        catch (Exception ex) when (ex is not TorrentException)
        {
            throw new TorrentException($"Invalid magnet link: {ex.Message}", ex);
        }
    }

    public static string? InfoHash(string source)
    {
        var xt = QueryValue(source, "xt");
        if (xt is null)
        {
            return null;
        }

        const string prefix = "urn:btih:";
        return xt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? xt[prefix.Length..]
            : null;
    }

    private static string? QueryValue(string source, string key)
    {
        var queryStart = source.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }

        foreach (var pair in source[(queryStart + 1)..].Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 ||
                !pair[..separator].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        return null;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString().Trim().TrimEnd('.').Trim();
    }
}
