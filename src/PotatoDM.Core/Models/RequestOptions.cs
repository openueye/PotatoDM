namespace PotatoDM.Core.Models;

/// <summary>Per-request metadata forwarded to every HTTP request of a job.</summary>
public sealed class RequestOptions
{
    public string? Referer { get; init; }

    public string? Cookie { get; init; }

    /// <summary>
    /// Structured cookies from a browser. When present, the raw <see cref="Cookie"/>
    /// header is not sent; the HTTP handler attaches matching cookies itself.
    /// </summary>
    public IReadOnlyList<RequestCookie>? Cookies { get; init; }

    public string UserAgent { get; init; } = "PotatoDM/0.1";

    /// <summary>Optional extra headers (e.g. Authorization) added to every request.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>Use the system proxy. Integration tests disable this.</summary>
    public bool UseProxy { get; init; } = true;
}
