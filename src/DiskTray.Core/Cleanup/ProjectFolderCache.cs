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

    /// <summary>
    /// Remembers the folders, and says whether anything changed. A folder whose distro was
    /// guessed never replaces one that was recorded.
    /// </summary>
    public bool Record(IReadOnlyDictionary<string, ProjectFolder> folders, DateTimeOffset now)
    {
        bool changed = false;
        foreach (var (project, folder) in folders)
        {
            var known = Get(project);
            if (folder.DistroGuessed && known is { DistroGuessed: false })
            {
                continue;
            }

            if (known != folder || known.DistroGuessed != folder.DistroGuessed)
            {
                _entries[project] = new Entry(folder.Distro, folder.Path, now, folder.DistroGuessed);
                changed = true;
            }
        }

        return changed;
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

    /// <param name="DistroGuessed">Null in files saved before this was recorded.</param>
    private sealed record Entry(string? Distro, string Path, DateTimeOffset SeenAt, bool? DistroGuessed = null);
}
