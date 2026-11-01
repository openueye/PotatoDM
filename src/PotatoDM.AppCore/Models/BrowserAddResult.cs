using PotatoDM.AppCore.Job;

namespace PotatoDM.AppCore.Models;

/// <summary>Outcome of a browser hand-off; the extension falls back when not accepted.</summary>
public sealed class BrowserAddResult
{
    public bool Accepted { get; init; }

    public DownloadJob? Job { get; init; }

    public string? JobId => Job?.Id;

    public string? TargetPath => Job?.TargetPath;

    /// <summary>Stable machine-readable reason, e.g. no_length or probe_timeout.</summary>
    public string? Error { get; init; }

    public string? Message { get; init; }

    public static BrowserAddResult Accept(DownloadJob job) => new()
    {
        Accepted = true,
        Job = job,
    };

    public static BrowserAddResult Reject(string error, string? message = null) => new()
    {
        Accepted = false,
        Error = error,
        Message = message,
    };
}
