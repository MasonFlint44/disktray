using System.Globalization;
using DiskTray.Core.Detection;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Flyout;

/// <summary>Whether the flyout is drawn on a dark or a light background.</summary>
public enum SurfaceTheme
{
    Dark,
    Light,
}

/// <summary>A banner at the top of the flyout, for low free space or a failed refresh.</summary>
public sealed record FlyoutBanner(AlertLevel Level, string Text);

/// <summary>One legend row: a swatch, a name and a size, with an optional second line.</summary>
public sealed record LegendItem(string Name, string Size, Rgb Color, string? Detail = null);

/// <summary>What "Other large files" lists: the last scan (null before the first) and the size threshold.</summary>
/// <param name="Scanning">A scan is running now.</param>
public sealed record LargeFileListing(LargeFileScan? Scan, long MinBytes, bool Scanning = false);

/// <summary>A file the large-file scan found, for "Other large files".</summary>
/// <param name="Folder">Where it is, e.g. <c>C:\Users\me\VMs</c>.</param>
/// <param name="CanPin">False when all the tray's pins are in use.</param>
public sealed record LargeFileLine(string Path, string Name, string Size, string Folder, bool CanPin);

/// <summary>Everything the flyout shows, already worded and colored.</summary>
public sealed record FlyoutModel(
    string DriveLine,
    IReadOnlyList<FlyoutBanner> Banners,
    PieChart Donut,
    string DonutDescription,
    string FreeSize,
    IReadOnlyList<LegendItem> Legend,
    bool ShowsReclaimable,
    string? ReclaimableTotal,
    string? ReclaimableBreakdown,
    string Footer)
{
    /// <summary>Whether to show "Other large files" at all: the scan is on and the flyout lists them.</summary>
    public bool ShowsLargeFiles { get; init; }

    public IReadOnlyList<LargeFileLine> LargeFiles { get; init; } = [];

    /// <summary>"Files over 2.0 GiB in your user folder · scanned 9:14 AM · 3 more not shown", or why there's nothing yet.</summary>
    public string? LargeFilesNote { get; init; }
}

