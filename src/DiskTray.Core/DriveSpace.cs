namespace DiskTray.Core;

/// <summary>Size and free space of one drive, e.g. <c>C:\</c>.</summary>
public sealed record DriveSpace(string Root, long TotalBytes, long FreeBytes)
{
    public double FreeFraction => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 0;

    /// <summary>The drive as people write it, e.g. "C:".</summary>
    public string Label => Root.TrimEnd('\\');
}
