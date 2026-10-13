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
        switch (IsElevated())
        {
            case null:
                return "DiskTray couldn't check whether it's running as admin";
            case true:
                // DiskTray itself running as admin (started that way, or by the built-in
                // Administrator account, which UAC doesn't split) would start the Compactor with
                // no prompt, and the checks below can't tell what a normal process could change.
                // With UAC off everything runs as admin: there's no line for a swapped exe to cross.
                return UacOn()
                    ? "DiskTray is running as admin. Start it normally, from an account other than the built-in Administrator, and it asks for admin when it compacts"
                    : null;
        }

        return FolderProblem(exe);
    }

    /// <summary>The checks on the files and folders, whoever is running: null when none of them can be changed.</summary>
    internal static string? FolderProblem(string exe)
    {
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

    private const int TokenElevationClass = 20;

    /// <summary>
    /// Whether this process has admin rights in effect: started as administrator, run by the
    /// built-in Administrator account, or by any admin with UAC off. Null when it can't be told.
    /// </summary>
    private static bool? IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, TokenElevationClass, out int elevated, sizeof(int), out _))
        {
            Log.Info($"Couldn't tell whether DiskTray is running as admin: error {Marshal.GetLastPInvokeError()}");
            return null;
        }

        return elevated != 0;
    }

    /// <summary>Whether UAC is on (EnableLUA). Unknown counts as on, which only means not compacting.</summary>
    private static bool UacOn()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1) is not 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);
}
