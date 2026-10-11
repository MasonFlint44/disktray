namespace DiskTray.Core.Usage;

/// <summary>
/// Fills in how much of each disk file is in use. Asks WSL only about distros that are
/// already running, so it never starts WSL or Docker, and otherwise falls back to the last
/// reading in the cache.
/// </summary>
public sealed class UsageMeter(IWslCommands wsl, UsageCache cache, TimeProvider clock)
{
    public DiskSnapshot Measure(DiskSnapshot snapshot)
    {
        var running = wsl.RunningDistros();
        var files = snapshot.Files.Select(f => Measure(f, running)).ToList();
        cache.RetainOnly(files.Select(f => f.Path));
        return snapshot with { Files = files };
    }

    private DiskFile Measure(DiskFile file, IReadOnlySet<string> running)
    {
        if (file.UsageMount is { } mount
            && running.Contains(mount.Distro)
            && wsl.Run(mount.Distro, WslOutput.DfCommand(mount.Path)) is { } output
            && WslOutput.ParseUsedBytes(output, mount.Path) is { } used)
        {
            cache.Set(file.Path, new UsageReading(used, file.SizeBytes, clock.GetUtcNow()));
            return file with { UsedBytes = used };
        }

        if (cache.Get(file.Path) is { } last)
        {
            // A disk file only grows when the disk inside it fills up, so count any growth
            // since the reading as used. Better to under- than over-promise what a compact
            // gets back.
            long grown = Math.Max(0, file.SizeBytes - last.FileSizeBytes);
            return file with { UsedBytes = last.UsedBytes + grown, UsedAsOf = last.MeasuredAt };
        }

        return file;
    }
}
