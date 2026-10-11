using DiskTray.Core;
using DiskTray.Core.Detection;
using DiskTray.Core.Tray;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>Scans the disk files and pinned items, measures how much of each disk is in use, and saves the readings.</summary>
internal sealed class DiskMonitor(DiskScanner scanner, UsageMeter meter, UsageCache cache, FolderSizer folders)
{
    /// <summary>Adds up pinned folders in the background.</summary>
    public FolderSizer Folders => folders;

    public DiskSnapshot Measure(IReadOnlyList<TrayPin>? pins = null, DetectionOptions? detect = null)
    {
        pins ??= TrayPin.Defaults;
        folders.Track(pins.Where(p => p.Kind == PinKind.Path && p.Target is not null).Select(p => p.Target!));
        var snapshot = meter.Measure(scanner.Scan(pins, detect));
        UsageCacheFile.Save(cache);
        return snapshot;
    }
}
