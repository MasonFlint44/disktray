using DiskTray.Core.Detection;

namespace DiskTray.Core;

public enum DiskKind
{
    DockerData,
    WslDistro,
}

/// <summary>
/// A virtual disk file on a Windows drive. <see cref="UsedBytes"/> is how much of it is
/// in use inside WSL/Docker, when known; the rest of <see cref="SizeBytes"/> is reclaimable.
/// </summary>
public sealed record DiskFile(string Id, string Name, string Path, DiskKind Kind, long SizeBytes)
{
    public long? UsedBytes { get; init; }

    public long? ReclaimableBytes => UsedBytes is { } used ? Math.Max(0, SizeBytes - used) : null;

    /// <summary>The drive the file lives on, e.g. <c>C:\</c>, or null for UNC paths.</summary>
    public string? DriveRoot => WindowsPath.GetDriveRoot(Path);
}
