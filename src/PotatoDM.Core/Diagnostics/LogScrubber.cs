using System.Text.RegularExpressions;

namespace PotatoDM.Core.Diagnostics;

/// <summary>
/// Removes credentials from text before it reaches a disk sink. Download logs are
/// sent to users for support, so anything a sink writes is effectively public:
/// cookies, bearer tokens and the query string of a signed URL must not survive.
/// </summary>
/// <remarks>
/// Every pattern is applied with a match timeout, and a timeout degrades to a
/// blunt redaction of the whole string rather than throwing or leaking it.
/// </remarks>
public static class LogScrubber
{
    private const string Redacted = "<redacted>";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Header values that carry credentials, to the end of the line.</summary>
    private static readonly Regex CredentialHeader = new(
        @"(?<name>\b(?:cookie|set-cookie|authorization|proxy-authorization|x-api-key)\b\s*:\s*)[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Drops the query string of any absolute URL. The scheme, host and path stay
    /// because that is what makes a log line useful; the query is where signed
    /// tokens live.
    /// </summary>
    private static readonly Regex UrlQuery = new(
        @"\b(?<url>https?://[^\s?#""']+)\?[^\s#""']*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Key/value pairs that look like secrets, wherever they appear.</summary>
    private static readonly Regex SecretPair = new(
        @"(?<key>\b(?:access[_-]?token|refresh[_-]?token|token|api[_-]?key|apikey|auth|authorization|" +
        @"password|passwd|pwd|secret|signature|sig|session[_-]?id|sessionid|ticket|cookie)\b\s*[=:]\s*)" +
        @"(?<value>[^&;,\s""'}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Returns <paramref name="text"/> with credentials and URL query strings
    /// replaced. Never throws; never returns null.
    /// </summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return Redact(text) ?? Redacted;
    }

    /// <summary>
    /// Returns null when scrubbing could not complete, so the caller can drop the
    /// payload entirely instead of writing something it failed to inspect.
    /// </summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            var scrubbed = CredentialHeader.Replace(text, "${name}" + Redacted);
            scrubbed = UrlQuery.Replace(scrubbed, "${url}?" + Redacted);
            scrubbed = SecretPair.Replace(scrubbed, "${key}" + Redacted);

            return scrubbed;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
