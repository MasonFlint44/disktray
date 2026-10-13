using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskTray.Core.Compact;
using DiskTray.Shared;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.App;

/// <summary>
/// Checks that an exe about to run with admin rights can't be changed without them: that no
/// account but Windows' own and admins can write, replace or re-permission it, add files
/// next to it (a DLL there would load into it), or move its folder, and that its path holds
/// no link that could be pointed elsewhere. The installer's <c>Program Files</c> folder
/// passes; a copy in <c>%LOCALAPPDATA%</c> doesn't, since anything running as the user could
/// swap it before the admin prompt is answered.
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
    private const uint GENERIC_ALL = 0x1000_0000;
    private const uint GENERIC_WRITE = 0x4000_0000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private static readonly uint[] FileRights = [FILE_WRITE_DATA, FILE_APPEND_DATA, DELETE, WRITE_DAC, WRITE_OWNER];
    private static readonly uint[] FolderRights = [FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY, FILE_DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER];

    /// <summary>Above the folder, only what would let it be renamed or re-permissioned.</summary>
    private static readonly uint[] AncestorRights = [FILE_DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER];

    /// <summary>NT SERVICE\TrustedInstaller, which owns much of Windows (C:\ and Program Files among it).</summary>
    internal const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Who may own, or be let change, what an admin process runs: SYSTEM, Administrators and TrustedInstaller.</summary>
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(TrustedInstaller),
    ];

    /// <summary>
    /// OWNER RIGHTS: where the permissions name it, the owner gets only what it's given,
    /// rather than always being able to re-permission what it owns.
    /// </summary>
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    /// <summary>CREATOR OWNER: only a pattern for what's created inside, so it grants nothing where it's set.</summary>
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    /// <summary>
    /// Null when <paramref name="exe"/>, everything in its folder and the folders above it are
    /// out of reach of anyone without admin rights. Otherwise why not, to follow "because".
    /// </summary>
    public static string? Problem(string exe) => Problem(exe, IsElevated, UacOn);

    /// <summary>The same, with how this process runs given, so tests can try each way.</summary>
    internal static string? Problem(string exe, Func<bool?> isElevated, Func<bool> uacOn)
    {
        switch (isElevated())
        {
            case null:
                return "DiskTray couldn't check whether it's running as admin";
            case true:
                // DiskTray itself running as admin (started that way, or by the built-in
                // Administrator account, which UAC doesn't split) would start the Compactor with
                // no prompt, and the checks below can't tell what a normal process could change.
                // With UAC off everything runs as admin: there's no line for a swapped exe to cross.
                return uacOn()
                    ? "DiskTray is running as admin. Start it normally, from an account other than the built-in Administrator, and it asks for admin when it compacts"
                    : null;
        }

        return FolderProblem(exe);
    }

    /// <summary>The checks on the files and folders, whoever is running: null when none of them can be changed.</summary>
    internal static string? FolderProblem(string exe)
    {
        string full = Path.GetFullPath(exe);
        string folder = Path.GetDirectoryName(full)!;

        // First, since every check after this reads what a path leads to: none may lead
        // anywhere but where it says, or it could be pointed elsewhere once checked.
        if (LinkProblem(full) is { } link)
        {
            return link;
        }

        var checks = new List<(string Path, uint[] Rights, bool IsFolder)> { (full, FileRights, false), (folder, FolderRights, true) };
        try
        {
            var everything = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", everything))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Log.Info($"{entry.FullName} is a link");
                    return $"there's a link in the Compactor's folder ({entry.FullName}), so DiskTray can't tell what it would run";
                }

                bool isFolder = entry is DirectoryInfo;
                checks.Add((entry.FullName, isFolder ? FolderRights : FileRights, isFolder));
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
            // What this account can do, which counts its groups and any deny rules; then what
            // the owner and permissions let anyone else do.
            var access = Check(path, rights, isFolder);
            if (access == Access.Denied)
            {
                access = GrantedToOthers(path, rights, isFolder);
            }

            switch (access)
            {
                case Access.Granted:
                    return $"the Compactor is in a folder that can be changed without admin permission ({folder}). DiskTray's installer puts it where it can't be";
                case Access.Unknown:
                    return $"DiskTray couldn't check that {folder} is safe to run from as admin";
            }
        }

        return null;
    }

    /// <summary>
    /// Why the exe's path can't be trusted to lead where it says, or null: it's a link, or
    /// its real path differs, which a link (a junction, say) on any folder above it, a short
    /// name, a drive letter of the user's own (<c>subst</c>) or a network share would make.
    /// </summary>
    private static string? LinkProblem(string exe)
    {
        using var handle = LinkSafePath.Open(exe, 0, (uint)(FileShare.ReadWrite | FileShare.Delete), folder: false);
        if (handle.IsInvalid)
        {
            Log.Info($"Couldn't open {exe} to check its path: error {Marshal.GetLastPInvokeError()}");
            return $"DiskTray couldn't check that {Path.GetDirectoryName(exe)} is safe to run from as admin";
        }

        if (LinkSafePath.IsLink(handle) || !LinkSafePath.IsAt(handle, exe))
        {
            Log.Info($"{exe} is really {LinkSafePath.FinalPath(handle) ?? "somewhere it can't tell"}");
            return $"the Compactor's path ({exe}) goes through a link or a drive letter of its own, so DiskTray can't tell what it would run. Start DiskTray from where it's installed";
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
                case WindowsError.AccessDenied:
                    continue;
                case 0:
                case WindowsError.SharingViolation:
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

    /// <summary>
    /// Whether an account other than <see cref="Trusted"/> is allowed any of
    /// <paramref name="rights"/> on <paramref name="path"/> by its permissions as written, or
    /// owns it while they leave the owner free to re-permission it. Catches another everyday
    /// account having write access, which opening it as this one can't.
    /// </summary>
    private static Access GrantedToOthers(string path, uint[] rights, bool isFolder)
    {
        try
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity security = isFolder ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            bool ownerTrusted = owner is not null && Trusted.Contains(owner);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(r => !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                .ToList();
            if (!ownerTrusted && !rules.Any(r => OwnerRights.Equals(r.IdentityReference)))
            {
                Log.Info($"{path} is owned by {owner?.Value ?? "no one it can read"}, who can re-permission it");
                return Access.Granted;
            }

            uint wanted = rights.Aggregate(GENERIC_ALL | GENERIC_WRITE, (all, right) => all | right);
            foreach (var rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow
                    && ((uint)rule.FileSystemRights & wanted) != 0
                    && rule.IdentityReference is SecurityIdentifier sid
                    && !Trusted.Contains(sid)
                    && sid != CreatorOwner
                    && !(sid == OwnerRights && ownerTrusted))
                {
                    Log.Info($"{path} lets {sid.Value} change it (0x{(uint)rule.FileSystemRights:x})");
                    return Access.Granted;
                }
            }

            return Access.Denied;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info($"Couldn't read who can change {path}: {e.Message}");
            return Access.Unknown;
        }
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
