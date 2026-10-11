using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskTray.Core.Tray;

namespace DiskTray.App;

/// <summary>Draws a <see cref="PieChart"/> as a tray icon.</summary>
internal static class PieIconRenderer
{
    private static readonly Dictionary<Rgb, SolidColorBrush> Brushes = [];

    public static BitmapSource Render(PieChart chart, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Draw(dc, chart, size);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        // Icons want straight alpha; RenderTargetBitmap only does premultiplied.
        var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }

    public static System.Drawing.Icon ToIcon(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        using var stream = new MemoryStream(IcoWriter.Write(width, height, pixels));
        return new System.Drawing.Icon(stream);
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Draw(DrawingContext dc, PieChart chart, int size)
    {
        // A thin gray outline keeps the dark "free" color visible on a dark taskbar; a
        // thicker amber or red one means free space is low.
        double ring = chart.Alert == AlertLevel.Normal ? Math.Max(1, size / 16.0) : Math.Max(2, size / 9.0);
        var ringColor = chart.Alert switch
        {
            AlertLevel.Critical => Palette.Critical,
            AlertLevel.Warning => Palette.Warning,
            _ => Palette.Outline,
        };
        double outer = size / 2.0;
        var center = new Point(outer, outer);
        // Tuck the pie half a pixel under the ring so no gap shows between them.
        double radius = outer - ring + 0.5;

        // Each slice is drawn from its start all the way round to 12 o'clock, over the ones
        // before it. Neighbors then blend into each other at their edges instead of letting
        // the background show through as a seam.
        double start = 0;
        foreach (var slice in chart.Slices)
        {
            if (start < 1 - 1e-6)
            {
                dc.DrawGeometry(Brush(slice.Color), null, WedgeToEnd(center, radius, start));
            }

            start += slice.Fraction;
        }

        double ringRadius = outer - (ring / 2);
        dc.DrawEllipse(null, new Pen(Brush(ringColor), ring), center, ringRadius, ringRadius);
    }

    /// <summary>The part of the circle from <paramref name="from"/> (0–1, clockwise from 12 o'clock) to the end.</summary>
    private static Geometry WedgeToEnd(Point center, double radius, double from)
    {
        if (from <= 0)
        {
            return new EllipseGeometry(center, radius, radius);
        }

        double angle = (from * 2 * Math.PI) - (Math.PI / 2);
        var arcStart = new Point(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
        var top = new Point(center.X, center.Y - radius);
        bool isLargeArc = 1 - from > 0.5;
        var figure = new PathFigure(
            center,
            [
                new LineSegment(arcStart, isStroked: false),
                new ArcSegment(top, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, isStroked: false),
            ],
            closed: true);
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Brush(Rgb color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            Brushes[color] = brush;
        }

        return brush;
    }
}
