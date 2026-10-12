using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiskTray.Core.Detection;

namespace DiskTray.Core.Cleanup;

/// <summary>A volume no container uses, so <c>docker volume rm</c> can remove it.</summary>
/// <param name="Project">The Compose project it belongs to, from <c>com.docker.compose.project</c>.</param>
/// <param name="ShortName">Its name within the project, from <c>com.docker.compose.volume</c>.</param>
public sealed record DockerVolume(string Name, long SizeBytes, string? Project = null, string? ShortName = null);

/// <summary>A container, running or stopped, with what it says about where it came from.</summary>
public sealed record DockerContainer(
    string Name,
    bool Running,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> BindSources);

/// <summary>What a prune could remove, from <c>docker system df -v</c>.</summary>
/// <param name="BuildCacheBytes">Build cache that isn't in use or shared, as Docker counts it reclaimable.</param>
/// <param name="DanglingImageBytes">Untagged images no container uses.</param>
public sealed record DockerDiskUsage(
    long BuildCacheBytes,
    int DanglingImages,
    long DanglingImageBytes,
    IReadOnlyList<DockerVolume> UnusedVolumes);

/// <summary>The Docker commands DiskTray runs, and reading what they print.</summary>
public static partial class DockerOutput
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string VolumeLabel = "com.docker.compose.volume";
    public const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    public const string LocalFolderLabel = "devcontainer.local_folder";

    public static readonly IReadOnlyList<string> DiskUsageCommand = ["system", "df", "-v", "--format", "json"];

    /// <summary>Every container's ID, one per line, for <see cref="InspectCommand"/>.</summary>
    public static readonly IReadOnlyList<string> ContainerIdsCommand = ["ps", "--all", "--quiet", "--no-trunc"];

    /// <summary><c>docker desktop status --format json</c>; <see cref="ParseDesktopStatus"/> reads it.</summary>
    public static readonly IReadOnlyList<string> DesktopStatusCommand = ["desktop", "status", "--format", "json"];

    public static readonly IReadOnlyList<string> DesktopStartCommand = ["desktop", "start", "--detach"];

    public static readonly IReadOnlyList<string> PruneBuildCacheCommand = ["builder", "prune", "--force"];

    public static readonly IReadOnlyList<string> PruneDanglingImagesCommand = ["image", "prune", "--force"];

    /// <summary>Stops Docker Desktop, waiting up to <paramref name="timeout"/> for it.</summary>
    public static IReadOnlyList<string> DesktopStopCommand(TimeSpan timeout) =>
        ["desktop", "stop", "--timeout", ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)];

    public static IReadOnlyList<string> InspectCommand(IEnumerable<string> ids) => ["inspect", .. ids];

    /// <summary>Removes the volumes by name. Never <c>volume prune</c>, which would take every unused one.</summary>
    public static IReadOnlyList<string> RemoveVolumesCommand(IEnumerable<string> names) => ["volume", "rm", .. names];

    private static readonly Dictionary<string, double> Units = new(StringComparer.Ordinal)
    {
        ["B"] = 1,
        ["kB"] = 1e3,
        ["KB"] = 1e3,
        ["MB"] = 1e6,
        ["GB"] = 1e9,
        ["TB"] = 1e12,
        ["PB"] = 1e15,
    };

    /// <summary>
    /// Reads a size the way Docker prints it ("3.111GB", "21.13kB", "0B"), in 1000-based units.
    /// Null for "N/A" or anything else it can't read.
    /// </summary>
    public static long? ParseSize(string? text)
    {
        if (text is null || SizePattern().Match(text.Trim()) is not { Success: true } match
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !Units.TryGetValue(match.Groups[2].Value, out double unit))
        {
            return null;
        }

        return (long)Math.Round(number * unit);
    }

    /// <summary>
    /// Reads Docker's "key=value,key=value" label lists. A piece without "=" belongs to the
    /// value before it, since values such as Compose's config file list can hold commas.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseLabels(string? labels)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? last = null;
        foreach (string piece in (labels ?? "").Split(','))
        {
            int equals = piece.IndexOf('=');
            if (equals > 0)
            {
                last = piece[..equals];
                result[last] = piece[(equals + 1)..];
            }
            else if (last is not null)
            {
                result[last] += "," + piece;
            }
        }

        return result;
    }

    /// <summary>Reads <c>docker system df -v --format json</c>, or null if it isn't that.</summary>
    public static DockerDiskUsage? ParseDiskUsage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            long buildCache = Items(root, "BuildCache")
                .Where(b => Text(b, "InUse") == "false" && Text(b, "Shared") == "false")
                .Sum(b => ParseSize(Text(b, "Size")) ?? 0);

            // Dangling images that a container still uses stay, so they don't count.
            var dangling = Items(root, "Images")
                .Where(i => Text(i, "Repository") == "<none>" && Text(i, "Tag") == "<none>" && Text(i, "Containers") == "0")
                .ToList();
            long danglingBytes = dangling.Sum(i => ParseSize(Text(i, "UniqueSize")) ?? ParseSize(Text(i, "Size")) ?? 0);

            var volumes = new List<DockerVolume>();
            foreach (var volume in Items(root, "Volumes"))
            {
                // Links counts containers that use the volume, stopped ones included.
                if (Text(volume, "Links") != "0" || Text(volume, "Name") is not { Length: > 0 } name)
                {
                    continue;
                }

                var labels = ParseLabels(Text(volume, "Labels"));
                volumes.Add(new DockerVolume(
                    name,
                    ParseSize(Text(volume, "Size")) ?? 0,
                    labels.GetValueOrDefault(ProjectLabel) is { Length: > 0 } project ? project : null,
                    labels.GetValueOrDefault(VolumeLabel) is { Length: > 0 } shortName ? shortName : null));
            }

            return new DockerDiskUsage(buildCache, dangling.Count, danglingBytes, volumes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>docker inspect</c> on containers. Empty if it can't.</summary>
    public static IReadOnlyList<DockerContainer> ParseContainers(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var containers = new List<DockerContainer>();
            foreach (var container in document.RootElement.EnumerateArray())
            {
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (container.TryGetProperty("Config", out var config)
                    && config.ValueKind == JsonValueKind.Object
                    && config.TryGetProperty("Labels", out var labelMap)
                    && labelMap.ValueKind == JsonValueKind.Object)
                {
                    foreach (var label in labelMap.EnumerateObject())
                    {
                        if (label.Value.ValueKind == JsonValueKind.String)
                        {
                            labels[label.Name] = label.Value.GetString()!;
                        }
                    }
                }

                var binds = Items(container, "Mounts")
                    .Where(m => Text(m, "Type") == "bind" && Text(m, "Source") is { Length: > 0 })
                    .Select(m => Text(m, "Source")!)
                    .ToList();

                bool running = container.TryGetProperty("State", out var state)
                    && state.ValueKind == JsonValueKind.Object
                    && Text(state, "Status") == "running";

                containers.Add(new DockerContainer((Text(container, "Name") ?? "").TrimStart('/'), running, labels, binds));
            }

            return containers;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The IDs printed by <see cref="ContainerIdsCommand"/>.</summary>
    public static IReadOnlyList<string> ParseIds(string output) =>
        output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    /// <summary>
    /// What <see cref="DesktopStatusCommand"/> says: false for "stopped", true for anything
    /// else it reports ("running", "starting", "paused" by Resource Saver), null if it isn't
    /// that output.
    /// </summary>
    public static bool? ParseDesktopStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && Text(document.RootElement, "Status") is { Length: > 0 } status
                ? !string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether Docker Desktop is running, or null when that can't be told, which callers
    /// must treat as "maybe": not installed means no; otherwise what
    /// <see cref="DesktopStatusCommand"/> printed, or failing that whether its WSL distros
    /// are running.
    /// </summary>
    /// <param name="statusOutput">What the status command printed, or null if it failed.</param>
    /// <param name="distros">The running distros, or null if WSL couldn't be asked.</param>
    public static bool? DesktopRunning(bool installed, string? statusOutput, IReadOnlySet<string>? distros)
    {
        if (!installed)
        {
            return false;
        }

        if (statusOutput is not null && ParseDesktopStatus(statusOutput) is { } running)
        {
            return running;
        }

        return distros?.Any(WslDistro.IsDockerOwnedName);
    }

    /// <summary>
    /// How much a prune says it freed: <c>image prune</c>'s "Total reclaimed space: 1.2GB"
    /// or <c>builder prune</c>'s "Total: 1.2GB". Null if it doesn't say.
    /// </summary>
    public static long? ParseReclaimed(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            if (ReclaimedPattern().Match(line.Trim()) is { Success: true } match)
            {
                return ParseSize(match.Groups[1].Value);
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    /// <summary>A field as text. Numbers and true/false come back as Docker prints them, in case a version stops quoting them.</summary>
    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            }
            : null;

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]+)$")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^Total(?: reclaimed space)?:\s*(\S+)$")]
    private static partial Regex ReclaimedPattern();
}
