using PotatoDM.Core.Models;

namespace PotatoDM.AppCore.Models;

/// <summary>Browser-supplied request metadata kept with a job for resumes.</summary>
public sealed class JobRequestMetadata
{
    public string? Referer { get; init; }

    public string? UserAgent { get; init; }

    public IReadOnlyList<RequestCookie>? Cookies { get; init; }

    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}
