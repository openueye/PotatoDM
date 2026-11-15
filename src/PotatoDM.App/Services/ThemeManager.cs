using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;
using PotatoDM.AppCore.Models;

namespace PotatoDM.App.Services;

/// <summary>Swaps the light/dark resource dictionary at runtime.</summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ResourceDictionary? _current;

    public static AppTheme EffectiveTheme(AppTheme requested) =>
        requested switch
        {
            AppTheme.Dark => AppTheme.Dark,
            AppTheme.Light => AppTheme.Light,
            _ => IsSystemDark() ? AppTheme.Dark : AppTheme.Light,
        };

    public static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\" + PersonalizeKey,
                "AppsUseLightTheme",
                1);

            return value is int useLight && useLight == 0;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A locked-down machine keeps the light default rather than failing
            // to start over a cosmetic lookup.
            return false;
        }
    }

    public static void Apply(AppTheme requested)
    {
        var app = Application.Current;

        if (app is null)
        {
            return;
        }

        var dark = EffectiveTheme(requested) == AppTheme.Dark;
        var assembly = typeof(ThemeManager).Assembly.GetName().Name ?? "PotatoDM";
        var dictionary = new ResourceDictionary
        {
            // A relative URI resolves against the entry assembly, which is not
            // necessarily this one (tests and tooling embed the client). Name the
            // assembly so the lookup always lands on the client's BAML.
            Source = new Uri(
                $"pack://application:,,,/{assembly};component/Themes/" +
                (dark ? "Dark.xaml" : "Light.xaml"),
                UriKind.Absolute),
        };

        if (_current is not null)
        {
            app.Resources.MergedDictionaries.Remove(_current);
        }

        app.Resources.MergedDictionaries.Add(dictionary);
        _current = dictionary;
    }
}
