using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
internal static partial class TestDisk
{
    private const uint VhdxDevice = 3;
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    /// <summary>Creates an expandable VHDX at <paramref name="path"/>, 64 MiB unless said otherwise.</summary>
    public static void Create(string path, long bytes = 64L << 20)
    {
        var type = new StorageType { DeviceId = VhdxDevice, VendorId = MicrosoftVendor };
        var parameters = new CreateParameters { Version = 2, MaximumSize = (ulong)bytes };
        uint error = CreateVirtualDisk(ref type, path, 0, 0, 0, 0, ref parameters, 0, out var handle);
        handle.Dispose();
        Assert.True(error == 0, $"CreateVirtualDisk failed with error {error}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
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
        public StorageType ParentVirtualStorageType;
        public StorageType SourceVirtualStorageType;
        public Guid ResiliencyGuid;
    }

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CreateVirtualDisk(
        ref StorageType type,
        string path,
        uint accessMask,
        nint securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref CreateParameters parameters,
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
/// This process's token with Administrators turned into a deny-only group, as Windows does
/// for an admin's everyday (not "Run as administrator") token, which is how DiskTray itself
/// runs for most people.
/// </summary>
internal static unsafe partial class NonAdminToken
{
    private const uint TokenDuplicate = 0x2;
    private const uint TokenAssignPrimary = 0x1;
    private const uint TokenQuery = 0x8;
    private const uint DisableMaxPrivilege = 0x1;

    public static SafeAccessTokenHandle Create()
    {
        Assert.True(OpenProcessToken(-1, TokenDuplicate | TokenAssignPrimary | TokenQuery, out var own), $"OpenProcessToken failed ({Marshal.GetLastPInvokeError()})");
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
                return restricted;
            }
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
internal sealed partial class AttachedTestDisk : IDisposable
{
    private const uint VhdxDevice = 3;
    private const uint AccessAttachDetachInfo = 0x000E_0000;
    private const uint AttachNoDriveLetter = 0x2;
    private const uint GenericReadWrite = 0xC000_0000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint DiskGetLengthInfo = 0x0007_405C;
    private const uint DiskSetAttributes = 0x0007_C0F4;
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    private readonly SafeFileHandle _virtualDisk;
    private readonly SafeFileHandle _device;

    public AttachedTestDisk(string vhdx, long expectedBytes)
    {
        var type = new StorageType { DeviceId = VhdxDevice, VendorId = MicrosoftVendor };
        var open = new OpenParameters { Version = 1, RWDepth = 1 };
        uint error = OpenVirtualDisk(ref type, vhdx, AccessAttachDetachInfo, 0, ref open, out _virtualDisk);
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
    private struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenParameters
    {
        public int Version;
        public uint RWDepth;
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
    private static partial uint OpenVirtualDisk(ref StorageType type, string path, uint access, uint flags, ref OpenParameters parameters, out SafeFileHandle handle);

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
