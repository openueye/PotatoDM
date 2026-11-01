using Microsoft.Win32;

namespace PotatoDM.App.Services;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PotatoDM";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key?.SetValue(ValueName, $"\"{Environment.ProcessPath}\"", RegistryValueKind.String);
        }
        else
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
