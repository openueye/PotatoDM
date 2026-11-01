using System.Globalization;
using System.Text;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Internal;

namespace PotatoDM.Core.Media;

/// <summary>A variant (rendition of the main content) advertised by a master playlist.</summary>
public sealed record HlsVariant(
    string? Name,
    Uri Uri,
    long Bandwidth,
    string? Codecs,
    string? Resolution,
    string? AudioGroup);

/// <summary>An alternate rendition (audio, subtitles, ...) declared by an EXT-X-MEDIA tag.</summary>
public sealed record HlsRendition(
    string Type,
    string? GroupId,
    string? Name,
    string? Language,
    Uri? Uri,
    bool IsDefault);

/// <summary>The parsed content of an HLS master playlist.</summary>
public sealed class HlsMasterPlaylist
{
    public required IReadOnlyList<HlsVariant> Variants { get; init; }

    public required IReadOnlyList<HlsRendition> Renditions { get; init; }

    public required IReadOnlyList<MediaKey> SessionKeys { get; init; }
}

/// <summary>The parsed content of an HLS media playlist.</summary>
public sealed class HlsMediaPlaylist
{
    public required IReadOnlyList<MediaSegment> Segments { get; init; }

    public bool EndList { get; init; }

    public string? PlaylistType { get; init; }

    public long MediaSequence { get; init; }

    public double TargetDuration { get; init; }
}

