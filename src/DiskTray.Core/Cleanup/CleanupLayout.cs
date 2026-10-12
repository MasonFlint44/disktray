using System.Globalization;
using DiskTray.Core.Detection;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Cleanup;

/// <summary>When to compact the disks.</summary>
public enum CompactWhen
{
    /// <summary>Once Docker Desktop has quit and no distros are running, so nothing gets interrupted.</summary>
    WhenIdle,

    /// <summary>Stops Docker Desktop and shuts down WSL now.</summary>
    RightAway,
}

/// <summary>What the machine looks like when the Clean up dialog opens.</summary>
/// <param name="Docker">What a prune could remove, or null when Docker Desktop isn't running or didn't say.</param>
/// <param name="RunningDistros">The WSL distros running now, Docker's own included.</param>
public sealed record CleanupState(
    DiskSnapshot Snapshot,
    DockerDiskUsage? Docker,
    IReadOnlyList<VolumeGroup> VolumeGroups,
    IReadOnlySet<string> RunningDistros,
    int RunningContainers)
{
    /// <summary>Whether Docker said what can be pruned, which the prune step needs.</summary>
    public bool CanPrune => Docker is not null;

    /// <summary>
    /// Whether Docker Desktop is running, apart from whether it answered about pruning;
    /// null when it couldn't be told. Defaults to "running" when it answered.
    /// </summary>
    public bool? DesktopRunning { get; init; }

    /// <summary>False when WSL couldn't be asked what's running, so <see cref="RunningDistros"/> is a guess.</summary>
    public bool RunningKnown { get; init; } = true;

    /// <summary>Docker Desktop running, or it might be.</summary>
    public bool DesktopMayRun =>
        DesktopRunning ?? (Docker is not null || !RunningKnown || RunningDistros.Any(WslDistro.IsDockerOwnedName));
}

/// <summary>What the Clean up dialog starts with, from Settings › Clean up.</summary>
/// <param name="PickDeletedProjectVolumes">Pick the unused volumes of projects whose folder is gone.</param>
/// <param name="StartDockerAfter">
/// Start Docker Desktop after compacting right away, when it was running as the dialog opened.
/// </param>
public sealed record CleanupDefaults(
    bool BuildCache = true,
    bool DanglingImages = true,
    bool PickDeletedProjectVolumes = true,
    bool Compact = true,
    CompactWhen When = CompactWhen.WhenIdle,
    bool StartDockerAfter = true);

