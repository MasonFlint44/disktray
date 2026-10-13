namespace DiskTray.Core;

/// <summary>One measurement of the disk files and the drives they live on.</summary>
public sealed record DiskSnapshot(
    DateTimeOffset TakenAt,
    DriveSpace SystemDrive,
    IReadOnlyList<DriveSpace> Drives,
    IReadOnlyList<DiskFile> Files,
    string? PrimaryDistroId)
{
    /// <summary>Kinds of disk file that weren't looked for, because Settings › Detection turned them off.</summary>
    public IReadOnlySet<DiskKind> NotDetected { get; init; } = new HashSet<DiskKind>();

    /// <summary>
    /// The disks of Docker Desktop's own distros (docker-desktop's system disk), other than
    /// its data disk. Not shown or compacted, but open while Docker runs, so Clean up's wait
    /// for WSL to go idle watches them too.
    /// </summary>
    public IReadOnlyList<string> DockerSystemDisks { get; init; } = [];

    public DiskFile? Docker => Files.FirstOrDefault(f => f.Kind == DiskKind.DockerData);

    /// <summary>The distro shown by default: the WSL default distro, or else the largest one.</summary>
    public DiskFile? PrimaryDistro => Files.FirstOrDefault(f => f.Id == PrimaryDistroId);

    public DriveSpace? DriveOf(DiskFile file) =>
        Drives.FirstOrDefault(d => string.Equals(d.Root, file.DriveRoot, StringComparison.OrdinalIgnoreCase));
}
