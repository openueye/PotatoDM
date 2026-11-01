using System.Globalization;

namespace PotatoDM.Core.Http;

/// <summary>Parsed Content-Range header. Any component may be null when the server sent '*'.</summary>
public readonly record struct ContentRange(long? Start, long? EndInclusive, long? TotalLength);

public static class ContentRangeParser
{
    /// <summary>Parses "bytes 0-99/1000" and "bytes */1000".</summary>
    public static bool TryParse(string? value, out ContentRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (!text.StartsWith("bytes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text[5..].Trim();
        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var rangePart = rest[..slash].Trim();
        var totalPart = rest[(slash + 1)..].Trim();

        long? total = null;
        if (totalPart != "*")
        {
            if (!long.TryParse(totalPart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTotal) ||
                parsedTotal < 0)
            {
                return false;
            }

            total = parsedTotal;
        }

        if (rangePart == "*")
        {
            range = new ContentRange(null, null, total);
            return true;
        }

        var dash = rangePart.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startPart = rangePart[..dash].Trim();
        var endPart = rangePart[(dash + 1)..].Trim();
        if (startPart.Length == 0 || endPart.Length == 0)
        {
            return false;
        }

        if (!long.TryParse(startPart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(endPart, NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            start < 0 || end < start)
        {
            return false;
        }

        range = new ContentRange(start, end, total);
        return true;
    }
}
