namespace PotatoDM.Core.Models;

/// <summary>Everything learned from probing a URL.</summary>
public sealed record ProbeResult(
    Uri RequestedUri,
    Uri FinalUri,
    long? TotalLength,
    bool SupportsRanges,
    string? ETag,
    DateTimeOffset? LastModified,
    string? SuggestedFileName,
    string? ContentType)
{
    /// <summary>Only a strong validator may be used in an If-Range header.</summary>
    public bool HasStrongETag =>
        !string.IsNullOrEmpty(ETag) &&
        !ETag.StartsWith("W/", StringComparison.Ordinal);
}
