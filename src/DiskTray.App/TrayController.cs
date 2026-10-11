using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskTray.Core;
using DiskTray.Core.Alerts;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Flyout;
using DiskTray.Core.Settings;
using DiskTray.Core.Tray;
using H.NotifyIcon;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace DiskTray.App;

/// <summary>
/// Owns the tray icons, the flyout, the low-space notifications and the clean up, and
/// refreshes them on a timer.
/// </summary>
internal sealed class TrayController : IDisposable
{
    /// <summary>How often to check whether it's time for the daily reminder.</summary>
    private static readonly TimeSpan ReminderCheckInterval = TimeSpan.FromMinutes(1);

    private readonly DiskMonitor _monitor;
    private readonly bool _dryRun;
    private readonly DockerExe _docker = new();
    private readonly WslExe _wsl = new();
    private readonly DispatcherTimer _reminderTimer;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TaskbarIcon> _icons = [];
    private readonly Dictionary<string, System.Drawing.Icon> _images = [];
    private bool _refreshing;
    private DiskSnapshot? _snapshot;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private CleanupWindow? _cleanupWindow;
    private CleanupProgress? _cleanupProgress;
    private CancellationTokenSource? _cleanupCancel;
    private AppSettings _settings;
    private AlertState _alerts = AlertStateFile.Load();

    // Why the last refresh failed, shown in every tooltip until one succeeds.
    private string? _problem;

