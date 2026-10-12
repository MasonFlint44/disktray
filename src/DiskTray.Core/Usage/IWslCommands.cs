namespace DiskTray.Core.Usage;

/// <summary>Runs <c>wsl.exe</c>. Implemented in the app, since WSL is Windows-only.</summary>
public interface IWslCommands
{
    /// <summary>The distros that are running now: empty if none are, null if WSL couldn't be asked.</summary>
    IReadOnlySet<string>? RunningDistros();

    /// <summary>
    /// Runs a command in a distro and returns its output, or null if it failed or timed out.
    /// WSL starts the distro if it isn't running, so only call this for running ones.
    /// </summary>
    string? Run(string distro, IReadOnlyList<string> command);
}

/// <summary>Reads the output of the WSL commands DiskTray runs.</summary>
public static class WslOutput
{
    /// <summary>
    /// The names printed by <c>wsl.exe --list --running --quiet</c>, one per line. Use
    /// <c>WSL_UTF8=1</c> so they come back as UTF-8 rather than UTF-16.
    /// </summary>
    /// <remarks>
    /// Windows 10's built-in wsl.exe ignores <c>WSL_UTF8</c>; its UTF-16 read as UTF-8 puts a
    /// NUL after every character, so those are dropped.
    /// </remarks>
    public static IReadOnlySet<string> ParseDistroNames(string output) =>
        output.Replace("\0", "", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim().Trim('\uFEFF'))
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The running distros from <c>wsl.exe --list --running --quiet</c>, or null when WSL
    /// couldn't say: it timed out or didn't start (no exit code), or it failed with one of its
    /// error codes ("Wsl/Service/…"). With none running it exits non-zero without one.
    /// </summary>
    public static IReadOnlySet<string>? ParseRunningDistros(string? output, int? exitCode) => exitCode switch
    {
        null => null,
        0 => ParseDistroNames(output ?? ""),
        _ when (output ?? "").Replace("\0", "", StringComparison.Ordinal).Contains("Wsl/", StringComparison.Ordinal) => null,
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>The <c>df</c> command whose output <see cref="ParseUsedBytes"/> reads.</summary>
    public static IReadOnlyList<string> DfCommand(string mountPoint) => ["df", "-P", "-B1", mountPoint];

    /// <summary>
    /// The used bytes from <c>df -P -B1 &lt;mount point&gt;</c> (GNU or busybox), or null
    /// unless the output is for that exact mount point. Given a plain folder, df reports the
    /// file system the folder is on, which would be the wrong disk.
    /// </summary>
    public static long? ParseUsedBytes(string output, string mountPoint)
    {
        foreach (string line in output.Split('\n').Skip(1))
        {
            // Filesystem, size, used, available, capacity ("4%"), mount point (may hold spaces).
            string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int capacity = Array.FindIndex(columns, c => c.Length > 1 && c[^1] == '%' && c[..^1].All(char.IsAsciiDigit));
            if (capacity >= 3
                && string.Join(' ', columns[(capacity + 1)..]) == mountPoint
                && long.TryParse(columns[capacity - 2], out long used))
            {
                return used;
            }
        }

        return null;
    }
}
