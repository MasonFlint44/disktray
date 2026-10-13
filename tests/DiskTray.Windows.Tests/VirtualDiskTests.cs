using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskTray.Compactor;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Windows.Tests;

/// <summary>
/// <see cref="VirtualDisk"/>'s handling of a compact it can't follow, with a stand-in for
/// Windows' virtual disk API, since the real one can't be made to fail on cue.
/// </summary>
public unsafe partial class VirtualDiskTests
{
    private const uint ErrorInvalidFunction = 1;
    private const uint ErrorOperationAborted = 995;

    private sealed partial class FakeWindows : VirtualDisk.IVirtualDiskCalls
    {
        private NativeOverlapped* _overlapped;

        /// <summary>How many progress queries work before they fail.</summary>
        public int ProgressWorks { get; set; } = 1;

        /// <summary>The compact has ended (its event set) by the time progress is first asked.</summary>
        public bool EndsAtOnce { get; set; }

        /// <summary>Cancelling ends it, as Windows does; otherwise it never ends.</summary>
        public bool EndsWhenCancelled { get; set; } = true;

        /// <summary>How it ended, by its result.</summary>
        public uint Ended { get; set; } = ErrorOperationAborted;

        public bool Cancelled { get; private set; }

        public bool Started { get; private set; }

        public uint Open(string path, out SafeFileHandle disk)
        {
            disk = new SafeFileHandle(0, ownsHandle: false);
            return 0;
        }

        public uint Compact(SafeFileHandle disk, NativeOverlapped* overlapped)
        {
            Started = true;
            _overlapped = overlapped;
            if (EndsAtOnce)
            {
                SetEvent(overlapped->EventHandle);
            }

            return 997;
        }

        public uint Progress(SafeFileHandle disk, NativeOverlapped* overlapped, out VirtualDisk.OperationProgress progress)
        {
            progress = new VirtualDisk.OperationProgress { OperationStatus = 997, CurrentValue = 1, CompletionValue = 2 };
            return ProgressWorks-- > 0 ? 0 : ErrorInvalidFunction;
        }

        public void Cancel(SafeFileHandle disk, NativeOverlapped* overlapped)
        {
            Cancelled = true;
            if (EndsWhenCancelled)
            {
                SetEvent(_overlapped->EventHandle);
            }
        }

        public uint Result(SafeFileHandle disk, NativeOverlapped* overlapped) => Ended;

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetEvent(nint handle);
    }

    private static string? Compact(FakeWindows windows, List<int>? progress = null, Func<string?>? changed = null) =>
        VirtualDisk.Compact(@"C:\disks\ext4.vhdx", changed ?? (() => null), (progress ?? []).Add, windows, TimeSpan.FromMilliseconds(200));

    [Fact]
    public void Cancels_a_compact_it_can_no_longer_follow()
    {
        var windows = new FakeWindows();
        var progress = new List<int>();

        string? problem = Compact(windows, progress);

        Assert.True(windows.Cancelled);
        Assert.StartsWith("Windows couldn't check on the disk:", problem);
        Assert.EndsWith("(1)", problem);
        Assert.Equal([50], progress);
    }

    [Fact]
    public void Goes_by_its_result_when_a_compact_ended_just_as_following_it_failed()
    {
        var windows = new FakeWindows { ProgressWorks = 0, EndsAtOnce = true, Ended = 0 };

        Assert.Null(Compact(windows));
        Assert.False(windows.Cancelled);
    }

    [Fact]
    public void Says_so_when_a_compact_that_ended_as_following_it_failed_had_failed()
    {
        var windows = new FakeWindows { ProgressWorks = 0, EndsAtOnce = true, Ended = 87 };

        Assert.StartsWith("Windows couldn't check on the disk:", Compact(windows));
        Assert.False(windows.Cancelled);
    }

    [Fact]
    public void Gives_up_on_a_compact_windows_doesnt_end_once_cancelled()
    {
        var windows = new FakeWindows { ProgressWorks = 0, EndsWhenCancelled = false };
        var clock = Stopwatch.StartNew();

        Assert.StartsWith("Windows couldn't check on the disk:", Compact(windows));

        Assert.True(windows.Cancelled);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public void Doesnt_compact_a_disk_that_changed_once_windows_opened_it()
    {
        var windows = new FakeWindows();

        Assert.Equal("it changed while it was being opened to compact", Compact(windows, changed: () => "it changed while it was being opened to compact"));
        Assert.False(windows.Started);
    }
}
