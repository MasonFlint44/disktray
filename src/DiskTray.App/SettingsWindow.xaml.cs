using System.Windows;
using DiskTray.Core;
using DiskTray.Core.Settings;
using Wpf.Ui.Controls;

namespace DiskTray.App;

/// <summary>
/// The Settings window. Nothing changes until Save. More tabs (Tray icons, Detection,
/// Clean up) come with the features they control.
/// </summary>
internal partial class SettingsWindow : FluentWindow
{
    private readonly AppSettings _original;
    private readonly DriveSpace? _drive;
    private bool _ready;

    /// <param name="drive">The system drive, to show each level as space on it. Null before the first refresh.</param>
    public SettingsWindow(AppSettings settings, bool startsWithWindows, DriveSpace? drive)
    {
        _original = settings;
        _drive = drive;
        InitializeComponent();
        Themes.Track(this);

        Warning.Value = settings.WarningPercent;
        Critical.Value = settings.CriticalPercent;
        Theme.SelectedIndex = (int)settings.Theme;
        // A hand-edited interval that isn't one of the choices stays selectable.
        var minutes = AppSettings.RefreshChoices.Append(settings.RefreshMinutes).Distinct().Order().ToList();
        Refresh.ItemsSource = minutes.Select(m => AppSettings.DescribeMinutes(m)).ToList();
        Refresh.Tag = minutes;
        Refresh.SelectedIndex = minutes.IndexOf(settings.RefreshMinutes);
        StartWithWindows.IsChecked = startsWithWindows;
        _ready = true;
        UpdateLevels();
    }

    /// <summary>Raised on Save with what was chosen, and whether to start with Windows.</summary>
    public event Action<AppSettings, bool>? Saved;

    private AppSettings Chosen() => _original with
    {
        WarningPercent = (int)Math.Round(Warning.Value ?? -1),
        CriticalPercent = (int)Math.Round(Critical.Value ?? -1),
        Theme = (AppTheme)Math.Max(0, Theme.SelectedIndex),
        RefreshMinutes = ((List<int>)Refresh.Tag)[Math.Max(0, Refresh.SelectedIndex)],
    };

    private void OnLevelChanged(object sender, RoutedEventArgs e)
    {
        // Also raised while the constructor fills in the boxes.
        if (_ready)
        {
            UpdateLevels();
        }
    }

    private void UpdateLevels()
    {
        string Hint(double? percent) => _drive is null || percent is null
            ? "% free"
            : $"% free ({AppSettings.ApproximateSpace((int)Math.Round(percent.Value), _drive)})";

        WarningHint.Text = Hint(Warning.Value);
        CriticalHint.Text = Hint(Critical.Value);
        string? problem = Chosen().LevelsProblem();
        LevelsProblem.Text = problem;
        LevelsProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        Save.IsEnabled = problem is null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var chosen = Chosen();
        if (chosen.LevelsProblem() is not null)
        {
            return;
        }

        Saved?.Invoke(chosen, StartWithWindows.IsChecked == true);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
