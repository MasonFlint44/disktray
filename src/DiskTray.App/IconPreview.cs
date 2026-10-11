using System.IO;
using System.Text;
using DiskTray.Core.Detection;
using DiskTray.Core.Tray;

namespace DiskTray.App;

/// <summary>
/// <c>DiskTray.exe --render-icons &lt;folder&gt;</c>: saves the current icons as PNGs at
/// several sizes, plus their tooltips, for checking the drawing without the tray.
/// </summary>
internal static class IconPreview
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64];

    public static void Run(DiskScanner scanner, string folder)
    {
        Directory.CreateDirectory(folder);
        var models = TrayLayout.Build(scanner.Scan(), new AlertThresholds()).ToList();
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
            }

            text.Append($"[{model.Key}]\n{model.ToolTip}\n\n");
        }

        File.WriteAllText(Path.Combine(folder, "tooltips.txt"), text.ToString());
    }

    private static TrayIconModel Sample(string key, AlertLevel alert) => new(
        key,
        new PieChart([new(0.18, Palette.Docker.Main), new(0.55, Palette.Docker.Shade), new(0.27, Palette.Free)], alert),
        "(sample)");
}
