using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskTray.Compactor;
using DiskTray.Core.Compact;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Windows.Tests;

/// <summary>
/// A fresh folder under the temp folder, written out in full (the runner's temp path can
/// come with 8.3 short names, which the Compactor refuses), removed afterwards.
/// </summary>
internal sealed partial class TestFolder : IDisposable
{
    public TestFolder()
    {
        Path = System.IO.Path.Combine(LongPath(System.IO.Path.GetTempPath()), "DiskTray.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A file in the folder (creating its folders), with a little in it.</summary>
    public string File(string relative)
    {
        string path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "not really a disk");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind on a test machine.
        }
    }

    public static string LongPath(string path)
    {
        var buffer = new char[1024];
        uint length = GetLongPathName(path, buffer, (uint)buffer.Length);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathName(string shortPath, [Out] char[] longPath, uint length);
}

internal static class Admin
{
    /// <summary>Whether this test run has admin rights in effect.</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Skips a test that needs admin rights, as the Compactor has, when they're missing. With
    /// DISKTRAY_REQUIRE_ADMIN_TESTS set (the GitHub Actions build, which has them) it fails
    /// instead, so those tests can't quietly stop running.
    /// </summary>
    public static void SkipUnlessElevated()
    {
        if (Environment.GetEnvironmentVariable("DISKTRAY_REQUIRE_ADMIN_TESTS") is { Length: > 0 })
        {
            Assert.True(IsElevated, "DISKTRAY_REQUIRE_ADMIN_TESTS is set, but the tests aren't running with admin rights.");
        }

        Assert.SkipUnless(IsElevated, "Needs admin rights, as the Compactor has.");
    }
}

/// <summary>Makes real (empty) VHDX files with Windows' virtual disk API, for compacting.</summary>
internal static unsafe partial class TestDisk
{
    /// <summary>Creates an expandable VHDX at <paramref name="path"/>, 64 MiB unless said otherwise.</summary>
    public static void Create(string path, long bytes = 64L << 20)
    {
        var type = new VirtualDisk.StorageType { DeviceId = VirtualDisk.VhdxDevice, VendorId = VirtualDisk.MicrosoftVendor };
        var parameters = new CreateParameters { Version = 2, MaximumSize = (ulong)bytes };
        // By pointer: the structs are the Compactor's, which LibraryImport won't marshal from another assembly.
        uint error = CreateVirtualDisk(&type, path, 0, 0, 0, 0, &parameters, 0, out var handle);
        handle.Dispose();
        Assert.True(error == 0, $"CreateVirtualDisk failed with error {error}");
    }

    /// <summary>CREATE_VIRTUAL_DISK_PARAMETERS, version 2. The union after Version is 8-byte aligned.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateParameters
    {
        public int Version;
        private readonly int _unionPadding;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public uint PhysicalSectorSizeInBytes;
        public nint ParentPath;
        public nint SourcePath;
        public int OpenFlags;
        public VirtualDisk.StorageType ParentVirtualStorageType;
        public VirtualDisk.StorageType SourceVirtualStorageType;
        public Guid ResiliencyGuid;
    }

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CreateVirtualDisk(
        VirtualDisk.StorageType* type,
        string path,
        uint accessMask,
        nint securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        CreateParameters* parameters,
        nint overlapped,
        out SafeFileHandle handle);
}

/// <summary>Reads what the Compactor sends, as the app does, until it says something in particular.</summary>
internal static class Statuses
{
    public static CompactStatus WaitFor(DiskTray.App.CompactorLauncher launcher, Func<CompactStatus, bool> wanted, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        CompactStatus? last = null;
        while (DateTime.UtcNow < until)
        {
            last = launcher.ReadStatus();
            if (last is not null && wanted(last))
            {
                return last;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"Waited {timeout.TotalSeconds:0} s; the last status was {last?.ToJson() ?? "none"}.");
    }
}

/// <summary>
/// This process's token with Administrators turned into a deny-only group and at Medium
/// integrity, as Windows makes an admin's everyday (not "Run as administrator") token, which
/// is how DiskTray itself runs for most people.
/// </summary>
internal static unsafe partial class NonAdminToken
{
    private const uint TokenDuplicate = 0x2;
    private const uint TokenAssignPrimary = 0x1;
    private const uint TokenQuery = 0x8;
    private const uint TokenAdjustDefault = 0x80;
    private const uint DisableMaxPrivilege = 0x1;
    private const int TokenIntegrityLevel = 25;
    private const uint GroupIntegrity = 0x20;

