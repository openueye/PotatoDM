namespace PotatoDM.App.Services;

public sealed class BrowserInstallStatus
{
    public bool HostPresent { get; init; }

    public bool SourcePresent { get; init; }

    public bool ChromeDetected { get; init; }

    public bool EdgeDetected { get; init; }

    public bool ChromeRegistered { get; init; }

    public bool EdgeRegistered { get; init; }

    public string? ChromeId { get; init; }

    public string? ChromePath { get; init; }

    public string? EdgePath { get; init; }

    public string BrowserRoot { get; init; } = string.Empty;

    public string ChromiumExtensionPath { get; init; } = string.Empty;

    public string HostManifestPath { get; init; } = string.Empty;

    public bool FullyInstalled => HostPresent && SourcePresent && ChromeRegistered && EdgeRegistered;
}
