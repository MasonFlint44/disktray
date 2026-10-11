namespace DiskTray.Core.Detection;

/// <summary>The file system reads detection needs, so tests can fake them.</summary>
public interface IDiskProbe
{
    /// <summary>The file's size, or null if it doesn't exist or can't be read.</summary>
    long? GetFileSize(string path);

    /// <summary>The drive's size and free space, or null if it isn't available.</summary>
    DriveSpace? GetDriveSpace(string root);

    /// <summary>The file's text, or null if it doesn't exist or can't be read.</summary>
    string? ReadAllText(string path);
}

public sealed class SystemDiskProbe : IDiskProbe
{
    public long? GetFileSize(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public DriveSpace? GetDriveSpace(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? new DriveSpace(root, drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public string? ReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
