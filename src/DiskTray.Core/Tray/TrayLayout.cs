using System.Globalization;

namespace DiskTray.Core.Tray;

/// <summary>One tray icon: a stable key, the pie it draws and its tooltip.</summary>
public sealed record TrayIconModel(string Key, PieChart Chart, string ToolTip);

/// <summary>
/// A pinned item matched to what's on the machine: a disk file or path (<see cref="File"/>),
/// or a whole drive (<see cref="Drive"/> with no file).
/// </summary>
/// <param name="Key">Stays the same across refreshes, since Windows remembers each tray icon by it.</param>
/// <param name="Missing">Why there's nothing to show yet, or null when there is.</param>
public sealed record TrayItem(TrayPin Pin, string Key, string Name, ItemColor Color, DiskFile? File, DriveSpace? Drive, string? Missing)
{
    public bool IsDrive => Pin.Kind == PinKind.Drive;
}

/// <summary>
/// Decides which tray icons to show and what they say: one per pinned item, by default the
/// Docker data disk, the primary WSL distro, and a breakdown of the system drive.
/// </summary>
public static class TrayLayout
{
    public const string DriveKey = "drive";

    private const string TurnedOff = "Turned off in Settings › Detection";

    /// <summary>Windows cuts tray tooltips off at 127 characters.</summary>
    public const int MaxToolTipLength = 127;

    /// <param name="pins">What's pinned, or null for <see cref="TrayPin.Defaults"/>.</param>
    /// <param name="zone">The time zone for "as of" times. Defaults to the local one.</param>
    public static IReadOnlyList<TrayIconModel> Build(
        DiskSnapshot snapshot,
        AlertThresholds thresholds,
        IFormatProvider? provider = null,
        TimeZoneInfo? zone = null,
        IReadOnlyList<TrayPin>? pins = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        zone ??= TimeZoneInfo.Local;
        var items = Resolve(snapshot, pins ?? TrayPin.Defaults);
        return items.Select(item => Icon(item, items, thresholds, provider, snapshot.TakenAt, zone)).ToList();
    }

