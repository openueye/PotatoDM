namespace PotatoDM.AppCore.Models;

/// <summary>Status shown in the UI. Distinct from the engine's persisted status.</summary>
public enum DownloadJobState
{
    Probing,
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
}
