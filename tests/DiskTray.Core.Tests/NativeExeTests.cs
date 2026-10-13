using System.Buffers.Binary;
using System.Text;
using DiskTray.Core.Compact;

namespace DiskTray.Core.Tests;

public class NativeExeTests
{
    /// <summary>
    /// A minimal 64-bit PE file whose export directory names <paramref name="exports"/>, laid
    /// out as a linker would: headers, then one section holding the export directory, its
    /// tables and the names.
    /// </summary>
    private static byte[] Pe(params string[] exports)
    {
        const int headers = 0x200;
        const int sectionRva = 0x1000;
        const int sectionSize = 0x200;
        var file = new byte[headers + sectionSize];
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), (ushort)value);
        void U32(int at, long value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), (uint)value);

        // DOS header, pointing at the PE header.
        U16(0, 0x5A4D);
        U32(0x3C, 0x40);
        U32(0x40, 0x4550);

        // COFF header: x64, one section, an optional header of 240 bytes, an executable.
        const int coff = 0x44;
        U16(coff, 0x8664);
        U16(coff + 2, 1);
        U16(coff + 16, 240);
        U16(coff + 18, 0x22);

        // Optional header (PE32+), with 16 data directories; the first is the exports.
        const int optional = coff + 20;
        U16(optional, 0x20B);
        U32(optional + 32, sectionRva);
        U32(optional + 36, headers);
        U16(optional + 48, 6);
        U32(optional + 56, sectionRva + 0x1000);
        U32(optional + 60, headers);
        U16(optional + 68, 2);
        U32(optional + 108, 16);
        if (exports.Length > 0)
        {
            U32(optional + 112, sectionRva);
            U32(optional + 116, sectionSize);
        }

        // The one section.
        const int section = optional + 240;
        Encoding.ASCII.GetBytes(".edata").CopyTo(file, section);
        U32(section + 8, sectionSize);
        U32(section + 12, sectionRva);
        U32(section + 16, sectionSize);
        U32(section + 20, headers);
        U32(section + 36, 0x4000_0040);

        // IMAGE_EXPORT_DIRECTORY (40 bytes), then the function, name and ordinal tables, then the names.
        int n = exports.Length;
        int functions = sectionRva + 40;
        int names = functions + 4 * n;
        int ordinals = names + 4 * n;
        int next = ordinals + 2 * n;
        int At(int rva) => headers + rva - sectionRva;
        U32(At(sectionRva) + 20, n);
        U32(At(sectionRva) + 24, n);
        U32(At(sectionRva) + 28, functions);
        U32(At(sectionRva) + 32, names);
        U32(At(sectionRva) + 36, ordinals);
        for (int i = 0; i < n; i++)
        {
            U32(At(names) + 4 * i, next);
            U16(At(ordinals) + 2 * i, i);
            Encoding.ASCII.GetBytes(exports[i]).CopyTo(file, At(next));
            next += exports[i].Length + 1;
        }

        return file;
    }

    private static bool IsNativeAot(byte[] contents)
    {
        string path = Path.Combine(Path.GetTempPath(), "DiskTray.Tests-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, contents);
        try
        {
            return NativeExe.IsNativeAot(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Knows_a_nativeaot_exe_by_what_it_exports()
    {
        Assert.True(IsNativeAot(Pe("DotNetRuntimeDebugHeader")));
        Assert.True(IsNativeAot(Pe("Something", NativeExe.Marker, "Else")));
    }

    [Fact]
    public void Doesnt_count_an_exe_without_that_export()
    {
        Assert.False(IsNativeAot(Pe()));
        Assert.False(IsNativeAot(Pe("DotNetRuntimeInfo")));
        Assert.False(IsNativeAot(Pe("DotNetRuntimeDebugHeaderX")));
    }

    [Fact]
    public void Doesnt_count_a_dotnet_assembly_or_a_file_that_isnt_an_exe()
    {
        Assert.False(NativeExe.IsNativeAot(typeof(NativeExe).Assembly.Location));
        Assert.False(IsNativeAot(Encoding.UTF8.GetBytes("not really an exe")));
        Assert.False(IsNativeAot([]));
        Assert.False(NativeExe.IsNativeAot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe")));
    }

    [Fact]
    public void Reads_every_name_in_the_export_table()
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(Pe("One", "Two", "Three")));

        Assert.Equal(["One", "Two", "Three"], NativeExe.Exports(pe));
    }
}
