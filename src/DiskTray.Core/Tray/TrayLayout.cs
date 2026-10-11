using System.Globalization;

namespace DiskTray.Core.Tray;

/// <summary>One tray icon: a stable key, the pie it draws and its tooltip.</summary>
public sealed record TrayIconModel(string Key, PieChart Chart, string ToolTip);

/// <summary>
/// Decides which tray icons to show and what they say: the Docker data disk, the primary
/// WSL distro, and a breakdown of the system drive.
/// </summary>
public static class TrayLayout
{
    public const string DriveKey = "drive";

    /// <summary>Windows cuts tray tooltips off at 127 characters.</summary>
    public const int MaxToolTipLength = 127;

    public static IReadOnlyList<TrayIconModel> Build(DiskSnapshot snapshot, AlertThresholds thresholds, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        var icons = new List<TrayIconModel>();
        foreach (var file in PinnedFiles(snapshot))
        {
            if (snapshot.DriveOf(file) is { } drive)
            {
                icons.Add(DiskIcon(file, drive, thresholds, provider));
            }
        }

        icons.Add(DriveIcon(snapshot, thresholds, provider));
        return icons;
    }

    /// <summary>The disk files that get their own icon and their own slice of the drive icon.</summary>
    public static IEnumerable<DiskFile> PinnedFiles(DiskSnapshot snapshot) =>
        new[] { snapshot.Docker, snapshot.PrimaryDistro }.OfType<DiskFile>();

    /// <summary>The disk file plus its drive's free space: used, reclaimable (when known) and free.</summary>
    internal static TrayIconModel DiskIcon(DiskFile file, DriveSpace drive, AlertThresholds thresholds, IFormatProvider provider)
    {
        var color = Palette.For(file.Kind);
        var amounts = file.UsedBytes is { } used
            ? new[] { (Math.Min(used, file.SizeBytes), color.Main), (file.ReclaimableBytes ?? 0, color.Shade), (drive.FreeBytes, Palette.Free) }
            : new[] { (file.SizeBytes, color.Main), (drive.FreeBytes, Palette.Free) };

        string sizeLine = $"{file.Name} · {ByteSize.Format(file.SizeBytes, provider)}";
        if (file.ReclaimableBytes is { } reclaimable)
        {
            sizeLine += $" ({ByteSize.Format(reclaimable, provider)} reclaimable)";
        }

        return new TrayIconModel(
            file.Id,
            PieChart.FromAmounts(amounts, thresholds.Classify(drive)),
            ToolTip(sizeLine, FreeLine(drive, thresholds, provider)));
    }

    /// <summary>The whole system drive: each pinned file, Windows &amp; other, and free.</summary>
    internal static TrayIconModel DriveIcon(DiskSnapshot snapshot, AlertThresholds thresholds, IFormatProvider provider)
    {
        var drive = snapshot.SystemDrive;
        var onDrive = PinnedFiles(snapshot)
            .Where(f => string.Equals(f.DriveRoot, drive.Root, StringComparison.OrdinalIgnoreCase))
            .ToList();
        long other = Math.Max(0, drive.TotalBytes - drive.FreeBytes - onDrive.Sum(f => f.SizeBytes));

        var amounts = onDrive.Select(f => (f.SizeBytes, Palette.For(f.Kind).Main))
            .Append((other, Palette.Other))
            .Append((drive.FreeBytes, Palette.Free));

        string breakdown = string.Join(" · ", onDrive
            .Select(f => $"{ShortName(f)} {ByteSize.Format(f.SizeBytes, provider)}")
            .Append($"Other {ByteSize.Format(other, provider)}"));

        return new TrayIconModel(
            DriveKey,
            PieChart.FromAmounts(amounts, thresholds.Classify(drive)),
            ToolTip($"Drive {drive.Label} · {ByteSize.Format(drive.TotalBytes, provider)}", breakdown, FreeLine(drive, thresholds, provider)));
    }

    /// <summary>"C: 50 GB free (11%)", plus "– below 15% warning" when low.</summary>
    internal static string FreeLine(DriveSpace drive, AlertThresholds thresholds, IFormatProvider provider)
    {
        string line = $"{drive.Label} {ByteSize.Format(drive.FreeBytes, provider)} free ({ByteSize.FormatPercent(drive.FreeFraction, provider)})";
        return thresholds.Classify(drive) switch
        {
            AlertLevel.Critical => line + $" – below {ByteSize.FormatPercent(thresholds.CriticalFreeFraction, provider)} critical",
            AlertLevel.Warning => line + $" – below {ByteSize.FormatPercent(thresholds.WarningFreeFraction, provider)} warning",
            _ => line,
        };
    }

    private static string ShortName(DiskFile file) => file.Kind == DiskKind.DockerData ? "Docker" : file.Name;

    /// <summary>
    /// Joins lines, dropping middle lines (then cutting the text) until it fits, so the
    /// first and last lines stay visible.
    /// </summary>
    public static string ToolTip(params string[] lines)
    {
        var kept = lines.ToList();
        string text = string.Join('\n', kept);
        while (text.Length > MaxToolTipLength && kept.Count > 2)
        {
            kept.RemoveAt(kept.Count - 2);
            text = string.Join('\n', kept);
        }

        return text.Length <= MaxToolTipLength ? text : text[..(MaxToolTipLength - 1)] + "…";
    }
}
