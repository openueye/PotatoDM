namespace PotatoDM.AppCore.Services;

/// <summary>Location of the app's own registry and settings files.</summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string JobsFile => Path.Combine(Root, "jobs.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Directory that receives the daily <c>potatodm-yyyyMMdd.log</c> files.</summary>
    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static string DefaultDownloadFolder
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(profile, "Downloads");

            return Directory.Exists(downloads) ? downloads : profile;
        }
    }

    private static string ResolveRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            return Path.Combine(AppContext.BaseDirectory, "data");
        }

        return Path.Combine(local, "PotatoDM");
    }
}
