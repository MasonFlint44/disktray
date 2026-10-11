using DiskTray.Core.Detection;

namespace DiskTray.Core;

public enum DiskKind
{
    DockerData,
    WslDistro,

    /// <summary>A file pinned by hand.</summary>
    File,

    /// <summary>A folder pinned by hand. Sized in the background, so it can be a while old.</summary>
    Folder,
}

/// <summary>A WSL distro and a mount point inside it, where <c>df</c> shows how full a disk is.</summary>
public sealed record WslMount(string Distro, string Path);

/// <summary>
/// A virtual disk file on a Windows drive, or a file or folder pinned by hand.
/// <see cref="UsedBytes"/> is how much of a virtual disk is in use inside WSL/Docker, when
/// known; the rest of <see cref="SizeBytes"/> is reclaimable.
/// </summary>
public sealed record DiskFile(string Id, string Name, string Path, DiskKind Kind, long SizeBytes)
{
    /// <summary>Where to measure how much of the disk is in use, or null if it can't be.</summary>
    public WslMount? UsageMount { get; init; }

    public long? UsedBytes { get; init; }

    /// <summary>
    /// When <see cref="UsedBytes"/> was measured, if it's a remembered value because the
    /// distro wasn't running. Null when it was measured just now.
    /// </summary>
    public DateTimeOffset? UsedAsOf { get; init; }

    /// <summary>When a folder's size was added up, since that happens in the background.</summary>
    public DateTimeOffset? SizedAsOf { get; init; }

    public long? ReclaimableBytes => UsedBytes is { } used ? Math.Max(0, SizeBytes - used) : null;

    /// <summary>The drive the file lives on, e.g. <c>C:\</c>, or null for UNC paths.</summary>
    public string? DriveRoot => WindowsPath.GetDriveRoot(Path);
}
