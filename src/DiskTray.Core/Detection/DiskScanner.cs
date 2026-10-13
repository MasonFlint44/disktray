using DiskTray.Core.Tray;

namespace DiskTray.Core.Detection;

/// <param name="SystemDriveRoot">Usually <c>C:\</c>.</param>
/// <param name="LocalAppData"><c>%LOCALAPPDATA%</c></param>
/// <param name="AppData"><c>%APPDATA%</c> (roaming)</param>
public sealed record ScanPaths(string SystemDriveRoot, string LocalAppData, string AppData);

/// <summary>Which disk files to look for, from Settings › Detection.</summary>
public sealed record DetectionOptions(bool Distros = true, bool Docker = true);

/// <summary>
/// Finds the Docker and WSL disk files and measures them and their drives, plus the files,
/// folders and drives pinned to the tray. Only reads file sizes, so it never starts WSL or
/// Docker. <see cref="Usage.UsageMeter"/> adds how much of each disk is in use.
/// </summary>
public sealed class DiskScanner(IDiskProbe probe, IWslRegistry registry, ScanPaths paths, TimeProvider clock, IFolderSizes? folders = null)
{
    public DiskSnapshot Scan(IReadOnlyList<TrayPin>? pins = null, DetectionOptions? detect = null)
    {
        pins ??= TrayPin.Defaults;
        detect ??= new DetectionOptions();
        // Docker's legacy data distro is in the WSL registry too, so read it either way.
        var registryData = detect.Distros || detect.Docker ? registry.Read() : WslRegistryData.Empty;
        var distros = WslDistros.FromRegistry(registryData);
        var files = new List<DiskFile>();

        if (detect.Docker && FindDockerData(distros) is { } docker)
        {
            files.Add(docker);
        }

        foreach (var distro in distros.Where(d => detect.Distros && !d.IsDockerOwned))
        {
            if (probe.GetFileSize(distro.VhdxPath) is { } size)
            {
                files.Add(new DiskFile(distro.Id, distro.Name, distro.VhdxPath, DiskKind.WslDistro, size)
                {
                    UsageMount = new WslMount(distro.Name, "/"),
                });
            }
        }

        foreach (var pin in pins.Where(p => p.Kind == PinKind.Path && p.Target is not null))
        {
            if (PinnedPath(pin.Target!) is { } pinned)
            {
                files.Add(pinned);
            }
        }

        var systemDrive = probe.GetDriveSpace(paths.SystemDriveRoot)
            ?? throw new InvalidOperationException($"Couldn't read the size of drive {paths.SystemDriveRoot}.");
        var drives = new List<DriveSpace> { systemDrive };
        var pinnedDrives = pins.Where(p => p.Kind == PinKind.Drive && p.Target is not null).Select(p => p.Target!);
        foreach (string root in files.Select(f => f.DriveRoot).OfType<string>().Concat(pinnedDrives).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!drives.Any(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase))
                && probe.GetDriveSpace(root) is { } drive)
            {
                drives.Add(drive);
            }
        }

        var notDetected = new HashSet<DiskKind>();
        if (!detect.Docker)
        {
            notDetected.Add(DiskKind.DockerData);
        }

        if (!detect.Distros)
        {
            notDetected.Add(DiskKind.WslDistro);
        }

        return new DiskSnapshot(clock.GetUtcNow(), systemDrive, drives, files, PickPrimaryDistro(files, registryData.DefaultDistributionId))
        {
            NotDetected = notDetected,
            DockerSystemDisks = distros
                .Where(d => d.IsDockerOwned && !files.Any(f => string.Equals(f.Path, d.VhdxPath, StringComparison.OrdinalIgnoreCase)))
                .Select(d => d.VhdxPath)
                .ToList(),
        };
    }

    /// <summary>The ID a pinned path's file has in a snapshot.</summary>
    public static string PathId(string path) => "path:" + path.TrimEnd('\\').ToUpperInvariant();

    private DiskFile? PinnedPath(string path)
    {
        string name = WindowsPath.GetName(path);
        if (probe.GetFileSize(path) is { } size)
        {
            return new DiskFile(PathId(path), name, path, DiskKind.File, size);
        }

        if (!probe.FolderExists(path))
        {
            return null;
        }

        // Until the first count finishes, the folder is there with no size yet.
        var measured = folders?.Get(path);
        return new DiskFile(PathId(path), name, path, DiskKind.Folder, measured?.Bytes ?? 0) { SizedAsOf = measured?.MeasuredAt };
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