    public static SafeAccessTokenHandle Create()
    {
        Assert.True(OpenProcessToken(-1, TokenDuplicate | TokenAssignPrimary | TokenQuery | TokenAdjustDefault, out var own), $"OpenProcessToken failed ({Marshal.GetLastPInvokeError()})");
        using (own)
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            byte[] sid = new byte[admins.BinaryLength];
            admins.GetBinaryForm(sid, 0);
            fixed (byte* sidPointer = sid)
            {
                var disable = new SidAndAttributes { Sid = (nint)sidPointer };
                Assert.True(
                    CreateRestrictedToken(own, DisableMaxPrivilege, 1, &disable, 0, 0, 0, 0, out var restricted),
                    $"CreateRestrictedToken failed ({Marshal.GetLastPInvokeError()})");
                MediumIntegrity(restricted);
                return restricted;
            }
        }
    }

    /// <summary>Lowers the token to Medium integrity, as an everyday token is; lowering needs no privilege.</summary>
    private static void MediumIntegrity(SafeAccessTokenHandle token)
    {
        var medium = new SecurityIdentifier("S-1-16-8192");
        byte[] sid = new byte[medium.BinaryLength];
        medium.GetBinaryForm(sid, 0);
        fixed (byte* sidPointer = sid)
        {
            var label = new SidAndAttributes { Sid = (nint)sidPointer, Attributes = GroupIntegrity };
            Assert.True(
                SetTokenInformation(token, TokenIntegrityLevel, &label, (uint)(sizeof(SidAndAttributes) + sid.Length)),
                $"SetTokenInformation failed ({Marshal.GetLastPInvokeError()})");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, void* info, uint length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(
        SafeAccessTokenHandle existing,
        uint flags,
        uint disableSidCount,
        SidAndAttributes* sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out SafeAccessTokenHandle restricted);
}

/// <summary>
/// A test VHDX attached to Windows (no drive letter, detached again on dispose), to write
/// to as a raw disk. Only ever the disk this attached: its device path comes from its own
/// handle, and its size is checked before anything is written.
/// </summary>
internal sealed unsafe partial class AttachedTestDisk : IDisposable
{
    private const uint AccessAttachDetachInfo = 0x000E_0000;
    private const uint AttachNoDriveLetter = 0x2;
    private const uint GenericReadWrite = 0xC000_0000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint DiskGetLengthInfo = 0x0007_405C;
    private const uint DiskSetAttributes = 0x0007_C0F4;

    private readonly SafeFileHandle _virtualDisk;
    private readonly SafeFileHandle _device;

    public AttachedTestDisk(string vhdx, long expectedBytes)
    {
        var type = new VirtualDisk.StorageType { DeviceId = VirtualDisk.VhdxDevice, VendorId = VirtualDisk.MicrosoftVendor };
        var open = new VirtualDisk.OpenParameters { Version = 1, RWDepth = 1 };
        uint error = OpenVirtualDisk(&type, vhdx, AccessAttachDetachInfo, 0, &open, out _virtualDisk);
        Assert.True(error == 0, $"OpenVirtualDisk failed ({error})");

        var attach = new AttachParameters { Version = 1 };
        error = AttachVirtualDisk(_virtualDisk, 0, AttachNoDriveLetter, 0, ref attach, 0);
        Assert.True(error == 0, $"AttachVirtualDisk failed ({error})");

        var path = new char[260];
        uint size = (uint)(path.Length * sizeof(char));
        error = GetVirtualDiskPhysicalPath(_virtualDisk, ref size, path);
        Assert.True(error == 0, $"GetVirtualDiskPhysicalPath failed ({error})");
        DevicePath = new string(path).TrimEnd('\0');
        Assert.StartsWith(@"\\.\PhysicalDrive", DevicePath);

        _device = CreateFile(DevicePath, GenericReadWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        Assert.False(_device.IsInvalid, $"Opening {DevicePath} failed ({Marshal.GetLastPInvokeError()})");
        Assert.True(DeviceIoControl(_device, DiskGetLengthInfo, 0, 0, out long length, sizeof(long), out _, 0), $"Reading the disk's size failed ({Marshal.GetLastPInvokeError()})");
        Assert.Equal(expectedBytes, length);

        // A disk attached on Windows Server can come up offline or read-only.
        var writable = new SetDiskAttributes { Version = (uint)Marshal.SizeOf<SetDiskAttributes>(), AttributesMask = 0x3 };
        DeviceIoControl(_device, DiskSetAttributes, ref writable, (uint)Marshal.SizeOf<SetDiskAttributes>(), 0, 0, out _, 0);
    }

    public string DevicePath { get; }

    /// <summary>Writes <paramref name="value"/> over the bytes from <paramref name="offset"/>, a MiB at a time.</summary>
    public void Fill(long offset, long count, byte value)
    {
        var chunk = new byte[1 << 20];
        Array.Fill(chunk, value);
        for (long done = 0; done < count; done += chunk.Length)
        {
            RandomAccess.Write(_device, chunk, offset + done);
        }

        Assert.True(FlushFileBuffers(_device), $"Flushing {DevicePath} failed ({Marshal.GetLastPInvokeError()})");
    }

    public void Dispose()
    {
        _device.Dispose();
        DetachVirtualDisk(_virtualDisk, 0, 0);
        _virtualDisk.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttachParameters
    {
        public int Version;
        public uint Reserved;
    }

    /// <summary>SET_DISK_ATTRIBUTES: 40 bytes, the attributes 8-byte aligned.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SetDiskAttributes
    {
        public uint Version;
        public byte Persist;
        public byte Reserved1A;
        public byte Reserved1B;
        public byte Reserved1C;
        public ulong Attributes;
        public ulong AttributesMask;
        public uint Reserved2A;
        public uint Reserved2B;
        public uint Reserved2C;
        public uint Reserved2D;
    }

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint OpenVirtualDisk(VirtualDisk.StorageType* type, string path, uint access, uint flags, VirtualDisk.OpenParameters* parameters, out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll")]
    private static partial uint AttachVirtualDisk(SafeFileHandle handle, nint securityDescriptor, uint flags, uint providerSpecificFlags, ref AttachParameters parameters, nint overlapped);

    [LibraryImport("virtdisk.dll")]
    private static partial uint DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerSpecificFlags);

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint sizeInBytes, [Out] char[] path);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, out long output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, ref SetDiskAttributes input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);
}

/// <summary>Junctions, which any account can make; they need neither admin rights nor Developer Mode.</summary>
internal static class Junction
{
    /// <summary>Makes <paramref name="link"/> a junction to <paramref name="target"/>, and returns it.</summary>
    public static string Create(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
        return link;
    }
}

/// <summary>This process's privileges, and a process's permissions, for the debug privilege test.</summary>
internal static unsafe partial class Privileges
{
    private const int TokenPrivilegesClass = 3;
    private const uint TokenQuery = 0x8;
    private const uint Enabled = 0x2;
    private const int KernelObject = 6;
    private const uint DaclSecurityInformation = 0x4;

    /// <summary>Whether SeDebugPrivilege is on in this process's token.</summary>
    public static bool DebugIsOn()
    {
        Assert.True(LookupPrivilegeValue(null, "SeDebugPrivilege", out long debug));
        Assert.True(OpenProcessToken(-1, TokenQuery, out var token), $"OpenProcessToken failed ({Marshal.GetLastPInvokeError()})");
        using (token)
        {
            GetTokenInformation(token, TokenPrivilegesClass, null, 0, out int size);
            var buffer = new byte[size];
            fixed (byte* info = buffer)
            {
                Assert.True(GetTokenInformation(token, TokenPrivilegesClass, info, size, out _), $"GetTokenInformation failed ({Marshal.GetLastPInvokeError()})");
                // TOKEN_PRIVILEGES: a count, then 12-byte entries of a LUID and its attributes.
                uint count = *(uint*)info;
                for (int i = 0; i < count; i++)
                {
                    byte* entry = info + 4 + (12 * i);
                    if (*(long*)entry == debug)
                    {
                        return (*(uint*)(entry + 8) & Enabled) != 0;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Gives <paramref name="process"/> permissions that let no one in, so only the debug privilege opens it.</summary>
    public static void LockOut(Process process)
    {
        var none = new RawAcl(GenericAcl.AclRevision, 0);
        byte[] acl = new byte[none.BinaryLength];
        none.GetBinaryForm(acl, 0);
        fixed (byte* dacl = acl)
        {
            uint error = SetSecurityInfo(process.SafeHandle, KernelObject, DaclSecurityInformation, 0, 0, (nint)dacl, 0);
            Assert.True(error == 0, $"SetSecurityInfo failed ({error})");
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, byte* info, int length, out int returned);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(SafeProcessHandle handle, int objectType, uint securityInfo, nint owner, nint group, nint dacl, nint sacl);
}