/// <summary>
/// Works out the flyout for the system drive: the pinned items on it, with each disk file
/// split into used and reclaimable space when that's known. Items go largest first; Windows
/// &amp; other and free always come last.
/// </summary>
public static class FlyoutLayout
{
    /// <param name="now">The current time, so "Updated" can show a date once the reading is a day old.</param>
    /// <param name="refreshInterval">How often the tray checks, for the footer.</param>
    /// <param name="problem">Why the last refresh failed, if it did.</param>
    /// <param name="pins">What's pinned to the tray, or null for <see cref="TrayPin.Defaults"/>.</param>
    /// <param name="largeFiles">What to list under "Other large files", or null to leave it out.</param>
    public static FlyoutModel Build(
        DiskSnapshot snapshot,
        AlertThresholds thresholds,
        SurfaceTheme theme,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        string? problem = null,
        IFormatProvider? provider = null,
        TimeZoneInfo? zone = null,
        IReadOnlyList<TrayPin>? pins = null,
        LargeFileListing? largeFiles = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        zone ??= TimeZoneInfo.Local;
        var drive = snapshot.SystemDrive;
        var all = TrayLayout.Resolve(snapshot, pins ?? TrayPin.Defaults);
        var items = TrayLayout.ItemsOn(all, drive);
        var files = items.Select(i => i.File!).ToList();
        long other = TrayLayout.OtherBytes(drive, files);
        // A color picked for the drive is its "Windows & other" color.
        var driveItem = all.FirstOrDefault(i => i.IsDrive && i.Drive == drive);
        var otherColor = driveItem?.Pin.Color is not null ? driveItem.Color.Main
            : theme == SurfaceTheme.Light ? Palette.OtherOnLight : Palette.Other;
        var freeColor = theme == SurfaceTheme.Light ? Palette.FreeOnLight : Palette.Free;
        string Size(long bytes) => ByteSize.Format(bytes, provider);

        var amounts = new List<(long Bytes, Rgb Color)>();
        var legend = new List<LegendItem>();
        foreach (var item in items)
        {
            var file = item.File!;
            var color = item.Color;
            if (file.UsedBytes is { } used)
            {
                amounts.Add((Math.Min(used, file.SizeBytes), color.Main));
                amounts.Add((file.ReclaimableBytes ?? 0, color.Reclaimable));
            }
            else
            {
                amounts.Add((file.SizeBytes, color.Main));
            }

            legend.Add(new LegendItem(item.Name, Size(file.SizeBytes), color.Main, Detail(file, now, zone, provider)));
        }

        amounts.Add((other, otherColor));
        amounts.Add((drive.FreeBytes, freeColor));
        legend.Add(new LegendItem("Windows & other", Size(other), otherColor));
        legend.Add(new LegendItem("Free", Size(drive.FreeBytes), freeColor));

        var banners = new List<FlyoutBanner>();
        if (problem is not null)
        {
            banners.Add(new FlyoutBanner(AlertLevel.Critical, problem));
        }

        if (LowSpaceBanner(drive, thresholds, provider) is { } low)
        {
            banners.Add(low);
        }

        var reclaimable = items.Where(i => i.File!.ReclaimableBytes > 0).ToList();
        long reclaimableTotal = reclaimable.Sum(i => i.File!.ReclaimableBytes ?? 0);

        return new FlyoutModel(
            $"Drive {drive.Label} · {Size(drive.TotalBytes)}",
            banners,
            PieChart.FromAmounts(amounts, thresholds.Classify(drive)),
            Description(drive, items, other, provider),
            Size(drive.FreeBytes),
            legend,
            files.Any(f => f.ReclaimableBytes > 0),
            reclaimableTotal > 0 ? Size(reclaimableTotal) : null,
            reclaimableTotal > 0
                ? string.Join(" · ", reclaimable.Select(i => $"{TrayLayout.ShortName(i)} {Size(i.File!.ReclaimableBytes ?? 0)}"))
                : null,
            Footer(snapshot.TakenAt, now, refreshInterval, zone, provider))
        {
            ShowsLargeFiles = largeFiles is not null,
            LargeFiles = largeFiles is null ? [] : LargeFileLines(largeFiles, snapshot, pins ?? TrayPin.Defaults, provider),
            LargeFilesNote = largeFiles is null ? null : LargeFilesNote(largeFiles, snapshot, pins ?? TrayPin.Defaults, now, zone, provider),
        };
    }

    private static IReadOnlyList<LargeFileLine> LargeFileLines(LargeFileListing listing, DiskSnapshot snapshot, IReadOnlyList<TrayPin> pins, IFormatProvider provider)
    {
        if (listing.Scan is not { } scan)
        {
            return [];
        }

        bool canPin = pins.Count < TrayPin.MaxPins;
        return scan.Others(snapshot, pins, listing.MinBytes)
            .Take(LargeFileScan.Shown)
            .Select(f =>
            {
                string name = WindowsPath.GetName(f.Path);
                string folder = f.Path.Length > name.Length ? f.Path[..^name.Length].TrimEnd('\\') : "";
                return new LargeFileLine(f.Path, name, ByteSize.Format(f.SizeBytes, provider), folder, canPin);
            })
            .ToList();
    }

