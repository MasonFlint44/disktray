using System.IO;
using System.Runtime.InteropServices;
using DiskTray.Core;
using DiskTray.Core.Detection;

namespace DiskTray.App;

/// <summary>
/// Reads a sparse or compressed file's size as the space it takes on the drive, not its
/// length. WSL's sparse disk setting makes a distro's disk a sparse file, whose length can
/// be far more than it holds.
/// </summary>
internal sealed partial class AllocatedSizeProbe(IDiskProbe inner) : IDiskProbe
{
    private const uint InvalidFileSize = 0xFFFF_FFFF;

    public long? GetFileSize(string path)
    {
        long? length = inner.GetFileSize(path);
        if (length is null)
        {
            return null;
        }

        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.SparseFile | FileAttributes.Compressed)) == 0)
            {
                return length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return length;
        }

        uint low = GetCompressedFileSize(path, out uint high);
        return low == InvalidFileSize && Marshal.GetLastPInvokeError() != 0 ? length : ((long)high << 32) | low;
    }

    public DriveSpace? GetDriveSpace(string root) => inner.GetDriveSpace(root);

    public string? ReadAllText(string path) => inner.ReadAllText(path);

    public bool FolderExists(string path) => inner.FolderExists(path);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSize(string path, out uint high);
}