/// <summary>What's ticked in the Clean up dialog.</summary>
public sealed record CleanupChoices(
    bool Prune,
    bool BuildCache,
    bool DanglingImages,
    IReadOnlySet<string> Volumes,
    bool Compact,
    CompactWhen When,
    bool StartDockerAfter)
{
    /// <summary>
    /// What Settings says to start with; out of the box, both steps on, compacting when idle,
    /// with build cache, dangling images and the volumes of deleted projects picked. Pruning
    /// needs Docker Desktop running. Starting it again afterwards: see <see cref="StartDockerDefault"/>.
    /// </summary>
    public static CleanupChoices Defaults(CleanupState state, CleanupDefaults? defaults = null)
    {
        defaults ??= new CleanupDefaults();
        return new(
            Prune: state.CanPrune && (defaults.BuildCache || defaults.DanglingImages || defaults.PickDeletedProjectVolumes),
            BuildCache: defaults.BuildCache,
            DanglingImages: defaults.DanglingImages,
            Volumes: defaults.PickDeletedProjectVolumes
                ? state.VolumeGroups.Where(g => g.PreSelected).SelectMany(g => g.Volumes).Select(v => v.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            Compact: defaults.Compact,
            When: defaults.When,
            StartDockerAfter: StartDockerDefault(state, defaults, defaults.When));
    }

    /// <summary>
    /// Whether "Start Docker Desktop afterwards" starts ticked: only when compacting right
    /// away, which stops Docker Desktop itself, and it's running now. When compacting waits
    /// for WSL to go idle, the user is the one who quits Docker, perhaps for the day, so it
    /// isn't started again unless they tick it.
    /// </summary>
    public static bool StartDockerDefault(CleanupState state, CleanupDefaults defaults, CompactWhen when) =>
        defaults.StartDockerAfter && when == CompactWhen.RightAway && (state.DesktopRunning ?? state.CanPrune);
}

/// <summary>A disk file to compact, with roughly how big it'll be afterwards when that's known.</summary>
public sealed record CompactTarget(DiskFile File, long? AfterBytes)
{
    public long? GainBytes => AfterBytes is { } after ? File.SizeBytes - after : null;
}

/// <summary>A heading over a group of volumes, and whether it's a warning (a deleted folder).</summary>
public sealed record VolumeGroupLine(string Text, bool Warning, IReadOnlyList<VolumeLine> Volumes);

public sealed record VolumeLine(string Name, string Label, string Size);

/// <summary>Everything the Clean up dialog shows, already worded.</summary>
/// <param name="PruneUnavailable">Why the prune step can't be used, or null when it can.</param>
/// <param name="Warning">For <see cref="CompactWhen.RightAway"/>: the heading, then what gets closed.</param>
public sealed record CleanupModel(
    string PruneDetail,
    string PruneTotal,
    string? PruneUnavailable,
    string BuildCacheSize,
    string DanglingImagesSize,
    IReadOnlyList<VolumeGroupLine> VolumeGroups,
    string CompactDetail,
    string CompactGain,
    IReadOnlyList<string> Warning,
    string Summary,
    string Plan,
    bool CanStart,
    IReadOnlyList<CompactTarget> ToCompact);

/// <summary>Works out the Clean up dialog from what's on the machine and what's ticked.</summary>
public static class CleanupLayout
{
    public const string IdleNote = "Nothing gets interrupted. Starts once you've quit Docker Desktop and closed your WSL terminals.";

    public static CleanupModel Build(CleanupState state, CleanupChoices choices, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        string Size(long bytes) => ByteSize.Format(bytes, provider);

        var docker = state.Docker;
        bool prune = choices.Prune && docker is not null;
        var volumes = docker?.UnusedVolumes.Where(v => choices.Volumes.Contains(v.Name)).ToList() ?? [];
        long buildCache = prune && choices.BuildCache ? docker!.BuildCacheBytes : 0;
        long dangling = prune && choices.DanglingImages ? docker!.DanglingImageBytes : 0;
        long volumeBytes = prune ? volumes.Sum(v => v.SizeBytes) : 0;
        long pruneBytes = buildCache + dangling + volumeBytes;
        bool pruneHasWork = prune && (choices.BuildCache || choices.DanglingImages || volumes.Count > 0);

        var targets = choices.Compact ? Targets(state.Snapshot, pruneBytes) : [];
        long gain = targets.Sum(t => t.GainBytes ?? 0);
        bool idle = choices.When == CompactWhen.WhenIdle;

        string summary;
        if (targets.Count > 0)
        {
            summary = gain > 0 ? $"{GainsLabel(state.Snapshot, targets)} gets back ~{Size(gain)}" : $"Compacts {Names(targets)}";
        }
        else
        {
            summary = pruneHasWork ? $"Frees {Size(pruneBytes)} inside Docker" : "Select a step";
        }

        string plan = (pruneHasWork, targets.Count > 0) switch
        {
            (true, true) => idle ? "Prunes now, compacts when WSL is idle" : "Prunes, then shuts down WSL and compacts",
            (true, false) => $"Prunes now · {state.Snapshot.SystemDrive.Label} gains space after a compact",
            (false, true) => idle ? "Compacts when WSL is idle" : "Shuts down WSL and compacts now",
            _ => "Nothing will run",
        };

        return new CleanupModel(
            PruneDetail(docker, choices, volumes, provider),
            docker is null ? "" : Size(pruneBytes),
            docker is not null ? null
                : state.DesktopRunning == true ? "Docker Desktop is running but didn't say what can be pruned. Try again in a minute."
                : "Start Docker Desktop to see what can be pruned. DiskTray never starts it on its own.",
            docker is null ? "" : Size(docker.BuildCacheBytes),
            docker is null ? "" : docker.DanglingImages == 0 ? "none" : Size(docker.DanglingImageBytes),
            state.VolumeGroups.Select(g => GroupLine(g, provider)).ToList(),
            CompactDetail(state.Snapshot, pruneBytes, provider),
            choices.Compact && gain > 0 ? Size(gain) : "",
            choices.Compact && !idle ? Warning(state) : [],
            summary,
            plan,
            pruneHasWork || targets.Count > 0,
            targets);
    }

    /// <summary>
    /// The disks to compact: Docker's data disk and the main distro's. Afterwards each is about
    /// as big as what's in use, less what the prune removes from Docker's.
    /// </summary>
    public static IReadOnlyList<CompactTarget> Targets(DiskSnapshot snapshot, long pruneBytes) =>
        TrayLayout.CompactableFiles(snapshot)
            .OrderByDescending(f => f.SizeBytes)
            .Select(f => new CompactTarget(
                f,
                f.UsedBytes is { } used
                    ? Math.Clamp(used - (f.Kind == DiskKind.DockerData ? pruneBytes : 0), 0, f.SizeBytes)
                    : null))
            .ToList();

    /// <summary>The heading over a group of unused volumes, e.g. "acmeweb-app · folder deleted (~/git/acme.web-app)".</summary>
    public static VolumeGroupLine GroupLine(VolumeGroup group, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        string where = group.Searched.Count > 0 ? string.Join(", ", group.Searched.Select(f => f.Display)) : "your code folders";
        string text = group.Check switch
        {
            FolderCheck.Deleted => $"{group.Project} · folder deleted ({group.Folder!.Display})",
            FolderCheck.Exists when group.Guessed => $"{group.Project} · folder exists ({group.Folder!.Display}, matched by name)",
            FolderCheck.Exists => $"{group.Project} · folder exists ({group.Folder!.Display})",
            FolderCheck.NotChecked when group.Folder is { Distro: { } distro } folder => $"{group.Project} · {folder.Display} not checked, {distro} isn't running",
            FolderCheck.NotChecked => $"{group.Project} · folder not checked, its distro isn't running",
            FolderCheck.NoMatch => $"{group.Project} · no matching folder in {where}",
            _ => "No project",
        };

        return new VolumeGroupLine(
            text,
            group.Check == FolderCheck.Deleted,
            group.Volumes.Select(v => new VolumeLine(v.Name, VolumeLabel(v), ByteSize.Format(v.SizeBytes, provider))).ToList());
    }

    /// <summary>
    /// How a volume is shown under its project: "…_sqlserver" for a Compose volume, the first
    /// 12 characters of an unnamed one's ID, or else its name.
    /// </summary>
    public static string VolumeLabel(DockerVolume volume)
    {
        if (volume.Project is { } project && volume.Name.StartsWith(project + "_", StringComparison.Ordinal))
        {
            return "…" + volume.Name[project.Length..];
        }

        bool anonymous = volume.Name.Length == 64 && volume.Name.All(char.IsAsciiHexDigitLower);
        return anonymous ? volume.Name[..12] + "…" : volume.Name;
    }

    /// <summary>"Build cache 12 GiB · 3 volumes 5.2 GiB", for what's ticked.</summary>
    private static string PruneDetail(DockerDiskUsage? docker, CleanupChoices choices, IReadOnlyList<DockerVolume> volumes, IFormatProvider provider)
    {
        if (docker is null)
        {
            return "Docker Desktop isn't running";
        }

        var parts = new List<string>();
        if (choices.BuildCache)
        {
            parts.Add($"Build cache {ByteSize.Format(docker.BuildCacheBytes, provider)}");
        }

        if (choices.DanglingImages && docker.DanglingImages > 0)
        {
            parts.Add($"{Count(docker.DanglingImages, "dangling image")} {ByteSize.Format(docker.DanglingImageBytes, provider)}");
        }

        if (volumes.Count > 0)
        {
            parts.Add($"{Count(volumes.Count, "volume")} {ByteSize.Format(volumes.Sum(v => v.SizeBytes), provider)}");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "Nothing picked";
    }

    /// <summary>"Docker 129 → ~16 GiB · Ubuntu 60 → ~35 GiB".</summary>
    private static string CompactDetail(DiskSnapshot snapshot, long pruneBytes, IFormatProvider provider)
    {
        var targets = Targets(snapshot, pruneBytes);
        if (targets.Count == 0)
        {
            return "No WSL or Docker disks found";
        }

        return string.Join(" · ", targets.Select(t =>
        {
            string name = TrayLayout.ShortName(t.File);
            string before = ByteSize.Format(t.File.SizeBytes, provider);
            if (t.AfterBytes is not { } after)
            {
                return $"{name} {before}, not measured yet";
            }

            // "129 → ~16 GiB" when both sizes share a unit.
            string afterText = ByteSize.Format(after, provider);
            string beforeUnit = before[(before.LastIndexOf(' ') + 1)..];
            string afterUnit = afterText[(afterText.LastIndexOf(' ') + 1)..];
            string from = beforeUnit == afterUnit ? before[..before.LastIndexOf(' ')] : before;
            return $"{name} {from} → ~{afterText}";
        }));
    }

    /// <summary>What shutting down WSL closes right now.</summary>
    private static IReadOnlyList<string> Warning(CleanupState state)
    {
        if (!state.RunningKnown)
        {
            return ["DiskTray couldn't tell what's running in WSL. Shutting it down closes everything running there, Docker Desktop included, and unsaved work in it is lost."];
        }

        var lines = new List<string>();
        foreach (string distro in state.RunningDistros
            .Where(d => !WslDistro.IsDockerOwnedName(d))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{distro} – open terminals, VS Code and Claude Code sessions");
        }

        if (state.DesktopMayRun)
        {
            lines.Add($"Docker Desktop – {Count(state.RunningContainers, "container")} running");
        }

        if (lines.Count == 0)
        {
            return ["Nothing is running in WSL right now, so nothing gets closed."];
        }

        return ["This shuts down WSL and closes:", .. lines, "Unsaved work in them is lost."];
    }

    private static string GainsLabel(DiskSnapshot snapshot, IReadOnlyList<CompactTarget> targets) =>
        targets.All(t => string.Equals(t.File.DriveRoot, snapshot.SystemDrive.Root, StringComparison.OrdinalIgnoreCase))
            ? snapshot.SystemDrive.Label
            : "Your drives";

    private static string Names(IEnumerable<CompactTarget> targets) =>
        string.Join(" and ", targets.Select(t => TrayLayout.ShortName(t.File)));

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
