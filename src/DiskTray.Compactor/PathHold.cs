using System.Runtime.InteropServices;
using DiskTray.Core.Compact;
using DiskTray.Shared;
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
/// The held file's handle is what the account check opens again, and what sizes are read
/// from, rather than the path, so each is of the file that gets compacted, however the path
/// would look to the user.
/// </remarks>
internal sealed class PathHold : IDisposable
{
    /// <summary>
    /// FILE_LIST_DIRECTORY on a folder, FILE_READ_DATA on a file. Windows only holds a handle
    /// to its sharing rules when it was opened for some data access, so attributes alone
    /// wouldn't stop a rename.
    /// </summary>
    private const uint DataAccess = 0x1;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;

    private readonly List<SafeFileHandle> _handles;
    private readonly string _path;

    private PathHold(List<SafeFileHandle> handles, string path)
    {
        _handles = handles;
        _path = path;
    }

    /// <summary>The disk file, held open; it's always the last handle taken.</summary>
    public SafeFileHandle File => _handles[^1];

    /// <summary>The file's size now, read through the held handle, or null if it can't be.</summary>
    public long? Size => LinkSafePath.Read(File, out var info) ? info.Size : null;

    /// <summary>
    /// Holds the path for compacting, or returns null with why it can't be. InUse when
    /// something else (WSL) has the file open, so it can't be held yet.
    /// </summary>
    public static PathHold? Take(string path, out string? problem, out bool inUse) => Take(path, DataAccess, out problem, out inUse);

    /// <summary>
    /// Makes the same checks without holding the file's data: opened only to read its
    /// attributes, which works while WSL still has the disk open. For the checks before
    /// anything is shut down, and for dry runs.
    /// </summary>
    public static PathHold? Look(string path, out string? problem) => Take(path, 0, out problem, out _);

    /// <summary>
    /// Why the held file isn't the one that was checked any more, or null when it still is:
    /// it has become a link, or gained another name. For after Windows' virtual disk service
    /// has opened it by its path.
    /// </summary>
    public string? Changed() =>
        LinkSafePath.Read(File, out var info) && !info.IsLink && info.NumberOfLinks == 1 && LinkSafePath.IsAt(File, _path)
            ? null
            : "it changed while it was being opened to compact";

    private static PathHold? Take(string path, uint fileAccess, out string? problem, out bool inUse)
    {
        inUse = false;
        var folders = new List<string>();
        for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            folders.Add(folder);
        }

        folders.Reverse();
        var held = new List<SafeFileHandle>();
        var hold = new PathHold(held, path);
        foreach (string folder in folders)
        {
            var handle = LinkSafePath.Open(folder, DataAccess, ShareRead | ShareWrite, folder: true);
            if (handle.IsInvalid)
            {
                problem = $"{folder} {OpenProblem(Marshal.GetLastPInvokeError())}";
                hold.Dispose();
                return null;
            }

            held.Add(handle);
            if (LinkSafePath.IsLink(handle))
            {
                hold.Dispose();
                problem = $"{folder} is a link, so the disk's path can't be trusted";
                return null;
            }
        }

        // Read and write sharing, so Windows' virtual disk service can still open it to compact.
        var file = LinkSafePath.Open(path, fileAccess, ShareRead | ShareWrite, folder: false);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            inUse = WindowsError.IsSharing(error);
            problem = $"it {OpenProblem(error)}";
            hold.Dispose();
            return null;
        }

        held.Add(file);
        if (!LinkSafePath.Read(file, out var info) || info.IsLink || !LinkSafePath.IsAt(file, path))
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
        error is WindowsError.FileNotFound or WindowsError.PathNotFound ? "wasn't found" : $"couldn't be opened (error {error})";
}
