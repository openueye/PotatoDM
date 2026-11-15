using PotatoDM.AppCore.Models;

namespace PotatoDM.App.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Clone_keeps_the_theme()
    {
        var settings = new AppSettings { Theme = AppTheme.Dark };

        Assert.Equal(AppTheme.Dark, settings.Clone().Theme);
    }

    [Fact]
    public void Normalize_resets_an_unknown_theme_to_system()
    {
        var settings = new AppSettings { Theme = (AppTheme)99 };

        settings.Normalize();

        Assert.Equal(AppTheme.System, settings.Theme);
    }
}
