using System.Text.Json;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Detection;

public sealed record LargeFile(string Path, long SizeBytes);

/// <summary>
/// What the daily look through the user folder found: every file over <see cref="MinBytes"/>.
/// Saved as largefiles.json, so a restart doesn't scan again.
/// </summary>
public sealed record LargeFileScan(DateTimeOffset ScannedAt, long MinBytes, IReadOnlyList<LargeFile> Files)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>How many the flyout lists before "and N more".</summary>
    public const int Shown = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Whether it's time to look again: a day has passed, or the threshold went down, so
    /// smaller files would now count. A higher threshold just shows fewer.
    /// </summary>
    public bool IsDue(DateTimeOffset now, long minBytes) => now - ScannedAt >= Interval || minBytes < MinBytes;

    /// <summary>
    /// The files to list: at least <paramref name="minBytes"/>, largest first, leaving out
    /// the disk files DiskTray already shows and anything pinned.
    /// </summary>
    public IReadOnlyList<LargeFile> Others(DiskSnapshot snapshot, IReadOnlyList<TrayPin> pins, long minBytes)
    {
        var known = snapshot.Files.Select(f => f.Path)
            .Concat(pins.Where(p => p.Kind == PinKind.Path && p.Target is not null).Select(p => p.Target!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = pins.Where(p => p.Kind == PinKind.Path && p.Target is not null)
            .Select(p => p.Target!.TrimEnd('\\') + "\\")
            .ToList();
        return Files
            .Where(f => f.SizeBytes >= minBytes && !known.Contains(f.Path)
                && !folders.Any(folder => f.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.SizeBytes)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a saved scan, or null when there isn't a usable one (so it scans again).</summary>
    public static LargeFileScan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var scan = JsonSerializer.Deserialize<LargeFileScan>(json, Json);
            return scan?.Files is null ? null : scan with { Files = scan.Files.Where(f => f is { Path.Length: > 0 }).ToList() };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
