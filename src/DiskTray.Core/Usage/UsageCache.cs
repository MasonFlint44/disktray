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

    /// <summary>When each disk file was last among those detected, which can be long after its last reading.</summary>
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);

    public UsageReading? Get(string path) => _readings.GetValueOrDefault(path);

    public void Set(string path, UsageReading reading) => _readings[path] = reading;

    /// <summary>How long a reading for a disk file that's no longer seen is kept, in case it comes back.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Forgets disk files that haven't been seen for <see cref="ForgetAfter"/>. Not at once,
    /// since one can be missing for a while: Docker or WSL detection switched off, or a
    /// distro moved.
    /// </summary>
    /// <param name="paths">The disk files detected now.</param>
    public void RetainOnly(IEnumerable<string> paths, DateTimeOffset now)
    {
        var keep = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in keep.Where(_readings.ContainsKey))
        {
            _seen[path] = now;
        }

        // A file saved before sightings were kept counts as seen when it was measured.
        foreach (string path in _readings.Keys.Where(p => !keep.Contains(p) && now - LastSeen(p) >= ForgetAfter).ToList())
        {
            _readings.Remove(path);
            _seen.Remove(path);
        }
    }

    private DateTimeOffset LastSeen(string path) =>
        _seen.TryGetValue(path, out var seen) && seen > _readings[path].MeasuredAt ? seen : _readings[path].MeasuredAt;

    public string ToJson() => JsonSerializer.Serialize(new CacheFile(_readings, _seen.Where(s => _readings.ContainsKey(s.Key)).ToDictionary(StringComparer.OrdinalIgnoreCase)), Json);

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
            if (JsonSerializer.Deserialize<CacheFile>(json, Json) is { Disks: { } disks } file)
            {
                foreach (var (path, reading) in disks)
                {
                    if (reading is { UsedBytes: >= 0, FileSizeBytes: >= 0 })
                    {
                        cache.Set(path, reading);
                    }
                }

                foreach (var (path, seen) in file.Seen ?? new Dictionary<string, DateTimeOffset>())
                {
                    if (cache._readings.ContainsKey(path))
                    {
                        cache._seen[path] = seen;
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
        [property: JsonPropertyName("disks")] IReadOnlyDictionary<string, UsageReading>? Disks,
        [property: JsonPropertyName("seen")] IReadOnlyDictionary<string, DateTimeOffset>? Seen = null);
}