/// <summary>
/// Parser for HLS playlists (RFC 8216). Produces <see cref="HlsMasterPlaylist"/> and
/// <see cref="HlsMediaPlaylist"/> values whose URIs are resolved against the playlist URI.
/// </summary>
public static class HlsPlaylistParser
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="text"/> looks like a master
    /// playlist (contains an EXT-X-STREAM-INF or EXT-X-MEDIA tag).
    /// </summary>
    public static bool IsMaster(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var line in SplitLines(text))
        {
            if (!TryGetTag(line, out var tag, out _))
            {
                continue;
            }

            if (tag.Equals("EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses an HLS master playlist and resolves every URI against <paramref name="playlistUri"/>.</summary>
    public static HlsMasterPlaylist ParseMaster(string text, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(playlistUri);

        var lines = SplitLines(text);
        var variants = new List<HlsVariant>();
        var renditions = new List<HlsRendition>();
        var sessionKeys = new List<MediaKey>();

        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryGetTag(lines[i], out var tag, out var value))
            {
                continue;
            }

            switch (tag.ToUpperInvariant())
            {
                case "EXT-X-STREAM-INF":
                {
                    var attributes = ParseAttributes(value);
                    var bandwidth = RequiredLong(attributes, "BANDWIDTH");
                    var uriText = FindNextUriLine(lines, ref i);
                    if (uriText is null)
                    {
                        throw new MediaManifestException("EXT-X-STREAM-INF is missing its URI line.");
                    }

                    variants.Add(new HlsVariant(
                        Optional(attributes, "NAME"),
                        ResolveUri(playlistUri, uriText),
                        bandwidth,
                        Optional(attributes, "CODECS"),
                        Optional(attributes, "RESOLUTION"),
                        Optional(attributes, "AUDIO")));
                    break;
                }
                case "EXT-X-MEDIA":
                {
                    var attributes = ParseAttributes(value);
                    var type = Optional(attributes, "TYPE")
                        ?? throw new MediaManifestException("EXT-X-MEDIA is missing its TYPE attribute.");

                    var uriValue = Optional(attributes, "URI");
                    Uri? uri = uriValue is null ? null : ResolveUri(playlistUri, uriValue);

                    renditions.Add(new HlsRendition(
                        type,
                        Optional(attributes, "GROUP-ID"),
                        Optional(attributes, "NAME"),
                        Optional(attributes, "LANGUAGE"),
                        uri,
                        string.Equals(Optional(attributes, "DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase)));
                    break;
                }
                case "EXT-X-SESSION-KEY":
                {
                    var key = ParseKey(ParseAttributes(value), playlistUri);
                    if (key is not null)
                    {
                        sessionKeys.Add(key);
                    }

                    break;
                }
                default:
                    break; // Ignore I-FRAME-STREAM-INF and every other master tag.
            }
        }

        return new HlsMasterPlaylist
        {
            Variants = variants,
            Renditions = renditions,
            SessionKeys = sessionKeys,
        };
    }

    /// <summary>Parses an HLS media playlist and resolves every URI against <paramref name="playlistUri"/>.</summary>
    public static HlsMediaPlaylist ParseMedia(string text, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(playlistUri);

        var segments = new List<MediaSegment>();
        var rangeEnds = new Dictionary<string, long>(StringComparer.Ordinal);

        var startMediaSequence = 0L;
        var nextSequence = 0L;
        var targetDuration = 0.0;
        string? playlistType = null;
        var endList = false;

        MediaKey? currentKey = null;
        MediaInitialization? currentMap = null;

        var pendingDuration = 0.0;
        var pendingDiscontinuity = false;
        var pendingGap = false;
        long? pendingRangeLength = null;
        long? pendingRangeOffset = null;

        foreach (var line in SplitLines(text))
        {
            if (TryGetTag(line, out var tag, out var value))
            {
                switch (tag.ToUpperInvariant())
                {
                    case "EXTINF":
                        pendingDuration = ParseDuration(value);
                        break;
                    case "EXT-X-MEDIA-SEQUENCE":
                        startMediaSequence = ParseLong(value, "EXT-X-MEDIA-SEQUENCE");
                        nextSequence = startMediaSequence;
                        break;
                    case "EXT-X-TARGETDURATION":
                        targetDuration = ParseDouble(value, "EXT-X-TARGETDURATION");
                        break;
                    case "EXT-X-PLAYLIST-TYPE":
                        playlistType = value.Trim();
                        break;
                    case "EXT-X-ENDLIST":
                        endList = true;
                        break;
                    case "EXT-X-KEY":
                        currentKey = ParseKey(ParseAttributes(value), playlistUri);
                        break;
                    case "EXT-X-MAP":
                        currentMap = ParseMap(value, playlistUri);
                        break;
                    case "EXT-X-BYTERANGE":
                        (pendingRangeLength, pendingRangeOffset) = ParseByteRangeValue(value);
                        break;
                    case "EXT-X-DISCONTINUITY":
                        pendingDiscontinuity = true;
                        break;
                    case "EXT-X-GAP":
                        pendingGap = true;
                        break;
                    default:
                        break; // EXTM3U, EXT-X-VERSION, EXT-X-START, ...
                }

                continue;
            }

            var uri = ResolveUri(playlistUri, line);

            ByteRange? range = null;
            if (pendingRangeLength is long length)
            {
                var rangeKey = uri.AbsoluteUri;
                var offset = pendingRangeOffset
                    ?? (rangeEnds.TryGetValue(rangeKey, out var previousEnd) ? previousEnd : 0L);
                range = new ByteRange(offset, length);
                rangeEnds[rangeKey] = offset + length;
            }

            segments.Add(new MediaSegment(
                uri,
                range,
                nextSequence++,
                pendingDuration,
                currentKey,
                currentMap,
                pendingDiscontinuity,
                pendingGap));

            pendingDuration = 0.0;
            pendingDiscontinuity = false;
            pendingGap = false;
            pendingRangeLength = null;
            pendingRangeOffset = null;
        }

        if (segments.Count == 0)
        {
            throw new MediaManifestException("Media playlist contains no segments.");
        }

        return new HlsMediaPlaylist
        {
            Segments = segments,
            EndList = endList,
            PlaylistType = playlistType,
            MediaSequence = startMediaSequence,
            TargetDuration = targetDuration,
        };
    }

    private static MediaInitialization ParseMap(string value, Uri playlistUri)
    {
        var attributes = ParseAttributes(value);
        var uriValue = Optional(attributes, "URI")
            ?? throw new MediaManifestException("EXT-X-MAP is missing its URI attribute.");

        ByteRange? range = null;
        if (Optional(attributes, "BYTERANGE") is { } byteRangeValue)
        {
            var (length, offset) = ParseByteRangeValue(byteRangeValue);
            range = new ByteRange(offset ?? 0L, length);
        }

        return new MediaInitialization(ResolveUri(playlistUri, uriValue), range);
    }

    private static MediaKey? ParseKey(IReadOnlyDictionary<string, string> attributes, Uri playlistUri)
    {
        var method = Optional(attributes, "METHOD")
            ?? throw new MediaManifestException("Key tag is missing its METHOD attribute.");

        if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
        {
            throw new DrmProtectedException(method);
        }

        var keyFormat = Optional(attributes, "KEYFORMAT");
        if (!IsIdentityFormat(keyFormat))
        {
            throw new DrmProtectedException(keyFormat!);
        }

        var uriValue = Optional(attributes, "URI")
            ?? throw new MediaManifestException("AES-128 key is missing its URI attribute.");

        return new MediaKey(
            "AES-128",
            ResolveUri(playlistUri, uriValue),
            ParseIv(Optional(attributes, "IV")),
            string.IsNullOrEmpty(keyFormat) ? null : keyFormat);
    }

    private static bool IsIdentityFormat(string? keyFormat) =>
        string.IsNullOrWhiteSpace(keyFormat) ||
        keyFormat.Equals("identity", StringComparison.OrdinalIgnoreCase);

    private static byte[]? ParseIv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.Length != 34)
        {
            throw new MediaManifestException("IV must be a 0x-prefixed 16-byte hex string.");
        }

        var bytes = new byte[16];
        var hex = value.AsSpan(2);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new MediaManifestException("IV contains non-hex characters.");
            }
        }

        return bytes;
    }

    private static (long Length, long? Offset) ParseByteRangeValue(string value)
    {
        var at = value.IndexOf('@');
        var lengthText = at < 0 ? value : value[..at];
        var offsetText = at < 0 ? null : value[(at + 1)..];

        if (!long.TryParse(lengthText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            throw new MediaManifestException("Byte range has an invalid length.");
        }

        long? offset = null;
        if (offsetText is not null)
        {
            if (!long.TryParse(offsetText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset))
            {
                throw new MediaManifestException("Byte range has an invalid offset.");
            }

            offset = parsedOffset;
        }

        return (length, offset);
    }

    private static double ParseDuration(string value)
    {
        var comma = value.IndexOf(',');
        var secondsText = comma < 0 ? value : value[..comma];

        if (!double.TryParse(secondsText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new MediaManifestException("EXTINF is missing a valid duration.");
        }

        return seconds;
    }

    private static long ParseLong(string value, string attribute)
    {
        if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new MediaManifestException($"'{attribute}' is not a valid integer.");
    }

    private static double ParseDouble(string value, string attribute)
    {
        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new MediaManifestException($"'{attribute}' is not a valid number.");
    }

    private static long RequiredLong(IReadOnlyDictionary<string, string> attributes, string key)
    {
        var raw = Optional(attributes, key)
            ?? throw new MediaManifestException($"'{key}' attribute is required.");

        return ParseLong(raw, key);
    }

    private static string? Optional(IReadOnlyDictionary<string, string> attributes, string key) =>
        attributes.TryGetValue(key, out var value) ? value : null;

    private static string? FindNextUriLine(IReadOnlyList<string> lines, ref int index)
    {
        while (index + 1 < lines.Count && lines[index + 1].StartsWith('#'))
        {
            index++;
        }

        if (index + 1 >= lines.Count)
        {
            return null;
        }

        index++;
        return lines[index];
    }

    private static Uri ResolveUri(Uri playlistUri, string value)
    {
        if (Uri.TryCreate(playlistUri, value, out var resolved))
        {
            return resolved;
        }

        throw new MediaManifestException($"'{value}' is not a valid URI.");
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var line = text[start..i].TrimEnd('\r').Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }

            start = i + 1;
        }

        var last = text[start..].TrimEnd('\r').Trim();
        if (last.Length > 0)
        {
            lines.Add(last);
        }

        return lines;
    }

    private static bool TryGetTag(string line, out string tag, out string value)
    {
        tag = string.Empty;
        value = string.Empty;

        if (line.Length == 0 || line[0] != '#')
        {
            return false;
        }

        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            tag = line[1..];
            return true;
        }

        tag = line[1..colon];
        value = line[(colon + 1)..];
        return true;
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in SplitOnCommas(text))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = part[..equals].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = Unquote(part[(equals + 1)..].Trim());
        }

        return result;
    }

    private static IEnumerable<string> SplitOnCommas(string text) =>
        QuotedListSplitter.Split(text, ',');

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return value;
    }
}
