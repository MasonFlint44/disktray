using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Compactor;

/// <summary>
/// Pins a disk file's path while it's compacted. The file and the folders above it can
/// usually be changed by the user, so something running as them could otherwise swap one
/// for another, or for a link, between the checks and the compact, and point this admin
/// process at a different disk. The file and each folder are held open without delete
/// sharing, so none can be renamed, removed or replaced until this is disposed; none may be
/// a link, and the file may have no other names (hard links).
/// </summary>
/// <remarks>
/// The held file's handle is what the account check opens again, rather than the path, so
/// the check is of the file that gets compacted, however the path would look to the user.
/// </remarks>
internal sealed partial class PathHold : IDisposable
{
    private const uint FileReadAttributes = 0x80;

    /// <summary>
    /// FILE_LIST_DIRECTORY on a folder, FILE_READ_DATA on a file. Windows only holds a handle
    /// to its sharing rules when it was opened for some data access, so attributes alone
    /// wouldn't stop a rename.
    /// </summary>
    private const uint DataAccess = 0x1;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint ReparsePointAttribute = 0x400;
    private const uint VolumeNameDos = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    private readonly List<SafeFileHandle> _handles;

    private PathHold(List<SafeFileHandle> handles) => _handles = handles;

    /// <summary>The disk file, held open; it's always the last handle taken.</summary>
    public SafeFileHandle File => _handles[^1];

    /// <summary>Holds the path for compacting, or returns null with why it can't be.</summary>
    public static PathHold? Take(string path, out string? problem) => Take(path, DataAccess, out problem);

    /// <summary>
    /// Makes the same checks without holding the file's data: opened only to read its
    /// attributes, which works while WSL still has the disk open. For the checks before
    /// anything is shut down, and for dry runs.
    /// </summary>
    public static PathHold? Look(string path, out string? problem) => Take(path, 0, out problem);

    private static PathHold? Take(string path, uint fileAccess, out string? problem)
    {
        var folders = new List<string>();
        for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            folders.Add(folder);
        }

        folders.Reverse();
        var held = new List<SafeFileHandle>();
        var hold = new PathHold(held);
        foreach (string folder in folders)
        {
            var handle = Open(folder, DataAccess, ShareRead | ShareWrite, BackupSemantics);
            if (handle.IsInvalid)
            {
                problem = $"{folder} {OpenProblem(Marshal.GetLastPInvokeError())}";
                hold.Dispose();
                return null;
            }

            held.Add(handle);
            if (IsLink(handle))
            {
                hold.Dispose();
                problem = $"{folder} is a link, so the disk's path can't be trusted";
                return null;
            }
        }

        // Read and write sharing, so Windows' virtual disk service can still open it to compact.
        var file = Open(path, fileAccess, ShareRead | ShareWrite, 0);
        if (file.IsInvalid)
        {
            problem = $"it {OpenProblem(Marshal.GetLastPInvokeError())}";
            hold.Dispose();
            return null;
        }

        held.Add(file);
        if (!GetFileInformationByHandle(file, out var info) || (info.Attributes & ReparsePointAttribute) != 0
            || !string.Equals(FinalPath(file), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
        {
            hold.Dispose();
            problem = "it's a link, or isn't where its path says";
            return null;
        }

        if (info.NumberOfLinks != 1)
        {
            hold.Dispose();
            problem = "it has more than one name (a hard link), so its path can't be trusted";
            return null;
        }

        problem = null;
        return hold;
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }

    private static string OpenProblem(int error) =>
        error is ErrorFileNotFound or ErrorPathNotFound ? "wasn't found" : $"couldn't be opened (error {error})";

    /// <summary>Opens the link itself rather than what it points to, so a link is seen as one.</summary>
    private static SafeFileHandle Open(string path, uint access, uint share, uint flags) =>
        CreateFile(path, FileReadAttributes | access, share, 0, OpenExisting, flags | OpenReparsePoint, 0);

    private static bool IsLink(SafeFileHandle handle) =>
        !GetFileInformationByHandle(handle, out var info) || (info.Attributes & ReparsePointAttribute) != 0;

    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        char* buffer = stackalloc char[1024];
        uint length = GetFinalPathNameByHandle(handle, buffer, 1024, VolumeNameDos);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : null;
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION. Its times are FILETIMEs, two DWORDs, so 4-byte aligned.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint length, uint flags);
}