    /// <param name="dryRun">Clean up only logs what it would do. Set by starting with <c>--dry-run</c>.</param>
    public TrayController(DiskMonitor monitor, AppSettings settings, bool dryRun)
    {
        _monitor = monitor;
        _dryRun = dryRun;
        _settings = settings;
        _timer = new DispatcherTimer { Interval = settings.RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _reminderTimer = new DispatcherTimer { Interval = ReminderCheckInterval };
        _reminderTimer.Tick += (_, _) => CheckAlerts(UserIsActive());
        ApplicationThemeManager.Changed += OnThemeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Notifier.Clicked += OnNotificationClicked;
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _timer.Start();
        _reminderTimer.Start();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _snapshot = await Task.Run(_monitor.Measure);
            _problem = null;
            Redraw();
            CheckAlerts(UserIsActive());
        }
        catch (Exception e)
        {
            // Keep whatever the icons showed last, and say why they're stale.
            Log.Error("Refresh failed", e);
            _problem = $"Couldn't refresh: {e.Message}";
            Redraw();
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _reminderTimer.Stop();
        ApplicationThemeManager.Changed -= OnThemeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Notifier.Clicked -= OnNotificationClicked;
        _flyout?.Close();
        _settingsWindow?.Close();
        _cleanupWindow?.Close();
        _cleanupCancel?.Cancel();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        foreach (var image in _images.Values)
        {
            image.Dispose();
        }

        _icons.Clear();
        _images.Clear();
    }

    private void Redraw()
    {
        int size = NativeMethods.TrayIconSize();
        if (_snapshot is null)
        {
            // Nothing measured yet. Show one plain icon so there's still a way to exit.
            var empty = new PieChart([new PieSlice(1, Palette.Free)], AlertLevel.Normal);
            SetIcon(TrayLayout.DriveKey, PieIconRenderer.ToIcon(PieIconRenderer.Render(empty, size)), _problem is null ? "DiskTray" : TrayLayout.ToolTip("DiskTray", _problem));
            return;
        }

        var models = TrayLayout.Build(_snapshot, _settings.Thresholds);
        foreach (var model in models)
        {
            string toolTip = _problem is null ? model.ToolTip : TrayLayout.ToolTip(model.ToolTip, _problem);
            SetIcon(model.Key, PieIconRenderer.ToIcon(PieIconRenderer.Render(model.Chart, size)), toolTip);
        }

        foreach (string key in _icons.Keys.Except(models.Select(m => m.Key)).ToList())
        {
            _icons.Remove(key, out var icon);
            icon?.Dispose();
            _images.Remove(key, out var image);
            image?.Dispose();
        }

        if (_flyout?.IsVisible == true)
        {
            UpdateFlyout();
        }
    }

    private void ToggleFlyout()
    {
        if (_flyout?.IsVisible == true)
        {
            _flyout.Dismiss(fromDeactivate: false);
        }
        else if (_flyout?.JustClosed != true)
        {
            ShowFlyout();
        }
    }

    private void ShowFlyout()
    {
        if (_flyout is null)
        {
            _flyout = new FlyoutWindow();
            _flyout.SettingsRequested += (_, _) => OpenSettings();
            _flyout.CleanupRequested += (_, _) => OpenCleanup();
            _flyout.CancelCleanupRequested += (_, _) => _cleanupCancel?.Cancel();
        }

        // Nothing to show until the first refresh finishes, which takes about a second.
        if (_snapshot is null)
        {
            return;
        }

        UpdateFlyout();
        _flyout.ShowAt(NativeMethods.CursorPosition());
    }

    private void CheckAlerts(bool userActive)
    {
        if (_snapshot is null)
        {
            return;
        }

        var (state, toast) = AlertRules.Check(_alerts, _snapshot, _settings.Thresholds, _settings.Alerts, DateTimeOffset.Now, userActive);
        if (state != _alerts)
        {
            _alerts = state;
            AlertStateFile.Save(state);
        }

        if (toast is not null)
        {
            var theme = ToastImage.WindowsMode();
            var picture = FlyoutLayout.Build(_snapshot, _settings.Thresholds, theme, DateTimeOffset.Now, _settings.RefreshInterval);
            Notifier.Show(toast, ToastImage.Save(picture, theme));
        }
    }

    /// <summary>
    /// Whether the user has used the keyboard or mouse since the last reminder check, so the
    /// reminder shows when they're at the PC rather than to an empty room.
    /// </summary>
    private static bool UserIsActive() => NativeMethods.IdleTime() < ReminderCheckInterval + TimeSpan.FromSeconds(30);

    // Unlocking or signing in counts as being at the PC.
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => CheckAlerts(userActive: true));
        }
    }

    private void OnNotificationClicked(object? sender, EventArgs e) => ShowFlyout();

    private void UpdateFlyout()
    {
        if (_flyout is not null && _snapshot is not null)
        {
            _flyout.Update(FlyoutLayout.Build(_snapshot, _settings.Thresholds, Themes.Current, DateTimeOffset.Now, _settings.RefreshInterval, _problem));
            _flyout.ShowCleanup(_cleanupProgress?.Text, _cleanupProgress?.CanCancel == true);
        }
    }

    private async void OpenCleanup()
    {
        if (_cleanupProgress is not null)
        {
            // One at a time; the flyout shows how the current one is going.
            ShowFlyout();
            return;
        }

        if (_cleanupWindow is not null)
        {
            _cleanupWindow.Activate();
            return;
        }

        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var window = new CleanupWindow(snapshot, _settings.Cleanup, _dryRun);
        _cleanupWindow = window;
        window.Started += request => _ = RunCleanupAsync(request);
        window.Closed += (_, _) => _cleanupWindow = null;
        window.Show();
        window.Activate();

        try
        {
            var state = await Task.Run(() => new CleanupScanner(_docker, _wsl).Scan(snapshot));
            if (_cleanupWindow == window)
            {
                window.ShowScan(state);
            }
        }
        catch (Exception e)
        {
            Log.Error("Couldn't check what Docker can prune", e);
            if (_cleanupWindow == window)
            {
                window.ShowScan(new CleanupState(snapshot, null, [], new HashSet<string>(), 0));
            }
        }
    }

    private async Task RunCleanupAsync(CleanupRequest request)
    {
        using var launcher = new CompactorLauncher();
        _cleanupCancel = new CancellationTokenSource();
        bool toldCompacting = false;
        var progress = new Progress<CleanupProgress>(p =>
        {
            _cleanupProgress = p;
            if (p.Step == CleanupStep.Compacting && !toldCompacting)
            {
                toldCompacting = true;
                Notifier.ShowCleanup(
                    request.DryRun ? "Compacting (dry run)" : "Compacting now",
                    request.DryRun ? "Nothing is being changed." : "WSL and Docker can't start until it's done, in about 10–20 minutes.");
            }

            UpdateCleanupStatus();
        });

        Log.Info($"Clean up started{(request.DryRun ? " (dry run)" : "")}: build cache {request.BuildCache}, dangling images {request.DanglingImages}, " +
            $"{request.Volumes.Count} volumes, compact {request.Disks.Count} disks {request.When}, start Docker after {request.StartDockerAfter}");
        try
        {
            var run = new CleanupRun(_docker, _wsl, launcher, (delay, cancel) => Task.Delay(delay, cancel), Log.Info);
            var result = await run.RunAsync(request, progress, _cleanupCancel.Token);
            Log.Info($"{result.Title}: {result.Body().Replace('\n', ' ')}");
            Notifier.ShowCleanup(result.Title, result.Body());
        }
        catch (Exception e)
        {
            Log.Error("Clean up failed", e);
            Notifier.ShowCleanup("Clean up didn't finish", e.Message);
        }
        finally
        {
            _cleanupProgress = null;
            _cleanupCancel.Dispose();
            _cleanupCancel = null;
            UpdateCleanupStatus();
        }

        await RefreshAsync();
    }

    private void UpdateCleanupStatus()
    {
        _flyout?.ShowCleanup(_cleanupProgress?.Text, _cleanupProgress?.CanCancel == true);
        foreach (var icon in _icons.Values)
        {
            if (icon.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Tag, CleanupMenuTag)) is { } item)
            {
                item.Header = _cleanupProgress?.CanCancel == true ? "Cancel clean up" : "Clean up…";
                item.IsEnabled = _cleanupProgress is null || _cleanupProgress.CanCancel;
            }
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, StartupRegistration.IsEnabled(), _snapshot?.SystemDrive);
        _settingsWindow.Saved += SaveSettings;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SaveSettings(AppSettings settings, bool startWithWindows)
    {
        SettingsFile.Save(settings);
        if (startWithWindows != StartupRegistration.IsEnabled())
        {
            StartupRegistration.Set(startWithWindows);
        }

        _settings = settings;
        // Setting the interval restarts the timer, so only do it when it changed.
        if (_timer.Interval != settings.RefreshInterval)
        {
            _timer.Interval = settings.RefreshInterval;
        }

        Themes.Choose(settings.Theme);
        Redraw();
        CheckAlerts(UserIsActive());
    }

    // The donut's gray slices differ between light and dark.
    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        if (_flyout?.IsVisible == true)
        {
            UpdateFlyout();
        }
    }

    private void SetIcon(string key, System.Drawing.Icon image, string toolTip)
    {
        if (_icons.TryGetValue(key, out var icon))
        {
            icon.Icon = image;
            icon.ToolTipText = toolTip;
        }
        else
        {
            icon = new TaskbarIcon
            {
                Id = IconId(key),
                Icon = image,
                ToolTipText = toolTip,
                ContextMenu = BuildMenu(),
                NoLeftClickDelay = true,
            };
            icon.TrayLeftMouseUp += (_, _) => ToggleFlyout();
            icon.ForceCreate(enablesEfficiencyMode: false);
            _icons[key] = icon;
        }

        // The shell keeps its own copy of the icon, so the old one can go now.
        if (_images.Remove(key, out var old))
        {
            old.Dispose();
        }

        _images[key] = image;
    }

    private const string CleanupMenuTag = "cleanup";

    private ContextMenu BuildMenu()
    {
        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += async (_, _) => await RefreshAsync();
        var cleanup = new MenuItem { Header = "Clean up…", Tag = CleanupMenuTag };
        cleanup.Click += (_, _) =>
        {
            if (_cleanupProgress?.CanCancel == true)
            {
                _cleanupCancel?.Cancel();
            }
            else
            {
                OpenCleanup();
            }
        };
        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => OpenSettings();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        return new ContextMenu { Items = { refresh, cleanup, settings, new Separator(), exit } };
    }

    /// <summary>
    /// Windows remembers each tray icon's settings (like being pinned to the taskbar) by
    /// GUID, and ties the GUID to the exe's path. Deriving it from both keeps the settings
    /// for an install and stops a copy elsewhere from colliding with it.
    /// </summary>
    private static Guid IconId(string key)
    {
        string seed = $"{Environment.ProcessPath}|{key}".ToUpperInvariant();
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
    }
}
