using System.Text.RegularExpressions;

namespace DiskTray.Core.Compact;

/// <summary>
/// What the app tells the elevated Compactor on its command line. The disk paths go on the
/// command line rather than in a file, so nothing can change them once Windows has asked
/// for admin. Everything else goes over the named pipe, so the Compactor never writes a file
/// in a folder that programs without admin rights could tamper with.
/// </summary>
/// <param name="Pipe">The pipe the app is listening on, made fresh for each run.</param>
/// <param name="DryRun">Go through the steps and report them without compacting anything.</param>
public sealed partial record CompactorArgs(string Pipe, IReadOnlyList<string> Disks, bool DryRun = false)
{
    public static string NewPipeName() => "DiskTray-compact-" + Guid.NewGuid().ToString("N");

    public IReadOnlyList<string> ToArgs()
    {
        var args = new List<string> { "--pipe", Pipe };
        if (DryRun)
        {
            args.Add("--dry-run");
        }

        foreach (string disk in Disks)
        {
            args.Add("--disk");
            args.Add(disk);
        }

        return args;
    }

    /// <summary>Reads the command line, or null with a reason when it isn't usable.</summary>
    public static CompactorArgs? Parse(IReadOnlyList<string> args, out string? problem)
    {
        string? pipe = null;
        bool dryRun = false;
        var disks = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case "--pipe":
                    pipe = Next();
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--disk":
                    if (Next() is { } disk)
                    {
                        disks.Add(disk);
                    }

                    break;
                default:
                    problem = $"Unknown argument {args[i]}.";
                    return null;
            }
        }

        problem = pipe is null || !PipePattern().IsMatch(pipe) ? "The pipe name is missing."
            : disks.Count == 0 ? "No disks to compact."
            : disks.FirstOrDefault(d => !IsVhdx(d)) is { } bad ? $"{bad} isn't a .vhdx file."
            : null;
        return problem is null ? new CompactorArgs(pipe!, disks, dryRun) : null;
    }

    /// <summary>A full local path to a .vhdx file, e.g. <c>C:\…\ext4.vhdx</c>.</summary>
    public static bool IsVhdx(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
        && !path.Any(char.IsControl)
        && !path.Contains('"')
        && !path.Split('\\').Contains("..");

    [GeneratedRegex("^DiskTray-compact-[0-9a-f]{32}$")]
    private static partial Regex PipePattern();
}