    /// <summary>Matches each pin to the disk file, path or drive it means.</summary>
    public static IReadOnlyList<TrayItem> Resolve(DiskSnapshot snapshot, IReadOnlyList<TrayPin> pins)
    {
        var items = new List<TrayItem>();
        int paths = 0;
        foreach (var pin in pins)
        {
            ItemColor Colored(ItemColor usual) => pin.Color is { } hex ? new ItemColor(Rgb.FromHex(hex)) : usual;
            switch (pin.Kind)
            {
                case PinKind.Docker:
                {
                    var file = snapshot.Docker;
                    items.Add(new TrayItem(pin, "docker", pin.Name ?? "Docker data", Colored(Palette.Docker), file, Drive(snapshot, file),
                        file is not null ? null
                        : snapshot.NotDetected.Contains(DiskKind.DockerData) ? TurnedOff
                        : "Docker Desktop's data disk wasn't found"));
                    break;
                }

                case PinKind.Distro:
                {
                    var file = pin.Target is null
                        ? snapshot.PrimaryDistro
                        : snapshot.Files.FirstOrDefault(f => f.Kind == DiskKind.WslDistro && string.Equals(f.Id, pin.Target, StringComparison.OrdinalIgnoreCase));
                    items.Add(new TrayItem(pin, file?.Id ?? "distro:" + (pin.Target ?? "primary"), pin.Name ?? file?.Name ?? "WSL distro",
                        Colored(Palette.Wsl), file, Drive(snapshot, file),
                        file is not null ? null
                        : snapshot.NotDetected.Contains(DiskKind.WslDistro) ? TurnedOff
                        : "The WSL distro wasn't found"));
                    break;
                }

                case PinKind.Drive:
                {
                    var drive = pin.Target is null
                        ? snapshot.SystemDrive
                        : snapshot.Drives.FirstOrDefault(d => string.Equals(d.Root, pin.Target, StringComparison.OrdinalIgnoreCase));
                    string root = drive?.Root ?? pin.Target!;
                    bool system = string.Equals(root, snapshot.SystemDrive.Root, StringComparison.OrdinalIgnoreCase);
                    items.Add(new TrayItem(pin, system ? DriveKey : "drive:" + root.ToUpperInvariant(), pin.Name ?? $"Drive {root.TrimEnd('\\')}",
                        Colored(new ItemColor(Palette.Other)), null, drive, drive is null ? "The drive isn't available" : null));
                    break;
                }

                default:
                {
                    string id = Detection.DiskScanner.PathId(pin.Target!);
                    var file = snapshot.Files.FirstOrDefault(f => f.Id == id);
                    var usual = Palette.Extras[paths++ % Palette.Extras.Count];
                    string? missing = file is null ? $"{pin.Target} wasn't found"
                        : file is { Kind: DiskKind.Folder, SizedAsOf: null } ? "Adding up the folder's size…"
                        : null;
                    items.Add(new TrayItem(pin, id, pin.Name ?? Detection.WindowsPath.GetName(pin.Target!), Colored(usual), file, Drive(snapshot, file), missing));
                    break;
                }
            }
        }

        // Two pins can end up meaning the same thing (the default distro pinned both ways);
        // only the first gets an icon, since the second would share its key.
        return items.DistinctBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The disk files that get compacted and count as reclaimable, whatever is pinned:
    /// Docker's data disk and the primary distro's, and any other distro (not Docker
    /// Desktop's own) once it's been measured holding space it doesn't use, which is when
    /// the flyout shows it as reclaimable. Shutting down WSL frees them all at once.
    /// </summary>
    public static IEnumerable<DiskFile> CompactableFiles(DiskSnapshot snapshot) =>
        snapshot.Files.Where(f => f.Kind == DiskKind.DockerData
            || f.Id == snapshot.PrimaryDistroId
            || (f.Kind == DiskKind.WslDistro && !Detection.WslDistro.IsDockerOwnedName(f.Name) && f.ReclaimableBytes > 0));

    /// <summary>
    /// The pinned files and paths on <paramref name="drive"/>, largest first, which get their
    /// own slices of it. Each part of the drive is counted once, so the slices never add up
    /// past what's used: a file pinned twice (as a detected disk and by its path) keeps its
    /// first, and one inside a pinned folder is left to the folder.
    /// </summary>
    public static IReadOnlyList<TrayItem> ItemsOn(IEnumerable<TrayItem> items, DriveSpace drive)
    {
        var on = items.Where(i => !i.IsDrive && i.Missing is null && i.File is not null
                && string.Equals(i.File.DriveRoot, drive.Root, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var counted = new List<TrayItem>();
        for (int i = 0; i < on.Count; i++)
        {
            var file = on[i].File!;
            bool already = on.Where((_, j) => j != i).Any(other => Inside(file, other.File!))
                || on.Take(i).Any(other => string.Equals(other.File!.Path, file.Path, StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                counted.Add(on[i]);
            }
        }

        return counted.OrderByDescending(i => i.File!.SizeBytes).ToList();

        static bool Inside(DiskFile file, DiskFile folder) =>
            folder.Kind == DiskKind.Folder
            && file.Path.StartsWith(folder.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Everything on the drive that isn't free or one of <paramref name="files"/>.</summary>
    public static long OtherBytes(DriveSpace drive, IEnumerable<DiskFile> files) =>
        Math.Max(0, drive.TotalBytes - drive.FreeBytes - files.Sum(f => f.SizeBytes));

    private static TrayIconModel Icon(TrayItem item, IReadOnlyList<TrayItem> all, AlertThresholds thresholds, IFormatProvider provider, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (item.Missing is not null || item.Drive is not { } drive)
        {
            return new TrayIconModel(item.Key, new PieChart([new PieSlice(1, Palette.Free)], AlertLevel.Normal), ToolTip(item.Name, item.Missing ?? "Not found"));
        }

        return item.File is { } file
            ? FileIcon(item, file, drive, thresholds, provider, now, zone)
            : DriveIcon(item, drive, all, thresholds, provider);
    }

    /// <summary>
    /// A disk file or path plus its drive's free space: used, reclaimable (when known) and free.
    /// As a share of the drive, the rest of what's used on the drive goes in between.
    /// </summary>
    private static TrayIconModel FileIcon(TrayItem item, DiskFile file, DriveSpace drive, AlertThresholds thresholds, IFormatProvider provider, DateTimeOffset now, TimeZoneInfo zone)
    {
        var color = item.Color;
        var amounts = new List<(long, Rgb)>();
        if (file.UsedBytes is { } used)
        {
            amounts.Add((Math.Min(used, file.SizeBytes), color.Main));
            amounts.Add((file.ReclaimableBytes ?? 0, color.Reclaimable));
        }
        else
        {
            amounts.Add((file.SizeBytes, color.Main));
        }

        if (item.Pin.Shows == IconShows.ShareOfDrive)
        {
            amounts.Add((OtherBytes(drive, [file]), Palette.Other));
        }

        amounts.Add((drive.FreeBytes, Palette.Free));

        string sizeLine = $"{item.Name} · {ByteSize.Format(file.SizeBytes, provider)}";
        if (file.ReclaimableBytes is { } reclaimable)
        {
            string asOf = file.UsedAsOf is { } measuredAt ? $" as of {AsOf(measuredAt, now, zone, provider)}" : "";
            sizeLine += $" ({ByteSize.Format(reclaimable, provider)} reclaimable{asOf})";
        }
        else if (file.SizedAsOf is { } sizedAt)
        {
            sizeLine += $" as of {AsOf(sizedAt, now, zone, provider)}";
        }

        return new TrayIconModel(
            item.Key,
            PieChart.FromAmounts(amounts, thresholds.Classify(drive)),
            ToolTip(sizeLine, FreeLine(drive, thresholds, provider)));
    }

    /// <summary>A whole drive: each pinned item on it, the rest of what's used, and free; or just used and free.</summary>
    private static TrayIconModel DriveIcon(TrayItem item, DriveSpace drive, IReadOnlyList<TrayItem> all, AlertThresholds thresholds, IFormatProvider provider)
    {
        var onDrive = item.Pin.Shows == IconShows.UsedVsFree ? [] : ItemsOn(all, drive);
        long other = OtherBytes(drive, onDrive.Select(i => i.File!));

        var amounts = onDrive.Select(i => (i.File!.SizeBytes, i.Color.Main))
            .Append((other, item.Color.Main))
            .Append((drive.FreeBytes, Palette.Free));

        string title = $"{item.Name} · {ByteSize.Format(drive.TotalBytes, provider)}";
        string freeLine = FreeLine(drive, thresholds, provider);
        if (onDrive.Count == 0)
        {
            return new TrayIconModel(item.Key, PieChart.FromAmounts(amounts, thresholds.Classify(drive)), ToolTip(title, freeLine));
        }

        string breakdown = string.Join(" · ", onDrive
            .Select(i => $"{ShortName(i)} {ByteSize.Format(i.File!.SizeBytes, provider)}")
            .Append($"Other {ByteSize.Format(other, provider)}"));

        return new TrayIconModel(
            item.Key,
            PieChart.FromAmounts(amounts, thresholds.Classify(drive)),
            ToolTip(title, breakdown, freeLine));
    }

    private static DriveSpace? Drive(DiskSnapshot snapshot, DiskFile? file) => file is null ? null : snapshot.DriveOf(file);

    /// <summary>"C: 50 GiB free (11%)", plus "– below 15% warning" when low.</summary>
    internal static string FreeLine(DriveSpace drive, AlertThresholds thresholds, IFormatProvider provider)
    {
        var level = thresholds.Classify(drive);
        string line = $"{drive.Label} {ByteSize.Format(drive.FreeBytes, provider)} free ({ByteSize.FormatPercent(drive.FreeFraction, provider, thresholds.FractionFor(level))})";
        return level switch
        {
            AlertLevel.Critical => line + $" – below {ByteSize.FormatPercent(thresholds.CriticalFreeFraction, provider)} critical",
            AlertLevel.Warning => line + $" – below {ByteSize.FormatPercent(thresholds.WarningFreeFraction, provider)} warning",
            _ => line,
        };
    }

    /// <summary>The time for a reading from today, or else the date: "2:14 PM", "Oct 9", "12/30/2025".</summary>
    internal static string AsOf(DateTimeOffset measuredAt, DateTimeOffset now, TimeZoneInfo zone, IFormatProvider provider)
    {
        var then = TimeZoneInfo.ConvertTime(measuredAt, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        if (then.Date == today.Date)
        {
            return then.ToString("t", provider);
        }

        return then.Year == today.Year ? then.ToString("MMM d", provider) : then.ToString("d", provider);
    }

    internal static string ShortName(DiskFile file) => file.Kind == DiskKind.DockerData ? "Docker" : file.Name;

    /// <summary>"Docker" for the usual Docker name, or else the item's name.</summary>
    internal static string ShortName(TrayItem item) =>
        item.Pin.Kind == PinKind.Docker && item.Pin.Name is null ? "Docker" : item.Name;

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

        if (text.Length <= MaxToolTipLength)
        {
            return text;
        }

        // Never between the two halves of a character outside the BMP (an emoji in a name).
        int cut = MaxToolTipLength - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }
}
