using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskTray.Core.Usage;

/// <summary>How much of a disk file was in use, and how big the file was at the time.</summary>
public sealed record UsageReading(long UsedBytes, long FileSizeBytes, DateTimeOffset MeasuredAt);

/// <summary>
/// The last usage reading for each disk file, by path, so the icons can still shade
/// reclaimable space while WSL or Docker isn't running. Saved as usage.json.
/// </summary>
public sealed class UsageCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Dictionary<string, UsageReading> _readings = new(StringComparer.OrdinalIgnoreCase);

    public UsageReading? Get(string path) => _readings.GetValueOrDefault(path);

    public void Set(string path, UsageReading reading) => _readings[path] = reading;

    /// <summary>How long a reading for a disk file that's no longer seen is kept, in case it comes back.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Forgets disk files that haven't been seen for <see cref="ForgetAfter"/>. Not at once,
    /// since one can be missing for a while: Docker or WSL detection switched off, or a
    /// distro moved.
    /// </summary>
    public void RetainOnly(IEnumerable<string> paths, DateTimeOffset now)
    {
        var keep = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _readings.Where(r => !keep.Contains(r.Key) && now - r.Value.MeasuredAt >= ForgetAfter).Select(r => r.Key).ToList())
        {
            _readings.Remove(path);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new CacheFile(_readings), Json);

    /// <summary>Reads a saved cache. Anything unreadable gives an empty cache, since it's only a fallback.</summary>
    public static UsageCache FromJson(string? json)
    {
        var cache = new UsageCache();
        if (string.IsNullOrWhiteSpace(json))
        {
            return cache;
        }

        try
        {
            if (JsonSerializer.Deserialize<CacheFile>(json, Json)?.Disks is { } disks)
            {
                foreach (var (path, reading) in disks)
                {
                    if (reading is { UsedBytes: >= 0, FileSizeBytes: >= 0 })
                    {
                        cache.Set(path, reading);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return cache;
    }

    private sealed record CacheFile(
        [property: JsonPropertyName("disks")] IReadOnlyDictionary<string, UsageReading>? Disks);
}
