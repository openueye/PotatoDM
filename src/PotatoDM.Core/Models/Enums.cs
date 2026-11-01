namespace PotatoDM.Core.Models;

/// <summary>How the engine decided to transfer the resource.</summary>
public enum DownloadMode
{
    SingleStream = 0,
    Segmented = 1,
}

/// <summary>Lifecycle status persisted in the sidecar state file.</summary>
public enum JobStatus
{
    Pending = 0,
    Downloading = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
}
