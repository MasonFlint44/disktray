using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskTray.Core;
using DiskTray.Core.Flyout;
using DiskTray.Core.Tray;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DiskTray.App;

/// <summary>
/// <c>DiskTray.exe --render-icons &lt;folder&gt;</c>: saves the current icons as PNGs at
/// several sizes, plus their tooltips, the flyout and the notification picture in both
/// themes, and the app icon (app.ico), for checking the drawing without the tray.
/// </summary>
internal static class IconPreview
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64];

    public static void Run(DiskMonitor monitor, string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = SettingsFile.Load();
        var snapshot = monitor.Measure(settings.Pins);
        var thresholds = settings.Thresholds;
        var models = TrayLayout.Build(snapshot, thresholds, pins: settings.Pins).ToList();
        models.Add(Sample("sample-reclaimable", AlertLevel.Normal));
        models.Add(Sample("sample-critical", AlertLevel.Critical));

        var text = new StringBuilder($"Tray icon size on this taskbar: {NativeMethods.TrayIconSize()} px\n\n");
        foreach (var model in models)
        {
            string name = string.Concat(model.Key.Where(char.IsLetterOrDigit).Concat("-"));
            foreach (int size in Sizes)
            {
                var bitmap = PieIconRenderer.Render(model.Chart, size);
                PieIconRenderer.SavePng(bitmap, Path.Combine(folder, $"{name}{size}.png"));
                // Also go through the .ico path the tray uses.
                using var icon = PieIconRenderer.ToIcon(bitmap);
                PieIconRenderer.SavePng(PieIconRenderer.Render(model.Chart, size, IconShape.Bar), Path.Combine(folder, $"{name}bar-{size}.png"));
            }

            text.Append($"[{model.Key}]\n{model.ToolTip}\n\n");
        }

        File.WriteAllText(Path.Combine(folder, "tooltips.txt"), text.ToString());

        AppIcon.SaveIco(Path.Combine(folder, "app.ico"));
        foreach (int size in AppIcon.Sizes)
        {
            PieIconRenderer.SavePng(AppIcon.Render(size), Path.Combine(folder, $"app-{size}.png"));
        }

        foreach (var theme in new[] { SurfaceTheme.Dark, SurfaceTheme.Light })
        {
            var model = FlyoutLayout.Build(snapshot, thresholds, theme, DateTimeOffset.Now, TimeSpan.FromMinutes(5), pins: settings.Pins);
            if (ToastImage.Save(model, theme) is { } image)
            {
                File.Move(image, Path.Combine(folder, $"toast-{theme.ToString().ToLowerInvariant()}.png"), overwrite: true);
            }
        }

        SaveFlyout(snapshot, settings, ApplicationTheme.Dark, null, Path.Combine(folder, "flyout-dark.png"));
        SaveFlyout(snapshot, settings, ApplicationTheme.Light, null, Path.Combine(folder, "flyout-light.png"));
        SaveFlyout(snapshot, settings, ApplicationTheme.Dark, "Couldn't refresh: (sample)", Path.Combine(folder, "flyout-problem.png"));
    }

    /// <summary>Renders the flyout's content at 200% on the theme's plain background (the real one is see-through).</summary>
    private static void SaveFlyout(DiskSnapshot snapshot, DiskTray.Core.Settings.AppSettings settings, ApplicationTheme theme, string? problem, string path)
    {
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
        var view = new FlyoutView
        {
            DataContext = FlyoutLayout.Build(snapshot, settings.Thresholds, Themes.Current, DateTimeOffset.Now, TimeSpan.FromMinutes(5), problem, pins: settings.Pins),
        };
        var root = new Border { Child = view };
        root.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        const double scale = 2;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } };
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static TrayIconModel Sample(string key, AlertLevel alert) => new(
        key,
        new PieChart([new(0.18, Palette.Docker.Main), new(0.55, Palette.Docker.Reclaimable), new(0.27, Palette.Free)], alert),
        "(sample)");
}
