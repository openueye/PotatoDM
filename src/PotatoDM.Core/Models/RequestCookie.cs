namespace PotatoDM.Core.Models;

/// <summary>
/// A cookie taken from the browser. Domain and path are preserved so the HTTP
/// handler can decide which requests may receive it.
/// </summary>
public sealed class RequestCookie
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Path { get; set; } = "/";

    public bool Secure { get; set; }

    public bool HttpOnly { get; set; }

    public bool HostOnly { get; set; }

    public DateTimeOffset? ExpiresUtc { get; set; }
}
