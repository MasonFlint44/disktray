using System.Text.Json;

namespace DiskTray.Core.Cleanup;

/// <summary>
/// Which folder each Compose project came from, remembered from containers seen over time,
/// so a volume can still be traced to its folder after its containers are gone. Saved as
/// projects.json.
/// </summary>
public sealed class ProjectFolderCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The folder remembered for the project. Entries saved before guesses were marked count
    /// as guessed when they're in a distro, to be safe.
    /// </summary>
    public ProjectFolder? Get(string project) =>
        _entries.TryGetValue(project, out var entry)
            ? new ProjectFolder(entry.Distro, entry.Path) { DistroGuessed = entry.DistroGuessed ?? entry.Distro is not null }
            : null;

    /// <summary>When a container last showed the project at its remembered folder, or null when it isn't remembered.</summary>
    public DateTimeOffset? SeenAt(string project) => _entries.TryGetValue(project, out var entry) ? entry.SeenAt : null;

    /// <summary>How long a project that has nothing left in Docker is remembered.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(180);

    /// <summary>Sightings of a known folder are only moved on this often, so opening Clean up doesn't rewrite the file each time.</summary>
    private static readonly TimeSpan SeenPrecision = TimeSpan.FromDays(1);

    /// <summary>
    /// Remembers the folders, and when each was seen, and says whether anything changed. A
    /// folder whose distro was guessed never replaces one that was recorded, though it still
    /// counts as seeing the project there.
    /// </summary>
    public bool Record(IReadOnlyDictionary<string, ProjectFolder> folders, DateTimeOffset now)
    {
        bool changed = false;
        foreach (var (project, folder) in folders)
        {
            var known = Get(project);
            if (folder.DistroGuessed && known is { DistroGuessed: false } && known != folder)
            {
                continue;
            }

            if (known != folder || (known.DistroGuessed && !folder.DistroGuessed))
            {
                _entries[project] = new Entry(folder.Distro, folder.Path, now, folder.DistroGuessed);
                changed = true;
            }
            else if (now - _entries[project].SeenAt >= SeenPrecision || now < _entries[project].SeenAt)
            {
                _entries[project] = _entries[project] with { SeenAt = now };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Forgets projects not seen for <see cref="ForgetAfter"/> that have nothing left in Docker
    /// (no containers, no unused volumes), and says whether any were.
    /// </summary>
    /// <param name="inDocker">The projects that have containers or volumes now.</param>
    public bool Forget(IEnumerable<string> inDocker, DateTimeOffset now)
    {
        var keep = inDocker.ToHashSet(StringComparer.Ordinal);
        var gone = _entries.Where(e => !keep.Contains(e.Key) && now - e.Value.SeenAt >= ForgetAfter).Select(e => e.Key).ToList();
        foreach (string project in gone)
        {
            _entries.Remove(project);
        }

        return gone.Count > 0;
    }

    public string ToJson() =>
        JsonSerializer.Serialize(new SortedDictionary<string, Entry>(_entries, StringComparer.Ordinal), Json);

    /// <summary>Reads a saved cache. A file that can't be read gives an empty one.</summary>
    public static ProjectFolderCache FromJson(string? json)
    {
        var cache = new ProjectFolderCache();
        if (string.IsNullOrWhiteSpace(json))
        {
            return cache;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, Json);
            foreach (var (project, entry) in entries ?? [])
            {
                if (!string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(entry?.Path))
                {
                    cache._entries[project] = entry;
                }
            }
        }
        catch (JsonException)
        {
        }

        return cache;
    }

    /// <param name="SeenAt">When a container last showed the project here. In files saved before that was kept up to date, when it first did.</param>
    /// <param name="DistroGuessed">Null in files saved before this was recorded.</param>
    private sealed record Entry(string? Distro, string Path, DateTimeOffset SeenAt, bool? DistroGuessed = null);
}