    /// <summary>"Files over 2.0 GiB in your user folder · scanned 9:14 AM", plus "· 3 more not shown".</summary>
    private static string LargeFilesNote(LargeFileListing listing, DiskSnapshot snapshot, IReadOnlyList<TrayPin> pins, DateTimeOffset now, TimeZoneInfo zone, IFormatProvider provider)
    {
        string over = $"over {ByteSize.Format(listing.MinBytes, provider)} in your user folder";
        if (listing.Scan is not { } scan)
        {
            return listing.Scanning ? $"Looking for files {over}…" : $"Files {over} show here after the daily scan";
        }

        int count = scan.Others(snapshot, pins, listing.MinBytes).Count;
        string note = count == 0 ? $"No other files {over}" : $"Files {over}";
        note += listing.Scanning ? " · scanning…" : $" · scanned {TrayLayout.AsOf(scan.ScannedAt, now, zone, provider)}";
        return count > LargeFileScan.Shown ? $"{note} · {count - LargeFileScan.Shown} more not shown" : note;
    }

    /// <summary>"34 GiB used · 94 GiB reclaimable", or how to get those numbers.</summary>
    internal static string? Detail(DiskFile file, DateTimeOffset now, TimeZoneInfo zone, IFormatProvider provider)
    {
        if (file.UsedBytes is { } used)
        {
            string detail = $"{ByteSize.Format(Math.Min(used, file.SizeBytes), provider)} used · {ByteSize.Format(file.ReclaimableBytes ?? 0, provider)} reclaimable";
            return file.UsedAsOf is { } measuredAt ? $"{detail} · as of {TrayLayout.AsOf(measuredAt, now, zone, provider)}" : detail;
        }

        if (file.SizedAsOf is { } sizedAt)
        {
            return $"as of {TrayLayout.AsOf(sizedAt, now, zone, provider)}";
        }

        if (file.UsageMount is null)
        {
            return null;
        }

        // Only distros that are already running get measured.
        string runs = file.Kind == DiskKind.DockerData ? "Docker Desktop runs" : $"{file.Name} runs";
        return $"Shows what's reclaimable once {runs}";
    }

    /// <summary>"50 GiB free (11%) – below your 15% warning level", or null when there's room.</summary>
    internal static FlyoutBanner? LowSpaceBanner(DriveSpace drive, AlertThresholds thresholds, IFormatProvider provider)
    {
        string free = $"{ByteSize.Format(drive.FreeBytes, provider)} free ({ByteSize.FormatPercent(drive.FreeFraction, provider)})";
        return thresholds.Classify(drive) switch
        {
            AlertLevel.Critical => new FlyoutBanner(
                AlertLevel.Critical,
                $"{free} – below your {ByteSize.FormatPercent(thresholds.CriticalFreeFraction, provider)} critical level"),
            AlertLevel.Warning => new FlyoutBanner(
                AlertLevel.Warning,
                $"{free} – below your {ByteSize.FormatPercent(thresholds.WarningFreeFraction, provider)} warning level"),
            _ => null,
        };
    }

    /// <summary>"Updated 1:41 PM · checks every 5 min".</summary>
    internal static string Footer(DateTimeOffset takenAt, DateTimeOffset now, TimeSpan interval, TimeZoneInfo zone, IFormatProvider provider)
    {
        string every = interval.TotalMinutes < 60
            ? string.Format(provider, "{0:0} min", interval.TotalMinutes)
            : string.Format(provider, "{0:0.#} h", interval.TotalHours);
        return $"Updated {TrayLayout.AsOf(takenAt, now, zone, provider)} · checks every {every}";
    }

    /// <summary>For screen readers: "Drive C: 27% Docker data, 13% Ubuntu, 49% Windows &amp; other, 11% free".</summary>
    private static string Description(DriveSpace drive, IEnumerable<TrayItem> items, long other, IFormatProvider provider)
    {
        string Part(long bytes, string name) =>
            $"{ByteSize.FormatPercent(drive.TotalBytes > 0 ? (double)bytes / drive.TotalBytes : 0, provider)} {name}";

        var parts = items.Select(i => Part(i.File!.SizeBytes, i.Name))
            .Append(Part(other, "Windows & other"))
            .Append(Part(drive.FreeBytes, "free"));
        return $"Drive {drive.Label} {string.Join(", ", parts)}";
    }
}
