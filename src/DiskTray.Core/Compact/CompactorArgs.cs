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

    /// <summary>
    /// A full local path to a .vhdx file, e.g. <c>C:\…\ext4.vhdx</c>, written plainly:
    /// backslashes, no <c>.</c> or <c>..</c>, no empty parts or parts ending in a dot or space
    /// (which Windows quietly drops), no wildcards, no <c>:stream</c> and no device names
    /// (<c>NUL.vhdx</c> and the like, which older Windows opens as the device). An 8.3 short name
    /// (<c>PROGRA~1</c>) passes here; the Compactor's path hold refuses it, since the file's
    /// real path then differs from the one given.
    /// </summary>
    public static bool IsVhdx(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || !path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            || path.Any(c => char.IsControl(c) || c is '"' or '/' or '*' or '?' or '<' or '>' or '|')
            || path.IndexOf(':', 2) >= 0)
        {
            return false;
        }

        return path[3..].Split('\\').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' ') && !IsDeviceName(part));
    }

    /// <summary>CON, NUL, COM1 and so on, with or without an extension, as Windows reserves them.</summary>
    private static bool IsDeviceName(string part)
    {
        string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && stem[..3] is "COM" or "LPT" && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³'));
    }

    [GeneratedRegex("^DiskTray-compact-[0-9a-f]{32}$")]
    private static partial Regex PipePattern();
}
