using System.Runtime.InteropServices;
using DiskTray.Core.Compact;
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
    private const uint SePrivilegeEnabled = 0x2;
    private const int KernelObject = 6;
    private const uint OwnerSecurityInformation = 0x1;
    private const int TokenUserClass = 1;
    private const uint TokenAdjustPrivileges = 0x20;

    private readonly SafeAccessTokenHandle _token;

    private UserAccess(SafeAccessTokenHandle token) => _token = token;

    /// <summary>The account of the process that made the pipe, or null with why it can't be told.</summary>
    /// <remarks>
    /// Windows gives the process's ID, which is reused once that process ends, so the account
    /// found must also be the pipe's owner (DiskTray sets itself as that): an ID that has
    /// passed to another process, a service say, can't stand in for the one that made it.
    /// </remarks>
    public static UserAccess? OfPipeServer(SafePipeHandle pipe, out string? problem)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint server))
        {
            problem = $"couldn't tell what's on the other end of the pipe (error {Marshal.GetLastPInvokeError()})";
            return null;
        }

        var user = OfProcess(server, out problem);
        switch (user?.Owns(pipe))
        {
            case null:
                problem ??= "couldn't tell which account made the pipe";
                break;
            case false:
                problem = "found the pipe wasn't made by the account on its other end";
                break;
            case true:
                return user;
        }

        user?.Dispose();
        return null;
    }

    /// <summary>The account process <paramref name="id"/> runs as, or null with why it can't be told.</summary>
    public static UserAccess? OfProcess(uint id, out string? problem)
    {
        // Another account's DiskTray (a standard user who typed an admin's password) may only
        // be readable with the debug privilege, which admins have but don't use by default.
        var token = ImpersonationToken(id, out int error);
        if (token is null && error == WindowsError.AccessDenied && SetDebugPrivilege(true))
        {
            token = ImpersonationToken(id, out error);
            // Only needed for that; off again, so nothing later runs with it.
            SetDebugPrivilege(false);
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
    /// Whether that account could open the held file to change it: null when that can't be
    /// told, which callers treat as no. The handle is opened again, not the path, since a
    /// path can lead somewhere else for the user (their own drive letters, say) than for this
    /// admin process.
    /// </summary>
    public bool? CanWrite(SafeFileHandle file)
    {
        if (!ImpersonateLoggedOnUser(_token))
        {
            return null;
        }

        int error;
        try
        {
            using var handle = ReOpenFile(file, FileWriteData, ShareAll, 0);
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
            // A sharing problem (WSL still has the disk open) comes after the permission check passed.
            0 or WindowsError.SharingViolation or WindowsError.LockViolation => true,
            WindowsError.AccessDenied => false,
            _ => null,
        };
    }

    public void Dispose() => _token.Dispose();

    /// <summary>Whether this account owns <paramref name="handle"/>'s object, or null when that can't be read.</summary>
    internal unsafe bool? Owns(SafeHandle handle)
    {
        if (GetSecurityInfo(handle, KernelObject, OwnerSecurityInformation, out nint owner, 0, 0, 0, out nint descriptor) != 0)
        {
            return null;
        }

        try
        {
            GetTokenInformation(_token, TokenUserClass, null, 0, out int size);
            var buffer = new byte[size];
            fixed (byte* user = buffer)
            {
                // TOKEN_USER starts with the account's SID pointer.
                return size > 0 && GetTokenInformation(_token, TokenUserClass, user, size, out _) ? EqualSid(*(nint*)user, owner) : null;
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

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

    /// <summary>Turns SeDebugPrivilege on or off. True when it was changed.</summary>
    private static bool SetDebugPrivilege(bool on)
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
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = on ? SePrivilegeEnabled : 0 };
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeHandle handle, int objectType, uint securityInfo, out nint owner, nint group, nint dacl, nint sacl, out nint descriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, byte* info, int length, out int returned);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EqualSid(nint one, nint other);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
