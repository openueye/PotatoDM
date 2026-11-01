using System.Text;
using PotatoDM.Core.Internal;

namespace PotatoDM.Core.Http;

/// <summary>
/// Minimal, testable parser for Content-Disposition file names, including
/// RFC 5987/6266 <c>filename*=UTF-8''...</c>.
/// </summary>
public static class ContentDispositionParser
{
    public static string? GetFileName(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        string? extended = null;
        string? plain = null;

        foreach (var parameter in SplitParameters(headerValue))
        {
            var equals = parameter.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = parameter[..equals].Trim();
            var rawValue = parameter[(equals + 1)..].Trim();

            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                extended = DecodeExtended(rawValue);
            }
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                plain = Unquote(rawValue);
            }
        }

        return string.IsNullOrEmpty(extended) ? plain : extended;
    }

    private static IEnumerable<string> SplitParameters(string headerValue) =>
        QuotedListSplitter.Split(headerValue, ';');

    private static string? DecodeExtended(string value)
    {
        // charset'language'percent-encoded-value
        var firstQuote = value.IndexOf('\'');
        if (firstQuote < 0)
        {
            return Unquote(value);
        }

        var secondQuote = value.IndexOf('\'', firstQuote + 1);
        if (secondQuote < 0)
        {
            return Unquote(value);
        }

        var charset = value[..firstQuote].Trim();
        var encoded = value[(secondQuote + 1)..];

        if (charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) ||
            charset.Equals("latin-1", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = PercentDecodeToBytes(encoded);
            return Encoding.Latin1.GetString(bytes);
        }

        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            return Unquote(value);
        }
    }

    private static byte[] PercentDecodeToBytes(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length &&
                byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.Add((byte)value[i]);
            }
        }

        return bytes.ToArray();
    }

    private static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                builder.Append(text[++i]);
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }
}
