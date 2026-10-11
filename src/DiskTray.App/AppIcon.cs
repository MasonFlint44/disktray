using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskTray.Core.Tray;

namespace DiskTray.App;

/// <summary>
/// Draws the app's own icon (the exe, notifications, Task Manager): the flyout's donut with
/// Docker and WSL, each with its reclaimable tint, and Windows &amp; other.
/// <c>--render-icons</c> saves it as app.ico, which is copied to src/DiskTray.App.
/// </summary>
internal static class AppIcon
{
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static readonly PieChart Chart = new(
        [
            new(0.28, Palette.Docker.Main),
            new(0.20, Palette.Docker.Reclaimable),
            new(0.17, Palette.Wsl.Main),
            new(0.11, Palette.Wsl.Reclaimable),
            new(0.24, Palette.Other),
        ],
        AlertLevel.Normal);

    public static BitmapSource Render(int size)
    {
        // Thicker ring and smaller gaps at small sizes, so it still reads as a donut.
        var donut = new DonutChart
        {
            Chart = Chart,
            Width = size,
            Height = size,
            RingFraction = size <= 24 ? 0.55 : 0.45,
            Gap = Math.Max(0.6, size / 40.0),
        };
        donut.Measure(new Size(size, size));
        donut.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(donut);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SaveIco(string path)
    {
        var images = Sizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(Render(size)) } };
            using var png = new MemoryStream();
            encoder.Save(png);
            return (size, png.ToArray());
        }).ToList();
        File.WriteAllBytes(path, IcoWriter.WritePngs(images));
    }
}
