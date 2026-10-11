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

    public CleanupState Scan(DiskSnapshot snapshot)
    {
        var running = wsl.RunningDistros();
        if (!DesktopRunning() || docker.Run(DockerOutput.DiskUsageCommand, DiskUsageTimeout) is not { } dfOutput
            || DockerOutput.ParseDiskUsage(dfOutput) is not { } usage)
        {
            return new CleanupState(snapshot, null, [], running, 0);
        }

        var containers = Containers();
        var fromContainers = VolumeConfidence.FoldersFromContainers(containers, snapshot.PrimaryDistro?.Name);
        var cache = ProjectFolderCacheFile.Load();
        if (cache.Record(fromContainers, DateTimeOffset.Now))
        {
            ProjectFolderCacheFile.Save(cache);
        }

        var groups = VolumeConfidence.Group(usage.UnusedVolumes, fromContainers, cache, [], new FolderLookup(running));
        return new CleanupState(snapshot, usage, groups, running, containers.Count(c => c.Running));
    }

    private bool DesktopRunning() =>
        docker.Run(DockerOutput.DesktopStatusCommand, QuickTimeout) is { } output && DockerOutput.ParseDesktopRunning(output);

    private IReadOnlyList<DockerContainer> Containers()
    {
        if (docker.Run(DockerOutput.ContainerIdsCommand, QuickTimeout) is not { } idOutput
            || DockerOutput.ParseIds(idOutput) is not { Count: > 0 } ids)
        {
            return [];
        }

        return docker.Run(DockerOutput.InspectCommand(ids), QuickTimeout) is { } inspect ? DockerOutput.ParseContainers(inspect) : [];
    }
}
