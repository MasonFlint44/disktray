using System.Windows;
using DiskTray.Core;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Settings;
using DiskTray.Core.Tray;
using Wpf.Ui.Controls;

namespace DiskTray.App;

/// <summary>
/// The Settings window. Nothing changes until Save. The Detection tab comes with the
/// large-file scan.
/// </summary>
internal partial class SettingsWindow : FluentWindow
{
    private readonly AppSettings _original;
    private readonly DriveSpace? _drive;
    private readonly DiskSnapshot? _snapshot;
    private bool _ready;

    /// <param name="snapshot">The last reading, to show each level as space on the drive and what pins point at. Null before the first refresh.</param>
    public SettingsWindow(AppSettings settings, bool startsWithWindows, DiskSnapshot? snapshot)
    {
        _original = settings;
        _snapshot = snapshot;
        _drive = snapshot?.SystemDrive;
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
        NotifyOnDrop.IsChecked = settings.NotifyOnDrop;
        DailyReminder.IsChecked = settings.DailyReminder;
        // Every half hour, plus a hand-edited time that isn't one of those.
        var times = Enumerable.Range(0, 48).Select(i => new TimeOnly(i / 2, i % 2 * 30))
            .Append(settings.ReminderTime).Distinct().Order().ToList();
        ReminderTime.ItemsSource = times.Select(t => t.ToString("t")).ToList();
        ReminderTime.Tag = times;
        ReminderTime.SelectedIndex = times.IndexOf(settings.ReminderTime);
        CleanupBuildCache.IsChecked = settings.CleanupBuildCache;
        CleanupDanglingImages.IsChecked = settings.CleanupDanglingImages;
        CleanupDeletedProjectVolumes.IsChecked = settings.CleanupDeletedProjectVolumes;
        CleanupCompact.IsChecked = settings.CleanupCompact;
        CleanupCompactWhen.SelectedIndex = (int)settings.CleanupCompactWhen;
        CleanupStartDocker.IsChecked = settings.CleanupStartDocker;
        LoadTrayIcons(settings);
        LoadDetection(settings);
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
        NotifyOnDrop = NotifyOnDrop.IsChecked == true,
        DailyReminder = DailyReminder.IsChecked == true,
        ReminderTime = ((List<TimeOnly>)ReminderTime.Tag)[Math.Max(0, ReminderTime.SelectedIndex)],
        CleanupBuildCache = CleanupBuildCache.IsChecked == true,
        CleanupDanglingImages = CleanupDanglingImages.IsChecked == true,
        CleanupDeletedProjectVolumes = CleanupDeletedProjectVolumes.IsChecked == true,
        CleanupCompact = CleanupCompact.IsChecked == true,
        CleanupCompactWhen = (CompactWhen)Math.Max(0, CleanupCompactWhen.SelectedIndex),
        CleanupStartDocker = CleanupStartDocker.IsChecked == true,
        IconShape = ShapeBar.IsChecked == true ? IconShape.Bar : IconShape.Pie,
        DetectDistros = DetectDistros.IsChecked == true,
        DetectDocker = DetectDocker.IsChecked == true,
        LargeFileScan = LargeFileScan.IsChecked == true,
        LargeFileMinGiB = (int)Math.Clamp(Math.Round(LargeFileMin.Value ?? 2), 1, AppSettings.MaxLargeFileMinGiB),
        ShowLargeFiles = ShowLargeFiles.IsChecked == true,
        CodeFolders = [.. _codeFolders],
        Pins = TrayPin.Clean(_pins),
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
