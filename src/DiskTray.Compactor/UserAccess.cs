using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Compactor;

/// <summary>
/// What the account that asked for the compact can do on its own, checked with the token of
/// DiskTray on the other end of the pipe, which runs without admin rights. The disk paths
/// come from settings anything running as that account can change, so the Compactor only
/// compacts a disk that account could already write to: admin rights never reach further.
/// </summary>
internal sealed partial class UserAccess : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x2;
    private const uint TokenImpersonate = 0x4;
    private const uint TokenQuery = 0x8;
    private const int SecurityImpersonation = 2;
    private const int TokenTypeImpersonation = 2;
    private const uint FileWriteData = 0x2;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const uint SePrivilegeEnabled = 0x2;
    private const uint TokenAdjustPrivileges = 0x20;

    private readonly SafeAccessTokenHandle _token;

    private UserAccess(SafeAccessTokenHandle token) => _token = token;

    /// <summary>The account of the process that made the pipe, or null with why it can't be told.</summary>
    public static UserAccess? OfPipeServer(SafePipeHandle pipe, out string? problem)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint server))
        {
            problem = $"couldn't tell what's on the other end of the pipe (error {Marshal.GetLastPInvokeError()})";
            return null;
        }

        return OfProcess(server, out problem);
    }

    /// <summary>The account process <paramref name="id"/> runs as, or null with why it can't be told.</summary>
    public static UserAccess? OfProcess(uint id, out string? problem)
    {
        // Another account's DiskTray (a standard user who typed an admin's password) may only
        // be readable with the debug privilege, which admins have but don't use by default.
        var token = ImpersonationToken(id, out int error);
        if (token is null && error == ErrorAccessDenied && EnableDebugPrivilege())
        {
            token = ImpersonationToken(id, out error);
        }

        problem = token is null ? $"couldn't read which account asked (error {error})" : null;
        return token is null ? null : new UserAccess(token);
    }

    /// <summary>What <paramref name="token"/> (with TOKEN_DUPLICATE) can do, or null with why it can't be told.</summary>
    internal static UserAccess? OfToken(SafeAccessTokenHandle token, out string? problem)
    {
        var copy = Impersonation(token, out int error);
        problem = copy is null ? $"couldn't read which account asked (error {error})" : null;
        return copy is null ? null : new UserAccess(copy);
    }

    /// <summary>
    /// Whether that account could open the file to change it: null when that can't be told,
    /// which callers treat as no.
    /// </summary>
    public bool? CanWrite(string path)
    {
        if (!ImpersonateLoggedOnUser(_token))
        {
            return null;
        }

        int error;
        try
        {
            using var handle = CreateFile(path, FileWriteData, ShareAll, 0, OpenExisting, 0, 0);
            error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        }
        finally
        {
            // Carrying on as the user, or as anyone but this admin process, is never safe.
            if (!RevertToSelf())
            {
                Environment.FailFast("The Compactor couldn't stop acting as the user.");
            }
        }

        return error switch
        {
            // A sharing problem comes after the permission check passed.
            0 or ErrorSharingViolation or ErrorLockViolation => true,
            ErrorAccessDenied => false,
            _ => null,
        };
    }

    public void Dispose() => _token.Dispose();

    private static SafeAccessTokenHandle? ImpersonationToken(uint processId, out int error)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        if (!OpenProcessToken(process, TokenDuplicate | TokenQuery, out var primary))
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        using (primary)
        {
            return Impersonation(primary, out error);
        }
    }

    /// <summary>A copy of the token this process can act as, to check what it may open.</summary>
    private static SafeAccessTokenHandle? Impersonation(SafeAccessTokenHandle token, out int error)
    {
        if (!DuplicateTokenEx(token, TokenImpersonate | TokenQuery, 0, SecurityImpersonation, TokenTypeImpersonation, out var copy))
        {
            error = Marshal.GetLastPInvokeError();
            copy.Dispose();
            return null;
        }

        error = 0;
        return copy;
    }

    private static bool EnableDebugPrivilege()
    {
        if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out long luid))
        {
            return false;
        }

        if (!OpenProcessToken(CurrentProcess, TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            // Succeeds without enabling it when the token doesn't hold it, which this tells apart.
            return AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0) && Marshal.GetLastPInvokeError() == 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    /// <summary>What GetCurrentProcess returns: a stand-in for this process that needs no closing.</summary>
    private const nint CurrentProcess = -1;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle copy);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateLoggedOnUser(SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, nint previous, nint returned);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
