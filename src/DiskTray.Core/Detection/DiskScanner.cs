namespace DiskTray.Core.Detection;

/// <param name="SystemDriveRoot">Usually <c>C:\</c>.</param>
/// <param name="LocalAppData"><c>%LOCALAPPDATA%</c></param>
/// <param name="AppData"><c>%APPDATA%</c> (roaming)</param>
public sealed record ScanPaths(string SystemDriveRoot, string LocalAppData, string AppData);

/// <summary>
/// Finds the Docker and WSL disk files and measures them and their drives. Only reads file
/// sizes, so it never starts WSL or Docker. <see cref="Usage.UsageMeter"/> adds how much of
/// each disk is in use.
/// </summary>
public sealed class DiskScanner(IDiskProbe probe, IWslRegistry registry, ScanPaths paths, TimeProvider clock)
{
    public DiskSnapshot Scan()
    {
        var registryData = registry.Read();
        var distros = WslDistros.FromRegistry(registryData);
        var files = new List<DiskFile>();

        if (FindDockerData(distros) is { } docker)
        {
            files.Add(docker);
        }

        foreach (var distro in distros.Where(d => !d.IsDockerOwned))
        {
            if (probe.GetFileSize(distro.VhdxPath) is { } size)
            {
                files.Add(new DiskFile(distro.Id, distro.Name, distro.VhdxPath, DiskKind.WslDistro, size)
                {
                    UsageMount = new WslMount(distro.Name, "/"),
                });
            }
        }

        var systemDrive = probe.GetDriveSpace(paths.SystemDriveRoot)
            ?? throw new InvalidOperationException($"Couldn't read the size of drive {paths.SystemDriveRoot}.");
        var drives = new List<DriveSpace> { systemDrive };
        foreach (string root in files.Select(f => f.DriveRoot).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!drives.Any(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase))
                && probe.GetDriveSpace(root) is { } drive)
            {
                drives.Add(drive);
            }
        }

        return new DiskSnapshot(clock.GetUtcNow(), systemDrive, drives, files, PickPrimaryDistro(files, registryData.DefaultDistributionId));
    }

    private DiskFile? FindDockerData(IReadOnlyList<WslDistro> distros)
    {
        string path = DockerDesktop.DataDiskPath(probe, paths.LocalAppData, paths.AppData);
        if (probe.GetFileSize(path) is { } size)
        {
            return new DiskFile("docker", "Docker data", path, DiskKind.DockerData, size)
            {
                UsageMount = new WslMount(DockerDesktop.MainDistroName, DockerDesktop.DataDiskMountPoint),
            };
        }

        var legacy = distros.FirstOrDefault(d => string.Equals(d.Name, DockerDesktop.LegacyDataDistroName, StringComparison.OrdinalIgnoreCase));
        if (legacy is not null && probe.GetFileSize(legacy.VhdxPath) is { } legacySize)
        {
            return new DiskFile("docker", "Docker data", legacy.VhdxPath, DiskKind.DockerData, legacySize)
            {
                UsageMount = new WslMount(legacy.Name, "/"),
            };
        }

        return null;
    }

    private static string? PickPrimaryDistro(IReadOnlyList<DiskFile> files, string? defaultId)
    {
        var distros = files.Where(f => f.Kind == DiskKind.WslDistro).ToList();
        var preferred = distros.FirstOrDefault(f => string.Equals(f.Id, defaultId, StringComparison.OrdinalIgnoreCase))
            ?? distros.MaxBy(f => f.SizeBytes);
        return preferred?.Id;
    }
}
