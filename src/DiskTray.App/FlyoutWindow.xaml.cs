using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DiskTray.Core.Flyout;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DiskTray.App;

/// <summary>
/// The window that opens from a tray icon, next to the taskbar like the Windows clock and
/// volume flyouts. It hides when it loses focus.
/// </summary>
internal partial class FlyoutWindow : FluentWindow
{
    /// <summary>Clicking the tray icon takes focus from the flyout before the click arrives.</summary>
    private static readonly TimeSpan ClickThroughTime = TimeSpan.FromMilliseconds(400);

    private NativeMethods.Point _anchor;
    private long _hiddenAt;

    public FlyoutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => SystemThemeWatcher.Watch(this, WindowBackdropType.Acrylic);
        Deactivated += (_, _) => Dismiss(fromDeactivate: true);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss(fromDeactivate: false);
            }
        };
        // The content can grow or shrink while open, e.g. when a banner appears.
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Reposition();
            }
        };
    }

    /// <summary>
    /// True right after the flyout lost focus, when a click on a tray icon is the one that
    /// closed it and shouldn't open it again.
    /// </summary>
    public bool JustClosed => Environment.TickCount64 - _hiddenAt < ClickThroughTime.TotalMilliseconds;

    public void Update(FlyoutModel model) => View.DataContext = model;

    /// <summary>Opens the flyout by the taskbar, as close to <paramref name="anchor"/> (in screen pixels) as fits.</summary>
    public void ShowAt(NativeMethods.Point anchor)
    {
        _anchor = anchor;
        new WindowInteropHelper(this).EnsureHandle();
        Reposition();
        Show();
        Reposition();
        Activate();
    }

    public void Dismiss(bool fromDeactivate)
    {
        if (!IsVisible)
        {
            return;
        }

        if (fromDeactivate)
        {
            _hiddenAt = Environment.TickCount64;
        }

        Hide();
    }

    /// <summary>Puts the window against the taskbar's edge of the work area, centered on the anchor where there's room.</summary>
    private void Reposition()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || NativeMethods.MonitorAreas(_anchor) is not var (monitor, work))
        {
            return;
        }

        var window = NativeMethods.WindowRect(hwnd);
        int width = window.Right - window.Left;
        int height = window.Bottom - window.Top;
        int margin = (int)Math.Round(12 * NativeMethods.DpiOf(hwnd) / 96.0);

        int x = Math.Clamp(_anchor.X - (width / 2), work.Left + margin, Math.Max(work.Left + margin, work.Right - margin - width));
        int y = Math.Clamp(_anchor.Y - (height / 2), work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin - height));
        switch (TaskbarEdge(monitor, work))
        {
            case Edge.Top:
                y = work.Top + margin;
                break;
            case Edge.Left:
                x = work.Left + margin;
                break;
            case Edge.Right:
                x = work.Right - margin - width;
                break;
            default:
                y = work.Bottom - margin - height;
                break;
        }

        NativeMethods.MoveWindow(hwnd, x, y);
    }

    private enum Edge
    {
        Bottom,
        Top,
        Left,
        Right,
    }

    /// <summary>The side the taskbar takes from the work area, or the side nearest the anchor when it auto-hides.</summary>
    private Edge TaskbarEdge(NativeMethods.Rect monitor, NativeMethods.Rect work)
    {
        if (work.Bottom < monitor.Bottom)
        {
            return Edge.Bottom;
        }

        if (work.Top > monitor.Top)
        {
            return Edge.Top;
        }

        if (work.Left > monitor.Left)
        {
            return Edge.Left;
        }

        if (work.Right < monitor.Right)
        {
            return Edge.Right;
        }

        return new[]
            {
                (Edge.Bottom, monitor.Bottom - _anchor.Y),
                (Edge.Top, _anchor.Y - monitor.Top),
                (Edge.Left, _anchor.X - monitor.Left),
                (Edge.Right, monitor.Right - _anchor.X),
            }
            .MinBy(e => e.Item2).Item1;
    }
}
