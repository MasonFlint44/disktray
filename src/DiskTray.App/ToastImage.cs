using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskTray.Core.Flyout;
using Microsoft.Win32;

namespace DiskTray.App;

/// <summary>
/// Renders the flyout's donut and legend to a PNG for a notification. Notifications follow
/// the Windows mode, not the app theme, so the image is drawn for that.
/// </summary>
internal static class ToastImage
{
    /// <summary>
    /// About how wide Windows shows a notification's image, so the text below comes out
    /// at these sizes rather than shrunk.
    /// </summary>
    private const double Width = 346;
    private const double Scale = 2;
    private static readonly string Folder = Path.Combine(AppDataFile.Folder, "notifications");

    /// <summary>Whether notifications are drawn light, from Settings › Personalization › Colors › Windows mode.</summary>
    public static SurfaceTheme WindowsMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is 1 ? SurfaceTheme.Light : SurfaceTheme.Dark;
    }

    /// <summary>Saves the image and returns its path, or null if it couldn't be made.</summary>
    public static string? Save(FlyoutModel model, SurfaceTheme theme)
    {
        try
        {
            var view = new DriveBreakdown { DataContext = model, Width = Width };
            view.Resources["DonutSize"] = 116.0;
            view.Resources["CenterFontSize"] = 20.0;
            view.Resources["NameFontSize"] = 14.0;
            view.Resources["DetailFontSize"] = 13.0;
            // The app's theme brushes may be the other mode, so set the text colors here.
            bool light = theme == SurfaceTheme.Light;
            view.Resources["TextFillColorPrimaryBrush"] = Brush(light ? "#E4000000" : "#FFFFFFFF");
            view.Resources["TextFillColorSecondaryBrush"] = Brush(light ? "#9E000000" : "#C5FFFFFF");
            view.Resources["ControlStrokeColorDefaultBrush"] = Brush(light ? "#0F000000" : "#12FFFFFF");
            view.Measure(new Size(Width, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            view.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(view.ActualWidth * Scale), (int)Math.Ceiling(view.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(view);

            Directory.CreateDirectory(Folder);
            // Windows caches notification images by path, so each one gets a new name.
            foreach (string old in Directory.EnumerateFiles(Folder, "drive-*.png"))
            {
                File.Delete(old);
            }

            string path = Path.Combine(Folder, $"drive-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } };
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Couldn't draw the notification image", e);
            return null;
        }
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
