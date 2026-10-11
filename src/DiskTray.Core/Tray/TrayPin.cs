namespace DiskTray.Core.Tray;

public enum PinKind
{
    /// <summary>Docker Desktop's data disk.</summary>
    Docker,

    /// <summary>A WSL distro's disk. No target means the primary distro.</summary>
    Distro,

    /// <summary>A whole drive. No target means the system drive.</summary>
    Drive,

    /// <summary>Any file or folder, added by hand.</summary>
    Path,
}

/// <summary>What a pinned item's tray icon shows.</summary>
public enum IconShows
{
    /// <summary>A disk file or path plus its drive's free space; a drive's breakdown.</summary>
    Default,

    /// <summary>A disk file or path as a share of its whole drive.</summary>
    ShareOfDrive,

    /// <summary>A drive's used and free space only.</summary>
    UsedVsFree,
}

public enum IconShape
{
    Pie,

    /// <summary>A vertical bar, filled from the bottom.</summary>
    Bar,
}

/// <summary>An item with its own tray icon, as chosen in Settings › Tray icons.</summary>
/// <param name="Target">The distro's ID, the drive's root (<c>D:\</c>) or the path; null for the default one.</param>
/// <param name="Name">The name to show, or null for the detected one.</param>
/// <param name="Color">"#RRGGBB", or null for the item's usual color.</param>
public sealed record TrayPin(PinKind Kind, string? Target = null, string? Name = null, string? Color = null, IconShows Shows = IconShows.Default)
{
    public const int MaxPins = 3;

    /// <summary>The Docker data disk, the primary distro and the system drive.</summary>
    public static readonly IReadOnlyList<TrayPin> Defaults = [new(PinKind.Docker), new(PinKind.Distro), new(PinKind.Drive)];

    /// <summary>Whether this pin and <paramref name="other"/> pick the same item.</summary>
    public bool SameItem(TrayPin other) =>
        Kind == other.Kind && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase);

    /// <summary>What <see cref="Shows"/> can be for this kind of item.</summary>
    public IReadOnlyList<IconShows> ShowsChoices =>
        Kind == PinKind.Drive ? [IconShows.Default, IconShows.UsedVsFree] : [IconShows.Default, IconShows.ShareOfDrive];

    /// <summary>
    /// Pins that make sense: no more than <see cref="MaxPins"/>, no repeats, colors that
    /// parse and icon choices that fit the kind. Falls back to <see cref="Defaults"/> when
    /// none are left, so there's always an icon to right-click.
    /// </summary>
    public static IReadOnlyList<TrayPin> Clean(IEnumerable<TrayPin?>? pins)
    {
        var kept = new List<TrayPin>();
        foreach (var pin in pins ?? [])
        {
            if (pin is null || !Enum.IsDefined(pin.Kind) || kept.Any(pin.SameItem)
                || (pin.Kind == PinKind.Path && string.IsNullOrWhiteSpace(pin.Target)))
            {
                continue;
            }

            var clean = pin with
            {
                Target = string.IsNullOrWhiteSpace(pin.Target) ? null : pin.Target,
                Name = string.IsNullOrWhiteSpace(pin.Name) ? null : pin.Name.Trim(),
                Color = pin.Color is { } color && IsColor(color) ? color.ToUpperInvariant() : null,
            };
            if (!clean.ShowsChoices.Contains(clean.Shows))
            {
                clean = clean with { Shows = IconShows.Default };
            }

            kept.Add(clean);
            if (kept.Count == MaxPins)
            {
                break;
            }
        }

        return kept.Count > 0 ? kept : Defaults;
    }

    public static bool IsColor(string text)
    {
        try
        {
            Rgb.FromHex(text);
            return text.StartsWith('#');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
