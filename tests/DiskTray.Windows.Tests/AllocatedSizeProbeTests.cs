using System.IO;
using System.Runtime.InteropServices;
using DiskTray.App;
using DiskTray.Core.Detection;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Windows.Tests;

/// <summary><see cref="AllocatedSizeProbe"/>: how big a disk file is on its drive.</summary>
public partial class AllocatedSizeProbeTests
{
    private const uint SetSparse = 0x0009_00C4;

    [Fact]
    public void Reads_a_sparse_file_as_the_space_it_takes()
    {
        using var folder = new TestFolder();
        string path = Path.Combine(folder.Path, "sparse.vhdx");
        using (var file = new FileStream(path, FileMode.CreateNew))
        {
            Assert.True(DeviceIoControl(file.SafeFileHandle, SetSparse, 0, 0, 0, 0, out _, 0), $"FSCTL_SET_SPARSE failed ({Marshal.GetLastPInvokeError()})");
            file.SetLength(1L << 30);
        }

        Assert.Equal(1L << 30, new SystemDiskProbe().GetFileSize(path));
        Assert.True(new AllocatedSizeProbe(new SystemDiskProbe()).GetFileSize(path) < 1L << 20);
    }

    [Fact]
    public void Reads_a_plain_file_as_its_length()
    {
        using var folder = new TestFolder();
        string path = folder.File("plain.vhdx");

        Assert.Equal(new FileInfo(path).Length, new AllocatedSizeProbe(new SystemDiskProbe()).GetFileSize(path));
        Assert.Null(new AllocatedSizeProbe(new SystemDiskProbe()).GetFileSize(Path.Combine(folder.Path, "missing.vhdx")));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
