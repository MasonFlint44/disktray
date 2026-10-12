using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.App;

/// <summary>
/// Checks that an exe about to run with admin rights can't be changed without them: that this
/// process can't write, replace or re-permission it, add files next to it (a DLL there would
/// load into it), or move its folder. The installer's <c>Program Files</c> folder passes; a
/// copy in <c>%LOCALAPPDATA%</c> doesn't, since anything running as the user could swap it
/// before the admin prompt is answered.
/// </summary>
internal static partial class ProtectedFolder
{
    private const uint FILE_WRITE_DATA = 0x2;
    private const uint FILE_APPEND_DATA = 0x4;
    private const uint FILE_ADD_FILE = 0x2;
    private const uint FILE_ADD_SUBDIRECTORY = 0x4;
    private const uint FILE_DELETE_CHILD = 0x40;
    private const uint DELETE = 0x10000;
    private const uint WRITE_DAC = 0x40000;
    private const uint WRITE_OWNER = 0x80000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 32;

    private static readonly uint[] FileRights = [FILE_WRITE_DATA, FILE_APPEND_DATA, DELETE, WRITE_DAC, WRITE_OWNER];
    private static readonly uint[] FolderRights = [FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY, FILE_DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER];

    /// <summary>Above the folder, only what would let it be renamed or re-permissioned.</summary>
    private static readonly uint[] AncestorRights = [FILE_DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER];

    /// <summary>
    /// Null when <paramref name="exe"/>, everything in its folder and the folders above it are
    /// out of this process's reach. Otherwise why not, to follow "because".
    /// </summary>
    public static string? Problem(string exe)
    {
        switch (Elevation())
        {
            case TokenElevationType.Full:
                // DiskTray itself running as admin would start the Compactor with no prompt,
                // and the checks below can't tell what a normal process could change.
                return "DiskTray is running as admin. Start it normally, and it asks for admin when it compacts";
            case TokenElevationType.Default when IsAdmin():
                // UAC is off, so everything runs as admin: there's no line for a swapped exe to cross.
                return null;
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var checks = new List<(string Path, uint[] Rights, bool IsFolder)> { (exe, FileRights, false), (folder, FolderRights, true) };
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
            {
                bool isFolder = Directory.Exists(entry);
                checks.Add((entry, isFolder ? FolderRights : FileRights, isFolder));
            }

            for (var parent = Directory.GetParent(folder); parent is not null; parent = parent.Parent)
            {
                checks.Add((parent.FullName, AncestorRights, true));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't list {folder}", e);
            return $"DiskTray couldn't check that {folder} is safe to run from as admin";
        }

        foreach (var (path, rights, isFolder) in checks)
        {
            switch (Check(path, rights, isFolder))
            {
                case Access.Granted:
                    return $"the Compactor is in a folder that can be changed without admin permission ({folder}). DiskTray's installer puts it where it can't be";
                case Access.Unknown:
                    return $"DiskTray couldn't check that {folder} is safe to run from as admin";
            }
        }

        return null;
    }

    private enum Access
    {
        Denied,
        Granted,
        Unknown,
    }

    /// <summary>
    /// Tries opening <paramref name="path"/> with each right in turn, closing it again without
    /// changing anything. Granted if any of them opens.
    /// </summary>
    private static Access Check(string path, uint[] rights, bool isFolder)
    {
        foreach (uint right in rights)
        {
            using var handle = CreateFile(path, right, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, isFolder ? FILE_FLAG_BACKUP_SEMANTICS : 0, 0);
            int error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
            switch (error)
            {
                case ERROR_ACCESS_DENIED:
                    continue;
                case 0:
                case ERROR_SHARING_VIOLATION:
                    // A sharing violation comes after the permission check passed (a running
                    // exe can't be opened for writing, but it can still be replaced).
                    Log.Info($"{path} can be opened with 0x{right:x} without admin rights");
                    return Access.Granted;
                default:
                    Log.Info($"Couldn't check {path}: error {error}");
                    return Access.Unknown;
            }
        }

        return Access.Denied;
    }

    private enum TokenElevationType
    {
        Default = 1,
        Full = 2,
        Limited = 3,
    }

    private const int TokenElevationTypeClass = 18;

    /// <summary>Full for an elevated admin, Limited for an admin's normal token, Default with UAC off or for a standard user.</summary>
    private static TokenElevationType Elevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return GetTokenInformation(identity.AccessToken, TokenElevationTypeClass, out int type, sizeof(int), out _)
            ? (TokenElevationType)type
            : TokenElevationType.Default;
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);
}
