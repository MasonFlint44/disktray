using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Compactor;

/// <summary>
/// Compacts a detached VHDX with Windows' virtual disk API (what <c>Optimize-VHD</c> and
/// diskpart use). Detached, the compact doesn't depend on the file system inside, which
/// Windows can't read for ext4 anyway: it gives back the blocks WSL has already let go of.
/// </summary>
internal static unsafe partial class VirtualDisk
{
    private const uint VhdxDevice = 3;
    private const uint AccessMetaOps = 0x0020_0000;

    /// <summary>
    /// OPEN_VIRTUAL_DISK_FLAG_NO_PARENTS. WSL and Docker disks have no parents, and a
    /// differencing disk's parent paths are written inside the file, which the user can change,
    /// so they're never opened from here.
    /// </summary>
    private const uint OpenNoParents = 0x1;
    private const uint ErrorSuccess = 0;
    private const uint ErrorIoPending = 997;
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromMinutes(1);
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    /// <summary>Compacts the disk, reporting the percentage done. Returns why it failed, or null.</summary>
    public static string? Compact(string path, Action<int> progress)
    {
        var type = new StorageType { DeviceId = VhdxDevice, VendorId = MicrosoftVendor };
        var open = new OpenParameters { Version = 1, RWDepth = 1 };
        uint error = OpenVirtualDisk(ref type, path, AccessMetaOps, OpenNoParents, ref open, out var handle);
        if (error != ErrorSuccess)
        {
            return Describe("open", error);
        }

        using (handle)
        using (var done = new ManualResetEvent(false))
        {
            // Lives in unmanaged memory, since Windows writes to it until the compact ends.
            var overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            bool windowsHasIt = false;
            try
            {
                overlapped->EventHandle = done.SafeWaitHandle.DangerousGetHandle();
                var parameters = new CompactParameters { Version = 1 };
                error = CompactVirtualDisk(handle, 0, ref parameters, overlapped);
                if (error == ErrorSuccess)
                {
                    // Finished straight away, with nothing to give back.
                    progress(100);
                    return null;
                }

                if (error != ErrorIoPending)
                {
                    return Describe("compact", error);
                }

                while (true)
                {
                    bool finished = done.WaitOne(TimeSpan.FromSeconds(1));
                    error = GetVirtualDiskOperationProgress(handle, overlapped, out var state);
                    if (error != ErrorSuccess)
                    {
                        // Can't follow it any more, so it's cancelled (which leaves the disk as
                        // it was) rather than waited on blind. Windows owns the OVERLAPPED until
                        // the compact ends; if that doesn't come, it's left to the process's end.
                        if (!done.WaitOne(0))
                        {
                            CancelIoEx(handle, overlapped);
                            windowsHasIt = !done.WaitOne(CancelTimeout);
                        }

                        return Describe("check on", error);
                    }

                    // Every second, changed or not, so the app can tell this is still going.
                    progress(state.CompletionValue > 0 ? (int)Math.Min(100, state.CurrentValue * 100 / state.CompletionValue) : 0);

                    if (state.OperationStatus != ErrorIoPending)
                    {
                        if (!finished)
                        {
                            done.WaitOne();
                        }

                        return state.OperationStatus == ErrorSuccess ? null : Describe("compact", state.OperationStatus);
                    }
                }
            }
            finally
            {
                if (windowsHasIt)
                {
                    // Nor is its event closed, so Windows can't set a handle that's been reused.
                    done.SafeWaitHandle.SetHandleAsInvalid();
                }
                else
                {
                    NativeMemory.Free(overlapped);
                }
            }
        }
    }

    private static string Describe(string step, uint error) =>
        $"Windows couldn't {step} the disk: {new Win32Exception((int)error).Message} ({error})";

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    /// <summary>OPEN_VIRTUAL_DISK_PARAMETERS, version 1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenParameters
    {
        public int Version;
        public uint RWDepth;
    }

    /// <summary>COMPACT_VIRTUAL_DISK_PARAMETERS, version 1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CompactParameters
    {
        public int Version;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Progress
    {
        public uint OperationStatus;
        public ulong CurrentValue;
        public ulong CompletionValue;
    }

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint OpenVirtualDisk(
        ref StorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        ref OpenParameters parameters,
        out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll")]
    private static partial uint CompactVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        uint flags,
        ref CompactParameters parameters,
        NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle handle, NativeOverlapped* overlapped);

    [LibraryImport("virtdisk.dll")]
    private static partial uint GetVirtualDiskOperationProgress(
        SafeFileHandle virtualDiskHandle,
        NativeOverlapped* overlapped,
        out Progress progress);
}
