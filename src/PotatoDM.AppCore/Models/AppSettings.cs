namespace PotatoDM.AppCore.Models;

/// <summary>User-facing preferences persisted between sessions.</summary>
public sealed class AppSettings
{
    /// <summary>Largest value <see cref="Normalize"/> accepts for the per-job cap.</summary>
    public const int MaxConnectionsPerJobCeiling = 64;

    public int Connections { get; set; } = 8;

    public bool AutomaticConnections { get; set; } = true;

    public int MaxConnectionsPerHost { get; set; } = 8;

    /// <summary>
    /// Ceiling on the connections a single job may hold at once, whatever
    /// <see cref="Connections"/> says. A per-job value, not a process-wide one:
    /// it is passed straight to the engine's connection limiter.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="MaxConnectionsPerJobCeiling"/>, which is above every
    /// reachable connection count, so the default behaviour is "no extra cap" and
    /// the setting only binds when a user lowers it.
    /// </remarks>
    public int MaxConnectionsPerJob { get; set; } = MaxConnectionsPerJobCeiling;

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

    /// <summary>Light, dark, or follow Windows.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public AppSettings Clone() => new()
    {
        Connections = Connections,
        AutomaticConnections = AutomaticConnections,
        MaxConnectionsPerHost = MaxConnectionsPerHost,
        MaxConnectionsPerJob = MaxConnectionsPerJob,
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
        Theme = Theme,
    };

    public void Normalize()
    {
        Connections = Math.Clamp(Connections, 1, 32);
        MaxConnectionsPerHost = Math.Clamp(MaxConnectionsPerHost, 1, 32);
        MaxConnectionsPerJob = Math.Clamp(MaxConnectionsPerJob, 1, MaxConnectionsPerJobCeiling);
        MaxConcurrentJobs = Math.Clamp(MaxConcurrentJobs, 1, 32);
        MaxBytesPerSecond = Math.Max(0, MaxBytesPerSecond);
        DownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? null : DownloadFolder.Trim();

        if (!Enum.IsDefined(typeof(AppTheme), Theme))
        {
            Theme = AppTheme.System;
        }
    }
}
