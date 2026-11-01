using PotatoDM.Core.Models;

namespace PotatoDM.AppCore.Models;

/// <summary>Validated download hand-off received from the browser extension.</summary>
public sealed class BrowserDownloadContext
{
    public required Uri Url { get; init; }

    public string? RequestId { get; init; }

    /// <summary>"manifest" or "magnet" when the extension marked the request.</summary>
    public string? Kind { get; init; }

    /// <summary>Content type the browser saw; used to detect manifests without a suffix.</summary>
    public string? ContentType { get; init; }

    public string? SuggestedFileName { get; init; }

    public string? Referer { get; init; }

    public string? UserAgent { get; init; }

    public IReadOnlyList<RequestCookie>? Cookies { get; init; }

    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}
