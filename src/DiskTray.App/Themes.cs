using System.Windows;
using DiskTray.Core.Flyout;
using DiskTray.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DiskTray.App;

/// <summary>
/// Applies the theme chosen in Settings to the app's resources and open windows, and follows
/// Windows when the choice is System.
/// </summary>
internal static class Themes
{
    private static readonly List<FluentWindow> Windows = [];
    private static AppTheme _choice = AppTheme.System;
    private static bool _started;

    public static SurfaceTheme Current =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light ? SurfaceTheme.Light : SurfaceTheme.Dark;

    public static void Choose(AppTheme choice)
    {
        _choice = choice;
        if (!_started)
        {
            _started = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        Apply();
    }

    /// <summary>Keeps the window's backdrop matching the theme while it's open.</summary>
    public static void Track(FluentWindow window)
    {
        window.SourceInitialized += (_, _) =>
        {
            Windows.Add(window);
            UpdateBackground(window);
        };
        window.Closed += (_, _) => Windows.Remove(window);
    }

    private static void Apply()
    {
        if (_choice == AppTheme.System)
        {
            ApplicationThemeManager.ApplySystemTheme();
        }
        else
        {
            ApplicationThemeManager.Apply(_choice == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.None, updateAccent: true);
        }

        foreach (var window in Windows)
        {
            UpdateBackground(window);
        }
    }

    private static void UpdateBackground(FluentWindow window) =>
        WindowBackgroundManager.UpdateBackground(window, ApplicationThemeManager.GetAppTheme(), window.WindowBackdropType);

    // Raised for many changes, including switching between light and dark in Windows.
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_choice == AppTheme.System && !ApplicationThemeManager.IsAppMatchesSystem())
                {
                    Apply();
                }
            });
        }
    }
}
