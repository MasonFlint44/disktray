using DiskTray.Core;
using DiskTray.Core.Cleanup;

namespace DiskTray.App;

/// <summary>
/// Gathers what the Clean up dialog shows. Only reads: Docker is asked only when Docker
/// Desktop says it's running, and WSL folders are looked at only in distros already running.
/// </summary>
internal sealed class CleanupScanner(DockerExe docker, WslExe wsl)
{
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(20);

    // Sizing every volume can take a while on a big Docker disk.
    private static readonly TimeSpan DiskUsageTimeout = TimeSpan.FromMinutes(2);

    /// <param name="codeFolders">Settings › Detection's "Code folders to search".</param>
    public CleanupState Scan(DiskSnapshot snapshot, IReadOnlyList<string> codeFolders)
    {
        var running = wsl.RunningDistros();
        var known = running ?? new HashSet<string>();
        bool? desktop = DockerOutput.DesktopRunning(docker.Installed, docker.Installed ? docker.Run(DockerOutput.DesktopStatusCommand, QuickTimeout) : null, running);
        if (desktop != true || docker.Run(DockerOutput.DiskUsageCommand, DiskUsageTimeout) is not { } dfOutput
            || DockerOutput.ParseDiskUsage(dfOutput) is not { } usage)
        {
            return new CleanupState(snapshot, null, [], known, 0) { DesktopRunning = desktop, RunningKnown = running is not null };
        }

        var containers = Containers();
        var fromContainers = VolumeConfidence.FoldersFromContainers(containers, snapshot.PrimaryDistro?.Name);
        var cache = ProjectFolderCacheFile.Load();
        if (cache.Record(fromContainers, DateTimeOffset.Now))
        {
            ProjectFolderCacheFile.Save(cache);
        }

        var searchIn = codeFolders.Select(ProjectFolder.FromWindowsPath).OfType<ProjectFolder>();
        var groups = VolumeConfidence.Group(usage.UnusedVolumes, fromContainers, cache, searchIn, new FolderLookup(known));
        return new CleanupState(snapshot, usage, groups, known, containers.Count(c => c.Running)) { DesktopRunning = true, RunningKnown = running is not null };
    }

    private IReadOnlyList<DockerContainer> Containers()
    {
        if (docker.Run(DockerOutput.ContainerIdsCommand, QuickTimeout) is not { } idOutput
            || DockerOutput.ParseIds(idOutput) is not { Count: > 0 } ids)
        {
            return [];
        }

        // A container whose batch fails is left out, as if it weren't there.
        return DockerOutput.InspectCommands(ids)
            .SelectMany(command => docker.Run(command, QuickTimeout) is { } inspect ? DockerOutput.ParseContainers(inspect) : [])
            .ToList();
    }
}
