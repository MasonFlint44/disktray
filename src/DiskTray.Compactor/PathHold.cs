using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Compactor;

/// <summary>
/// Pins a disk file's path while it's compacted. The folders above it can usually be
/// changed by the user, so something running as them could otherwise swap one for a link,
/// between the admin prompt and the compact, and point this admin process at a different
/// disk. Each folder is held open without delete sharing, so it can't be renamed, removed
/// or replaced until this is disposed; and neither they nor the file may be links.
/// </summary>
internal sealed partial class PathHold : IDisposable
{
    private const uint FileReadAttributes = 0x80;

    /// <summary>
    /// Also asked for on folders: Windows only holds a handle to its sharing rules when it
    /// was opened for some data access, so attributes alone wouldn't stop a rename.
    /// </summary>
    private const uint FileListDirectory = 0x1;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint ShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint ReparsePointAttribute = 0x400;
    private const uint VolumeNameDos = 0;

    private readonly List<SafeFileHandle> _folders;

    private PathHold(List<SafeFileHandle> folders) => _folders = folders;

    /// <summary>Holds the path, or returns null with why it can't be.</summary>
    public static PathHold? Take(string path, out string? problem)
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
            var handle = Open(folder, FileListDirectory, ShareRead | ShareWrite, BackupSemantics);
            if (handle.IsInvalid)
            {
                hold.Dispose();
                problem = $"{folder} couldn't be opened (error {Marshal.GetLastPInvokeError()})";
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

        using (var file = Open(path, 0, ShareRead | ShareWrite | ShareDelete, 0))
        {
            if (file.IsInvalid)
            {
                hold.Dispose();
                problem = $"it couldn't be opened (error {Marshal.GetLastPInvokeError()})";
                return null;
            }

            if (IsLink(file) || !string.Equals(FinalPath(file), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            {
                hold.Dispose();
                problem = "it's a link, or isn't where its path says";
                return null;
            }
        }

        problem = null;
        return hold;
    }

    public void Dispose()
    {
        foreach (var handle in _folders)
        {
            handle.Dispose();
        }

        _folders.Clear();
    }

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

    [StructLayout(LayoutKind.Sequential)]
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
