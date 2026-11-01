namespace PotatoDM.BrowserIpc;

/// <summary>Request sent by the browser extension through the native host.</summary>
public sealed class BrowserRequest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>One of <see cref="BrowserRequestTypes"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Extension-generated id used to make retries idempotent.</summary>
    public string? RequestId { get; set; }

    public string? Url { get; set; }

    /// <summary>Optional hint: "manifest" (HLS/DASH) or "magnet". Null for ordinary downloads.</summary>
    public string? Kind { get; set; }

    /// <summary>Response content type observed by the extension, when available.</summary>
    public string? ContentType { get; set; }

    /// <summary>Browser-suggested file name; treated as an untrusted leaf name.</summary>
    public string? SuggestedFileName { get; set; }

    public string? Referer { get; set; }

    public string? UserAgent { get; set; }

    public List<BrowserCookieDto>? Cookies { get; set; }

    public Dictionary<string, string>? ExtraHeaders { get; set; }
}

public static class BrowserRequestTypes
{
    public const string Ping = "ping";
    public const string Download = "download";
    public const string Activate = "activate";
}

/// <summary>A browser cookie. Domain/path are preserved so the engine can use a cookie container.</summary>
public sealed class BrowserCookieDto
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Path { get; set; } = "/";

    public bool Secure { get; set; }

    public bool HttpOnly { get; set; }

    public bool HostOnly { get; set; }

    public bool Session { get; set; }

    /// <summary>Unix seconds, matching the browser cookies API.</summary>
    public double? ExpirationDate { get; set; }
}

/// <summary>Response returned to the native host and then to the extension.</summary>
public sealed class BrowserResponse
{
    public bool Ok { get; set; }

    /// <summary>Null for ping/activate; true/false for download requests.</summary>
    public bool? Accepted { get; set; }

    public string? JobId { get; set; }

    public string? TargetPath { get; set; }

    /// <summary>Stable machine-readable reason, e.g. no_length or app_unavailable.</summary>
    public string? Error { get; set; }

    public string? Message { get; set; }

    public static BrowserResponse Success(string? message = null) =>
        new() { Ok = true, Message = message };

    public static BrowserResponse AcceptedDownload(string jobId, string targetPath) =>
        new() { Ok = true, Accepted = true, JobId = jobId, TargetPath = targetPath };

    public static BrowserResponse Rejected(string error, string? message = null) =>
        new() { Ok = true, Accepted = false, Error = error, Message = message };

    public static BrowserResponse Failure(string error, string? message = null) =>
        new() { Ok = false, Error = error, Message = message };
}
