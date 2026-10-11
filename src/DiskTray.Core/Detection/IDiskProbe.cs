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

    bool FolderExists(string path);
}

/// <summary>A folder's total size, added up at <paramref name="MeasuredAt"/>.</summary>
public sealed record FolderSize(long Bytes, DateTimeOffset MeasuredAt);

/// <summary>Folder sizes, added up in the background since that can take a while. Implemented in the app.</summary>
public interface IFolderSizes
{
    /// <summary>The folder's last measured size, or null before the first.</summary>
    FolderSize? Get(string path);
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

    public bool FolderExists(string path) => Directory.Exists(path);
}
