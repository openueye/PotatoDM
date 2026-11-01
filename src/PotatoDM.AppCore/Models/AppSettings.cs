namespace PotatoDM.AppCore.Models;

/// <summary>User-facing preferences persisted between sessions.</summary>
public sealed class AppSettings
{
    public int Connections { get; set; } = 8;

    public bool AutomaticConnections { get; set; } = true;

    public int MaxConnectionsPerHost { get; set; } = 8;

    /// <summary>Number of jobs that may transfer at the same time; the rest queue.</summary>
    public int MaxConcurrentJobs { get; set; } = 3;

    /// <summary>Process-wide download cap in bytes per second; zero means unlimited.</summary>
    public long MaxBytesPerSecond { get; set; }

    /// <summary>Let connections that finish early split the slowest range.</summary>
    public bool DynamicSegmentation { get; set; }

    /// <summary>Keep ranges no worker has written yet out of the working file.</summary>
    public bool SparseWorkingFile { get; set; }

    /// <summary>Optional ffmpeg location; null means "look on PATH".</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Preferred destination for new downloads; null uses the Windows Downloads folder.</summary>
    public string? DownloadFolder { get; set; }

    public bool UseProxy { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool LaunchAtStartup { get; set; }

    public AppSettings Clone() => new()
    {
        Connections = Connections,
        AutomaticConnections = AutomaticConnections,
        MaxConnectionsPerHost = MaxConnectionsPerHost,
        MaxConcurrentJobs = MaxConcurrentJobs,
        MaxBytesPerSecond = MaxBytesPerSecond,
        DynamicSegmentation = DynamicSegmentation,
        SparseWorkingFile = SparseWorkingFile,
        FfmpegPath = FfmpegPath,
        DownloadFolder = DownloadFolder,
        UseProxy = UseProxy,
        CloseToTray = CloseToTray,
        StartMinimized = StartMinimized,
        LaunchAtStartup = LaunchAtStartup,
    };

    public void Normalize()
    {
        Connections = Math.Clamp(Connections, 1, 32);
        MaxConnectionsPerHost = Math.Clamp(MaxConnectionsPerHost, 1, 32);
        MaxConcurrentJobs = Math.Clamp(MaxConcurrentJobs, 1, 32);
        MaxBytesPerSecond = Math.Max(0, MaxBytesPerSecond);
        DownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? null : DownloadFolder.Trim();
    }
}
