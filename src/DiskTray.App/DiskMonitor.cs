using DiskTray.Core;
using DiskTray.Core.Detection;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>Scans the disk files, measures how much of each is in use, and saves the readings.</summary>
internal sealed class DiskMonitor(DiskScanner scanner, UsageMeter meter, UsageCache cache)
{
    public DiskSnapshot Measure()
    {
        var snapshot = meter.Measure(scanner.Scan());
        UsageCacheFile.Save(cache);
        return snapshot;
    }
}
