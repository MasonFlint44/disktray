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

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
