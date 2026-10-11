using DiskTray.Core.Flyout;
using Wpf.Ui.Appearance;

namespace DiskTray.App;

/// <summary>
/// Keeps the app's light or dark theme matching Windows. The Settings window will add a
/// choice of System, Light or Dark.
/// </summary>
internal static class Themes
{
    public static SurfaceTheme Current =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light ? SurfaceTheme.Light : SurfaceTheme.Dark;

    /// <summary>Switches to the system theme if it changed since last time.</summary>
    public static void Apply()
    {
        if (!ApplicationThemeManager.IsAppMatchesSystem())
        {
            ApplicationThemeManager.ApplySystemTheme();
        }
    }
}
