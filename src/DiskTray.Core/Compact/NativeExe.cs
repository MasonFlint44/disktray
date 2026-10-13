using System.Reflection.PortableExecutable;
using System.Text;

namespace DiskTray.Core.Compact;

/// <summary>
/// Tells the native (NativeAOT) Compactor from a .NET build of it, by the exe itself. A .NET
/// build, framework-dependent or single-file, starts the .NET runtime, which takes settings
/// anything running as the user can change (a startup hook or a profiler to load), so it
/// mustn't run as admin. A NativeAOT exe exports <see cref="Marker"/> for debuggers; the
/// hosts that start the .NET runtime don't.
/// </summary>
public static class NativeExe
{
    public const string Marker = "DotNetRuntimeDebugHeader";

    /// <summary>More than any exe here exports, so a damaged table can't keep this reading.</summary>
    private const int MostNames = 4096;

    /// <summary>Whether <paramref name="path"/> is a NativeAOT exe. False when it isn't, or can't be read as one.</summary>
    public static bool IsNativeAot(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);
            return Exports(pe).Contains(Marker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The names a PE file exports, from its export directory.</summary>
    public static IReadOnlyList<string> Exports(PEReader pe)
    {
        if (pe.PEHeaders.PEHeader?.ExportTableDirectory is not { RelativeVirtualAddress: > 0 } directory)
        {
            return [];
        }

        // IMAGE_EXPORT_DIRECTORY: NumberOfNames at 24, AddressOfNames (an array of RVAs) at 32.
        var table = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        table.Offset = 24;
        int count = table.ReadInt32();
        table.Offset = 32;
        int names = table.ReadInt32();
        if (count is < 0 or > MostNames)
        {
            return [];
        }

        var list = pe.GetSectionData(names).GetReader();
        var found = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var name = pe.GetSectionData(list.ReadInt32()).GetReader();
            var bytes = new List<byte>();
            for (byte b; name.RemainingBytes > 0 && (b = name.ReadByte()) != 0;)
            {
                bytes.Add(b);
            }

            found.Add(Encoding.ASCII.GetString([.. bytes]));
        }

        return found;
    }
}
