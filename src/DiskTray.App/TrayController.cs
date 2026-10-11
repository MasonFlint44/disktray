using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskTray.Core.Detection;
using DiskTray.Core.Tray;
using H.NotifyIcon;

namespace DiskTray.App;

/// <summary>Owns the tray icons and refreshes them on a timer.</summary>
internal sealed class TrayController : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly DiskScanner _scanner;
    private readonly AlertThresholds _thresholds = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TaskbarIcon> _icons = [];
    private readonly Dictionary<string, System.Drawing.Icon> _images = [];
    private readonly Dictionary<string, string> _toolTips = [];
    private bool _refreshing;

    public TrayController(DiskScanner scanner)
    {
        _scanner = scanner;
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _timer.Start();
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
            var snapshot = await Task.Run(_scanner.Scan);
            Show(TrayLayout.Build(snapshot, _thresholds));
        }
        catch (Exception e)
        {
            // Keep whatever the icons showed last, and say why they're stale.
            Log.Error("Refresh failed", e);
            ShowError(e);
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
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

    private void Show(IReadOnlyList<TrayIconModel> models)
    {
        int size = NativeMethods.TrayIconSize();
        foreach (var model in models)
        {
            SetIcon(model.Key, PieIconRenderer.ToIcon(PieIconRenderer.Render(model.Chart, size)), model.ToolTip);
        }

        foreach (string key in _icons.Keys.Except(models.Select(m => m.Key)).ToList())
        {
            _icons.Remove(key, out var icon);
            icon?.Dispose();
            _images.Remove(key, out var image);
            image?.Dispose();
            _toolTips.Remove(key);
        }
    }

    private void ShowError(Exception error)
    {
        string problem = $"Couldn't refresh: {error.Message}";
        if (_icons.Count == 0)
        {
            // Nothing measured yet. Show one plain icon so there's still a way to exit.
            var empty = new PieChart([new PieSlice(1, Palette.Free)], AlertLevel.Normal);
            SetIcon(TrayLayout.DriveKey, PieIconRenderer.ToIcon(PieIconRenderer.Render(empty, NativeMethods.TrayIconSize())), TrayLayout.ToolTip("DiskTray", problem));
            return;
        }

        foreach (var (key, icon) in _icons)
        {
            icon.ToolTipText = TrayLayout.ToolTip(_toolTips.GetValueOrDefault(key, "DiskTray"), problem);
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
            icon.ForceCreate(enablesEfficiencyMode: false);
            _icons[key] = icon;
        }

        // The shell keeps its own copy of the icon, so the old one can go now.
        if (_images.Remove(key, out var old))
        {
            old.Dispose();
        }

        _images[key] = image;
        _toolTips[key] = toolTip;
    }

    private ContextMenu BuildMenu()
    {
        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += async (_, _) => await RefreshAsync();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        return new ContextMenu { Items = { refresh, new Separator(), exit } };
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
