using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Shared;

/// <summary>
/// Opening paths the way the checks before anything runs as admin need: a link is opened as
/// itself, never followed, and a handle's real path can be read back and compared. Linked
/// into the app (for the Compactor's folder) and the Compactor (for disks), so both judge
/// links, drive letters and long paths the same way.
/// </summary>
internal static partial class LinkSafePath
{
    public const uint ReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint ReparsePointAttribute = 0x400;
    private const uint VolumeNameDos = 0;

    /// <summary>Folders past this many characters need the prefix that lifts Windows' 260-character limit.</summary>
    private const int LongPath = 248;

    /// <summary>
    /// The path as Windows' file calls take it: a long one gets the <c>\\?\</c> prefix, which
    /// lifts the 260-character limit. Only for full, already-checked paths, since the prefix
    /// also turns off Windows' tidying of them.
    /// </summary>
    public static string ForWindows(string path) =>
        path.Length >= LongPath && !path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + path : path;

    /// <summary>Opens <paramref name="path"/> itself: a link, rather than what it points to.</summary>
    public static SafeFileHandle Open(string path, uint access, uint share, bool folder) =>
        CreateFile(ForWindows(path), ReadAttributes | access, share, 0, OpenExisting, (folder ? BackupSemantics : 0) | OpenReparsePoint, 0);

    /// <summary>Reads the file's attributes, size and number of names through its handle.</summary>
    public static bool Read(SafeFileHandle handle, out FileInformation info) => GetFileInformationByHandle(handle, out info);

    /// <summary>Whether the handle is on a link (any reparse point). One that can't be read counts as a link.</summary>
    public static bool IsLink(SafeFileHandle handle) =>
        !GetFileInformationByHandle(handle, out var info) || (info.Attributes & ReparsePointAttribute) != 0;

    /// <summary>
    /// Whether the handle's real path is exactly <paramref name="path"/>: no link, short
    /// name, or drive letter that leads somewhere else (one made with <c>subst</c>, say) on
    /// the way. A path on a network share never is.
    /// </summary>
    public static bool IsAt(SafeFileHandle handle, string path) =>
        string.Equals(FinalPath(handle), @"\\?\" + path, StringComparison.OrdinalIgnoreCase);

    /// <summary>The handle's real path, as <c>\\?\C:\…</c>, or null when it can't be read.</summary>
    public static unsafe string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length = Final(handle, buffer);
        if (length >= buffer.Length)
        {
            // Too long for the buffer: length is then the size it needs.
            buffer = new char[length];
            length = Final(handle, buffer);
        }

        return length is > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : null;

        static uint Final(SafeFileHandle handle, char[] buffer)
        {
            fixed (char* chars = buffer)
            {
                return GetFinalPathNameByHandle(handle, chars, (uint)buffer.Length, VolumeNameDos);
            }
        }
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION. Its times are FILETIMEs, two DWORDs, so 4-byte aligned.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FileInformation
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

        public readonly bool IsLink => (Attributes & ReparsePointAttribute) != 0;

        public readonly long Size => ((long)FileSizeHigh << 32) | FileSizeLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint length, uint flags);
}
