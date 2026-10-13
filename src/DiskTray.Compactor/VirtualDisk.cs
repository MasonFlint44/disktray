using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskTray.Shared;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Compactor;

/// <summary>
/// Compacts a detached VHDX with Windows' virtual disk API (what <c>Optimize-VHD</c> and
/// diskpart use). Detached, the compact doesn't depend on the file system inside, which
/// Windows can't read for ext4 anyway: it gives back the blocks WSL has already let go of.
/// </summary>
internal static unsafe partial class VirtualDisk
{
    internal const uint VhdxDevice = 3;
    internal static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47e9-901F-71415A66345B");
    private const uint AccessMetaOps = 0x0020_0000;

    /// <summary>
    /// OPEN_VIRTUAL_DISK_FLAG_NO_PARENTS. WSL and Docker disks have no parents, and a
    /// differencing disk's parent paths are written inside the file, which the user can change,
    /// so they're never opened from here.
    /// </summary>
    private const uint OpenNoParents = 0x1;
    private const uint ErrorSuccess = 0;
    private const uint ErrorIoPending = 997;

    /// <summary>How long Windows gets to end a compact once it's been cancelled, or has said it's done.</summary>
    internal static readonly TimeSpan CancelTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Compacts the held disk, reporting the percentage done. Returns why it failed, or null.
    /// Windows' virtual disk service opens it by its path, so the hold is checked again once
    /// it has, to be sure it's the file that was checked.
    /// </summary>
    public static string? Compact(string path, PathHold hold, Action<int> progress) =>
        Compact(LinkSafePath.ForWindows(path), hold.Changed, progress, Windows.Calls, CancelTimeout);

    /// <summary>The compact, with the virtual disk calls and the wait after a cancel given, so tests can stand in for Windows.</summary>
    internal static string? Compact(string path, Func<string?> changed, Action<int> progress, IVirtualDiskCalls windows, TimeSpan cancelTimeout)
    {
        uint error = windows.Open(path, out var handle);
        if (error != ErrorSuccess)
        {
            handle.Dispose();
            return Describe("open", error);
        }

        using (handle)
        using (var done = new ManualResetEvent(false))
        {
            if (changed() is { } problem)
            {
                return problem;
            }

            // Lives in unmanaged memory, since Windows writes to it until the compact ends.
            var overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            bool windowsHasIt = false;
            try
            {
                overlapped->EventHandle = done.SafeWaitHandle.DangerousGetHandle();
                error = windows.Compact(handle, overlapped);
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
                    error = windows.Progress(handle, overlapped, out var state);
                    if (error != ErrorSuccess)
                    {
                        // Can't follow it any more. If it has ended anyway, its own result says
                        // how; otherwise it's cancelled (which leaves the disk as it was) rather
                        // than waited on blind. Windows owns the OVERLAPPED until the compact
                        // ends; if that doesn't come, it's left to the process's end.
                        if (!finished && !done.WaitOne(0))
                        {
                            windows.Cancel(handle, overlapped);
                            windowsHasIt = !done.WaitOne(cancelTimeout);
                        }

                        return !windowsHasIt && windows.Result(handle, overlapped) == ErrorSuccess ? null : Describe("check on", error);
                    }

                    // Every second, changed or not, so the app can tell this is still going.
                    progress(state.CompletionValue > 0 ? (int)Math.Min(100, state.CurrentValue * 100 / state.CompletionValue) : 0);

                    if (state.OperationStatus != ErrorIoPending)
                    {
                        // Windows sets the event as the compact ends, which should be at once.
                        windowsHasIt = !finished && !done.WaitOne(cancelTimeout);
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

    /// <summary>The virtual disk calls a compact makes.</summary>
    internal interface IVirtualDiskCalls
    {
        uint Open(string path, out SafeFileHandle disk);

        uint Compact(SafeFileHandle disk, NativeOverlapped* overlapped);

        uint Progress(SafeFileHandle disk, NativeOverlapped* overlapped, out OperationProgress progress);

        void Cancel(SafeFileHandle disk, NativeOverlapped* overlapped);

        /// <summary>How the compact on <paramref name="overlapped"/> ended, once it has.</summary>
        uint Result(SafeFileHandle disk, NativeOverlapped* overlapped);
    }

    /// <summary>Windows' own virtual disk API.</summary>
    private sealed class Windows : IVirtualDiskCalls
    {
        public static readonly Windows Calls = new();

        public uint Open(string path, out SafeFileHandle disk)
        {
            var type = new StorageType { DeviceId = VhdxDevice, VendorId = MicrosoftVendor };
            var open = new OpenParameters { Version = 1, RWDepth = 1 };
            return OpenVirtualDisk(ref type, path, AccessMetaOps, OpenNoParents, ref open, out disk);
        }

        public uint Compact(SafeFileHandle disk, NativeOverlapped* overlapped)
        {
            var parameters = new CompactParameters { Version = 1 };
            return CompactVirtualDisk(disk, 0, ref parameters, overlapped);
        }

        public uint Progress(SafeFileHandle disk, NativeOverlapped* overlapped, out OperationProgress progress) =>
            GetVirtualDiskOperationProgress(disk, overlapped, out progress);

        public void Cancel(SafeFileHandle disk, NativeOverlapped* overlapped) => CancelIoEx(disk, overlapped);

        public uint Result(SafeFileHandle disk, NativeOverlapped* overlapped) =>
            GetOverlappedResult(disk, overlapped, out _, false) ? ErrorSuccess : (uint)Marshal.GetLastPInvokeError();
    }

    /// <summary>VIRTUAL_STORAGE_TYPE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    /// <summary>OPEN_VIRTUAL_DISK_PARAMETERS, version 1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OpenParameters
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

    /// <summary>VIRTUAL_DISK_PROGRESS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OperationProgress
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("virtdisk.dll")]
    private static partial uint GetVirtualDiskOperationProgress(
        SafeFileHandle virtualDiskHandle,
        NativeOverlapped* overlapped,
        out OperationProgress progress);
}
