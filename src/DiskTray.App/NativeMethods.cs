using System.Runtime.InteropServices;

namespace DiskTray.App;

internal static partial class NativeMethods
{
    private const int SM_CXSMICON = 49;

    /// <summary>The pixel size of a tray icon at the taskbar's current DPI (16 at 100%, 24 at 150%, 32 at 200%).</summary>
    public static int TrayIconSize()
    {
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar != 0 ? GetDpiForWindow(taskbar) : 0;
        if (dpi == 0)
        {
            dpi = GetDpiForSystem();
        }

        int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        return size > 0 ? size : 16;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public int Size;
        public uint Time;
    }

    /// <summary>How long since the user last used the keyboard or mouse.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time))
            : TimeSpan.Zero;
    }

    /// <summary>Where the mouse is, in screen pixels.</summary>
    public static Point CursorPosition() => GetCursorPos(out var point) ? point : default;

    /// <summary>The whole monitor nearest <paramref name="point"/>, and its work area (the part the taskbar leaves).</summary>
    public static (Rect Monitor, Rect Work)? MonitorAreas(Point point)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        nint monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        return monitor != 0 && GetMonitorInfo(monitor, ref info) ? (info.Monitor, info.Work) : null;
    }

    public static Rect WindowRect(nint hwnd) => GetWindowRect(hwnd, out var rect) ? rect : default;

    public static uint DpiOf(nint hwnd) => GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi : 96;

    public static void MoveWindow(nint hwnd, int x, int y) =>
        SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
