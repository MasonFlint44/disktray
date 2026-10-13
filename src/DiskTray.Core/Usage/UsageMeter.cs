namespace DiskTray.Core.Usage;

/// <summary>
/// Fills in how much of each disk file is in use. Asks WSL only about distros that are
/// already running, so it never starts WSL or Docker, and otherwise falls back to the last
/// reading in the cache.
/// </summary>
public sealed class UsageMeter(IWslCommands wsl, UsageCache cache, TimeProvider clock)
{
    /// <param name="mayAskWsl">
    /// Asked before each call into WSL; false to use only the last readings from then on: once
    /// a clean up is waiting for WSL to stop, a <c>df</c> could start a distro that has just
    /// stopped. Asked each time, so a refresh that began before then stops asking too.
    /// </param>
    public DiskSnapshot Measure(DiskSnapshot snapshot, Func<bool>? mayAskWsl = null)
    {
        mayAskWsl ??= () => true;

        // Not knowing what's running is the same as nothing running here: it only means
        // falling back to the last reading.
        var running = (mayAskWsl() ? wsl.RunningDistros() : null) ?? new HashSet<string>();
        var files = snapshot.Files.Select(f => Measure(f, running, mayAskWsl)).ToList();
        cache.RetainOnly(files.Select(f => f.Path), clock.GetUtcNow());
        return snapshot with { Files = files };
    }

    private DiskFile Measure(DiskFile file, IReadOnlySet<string> running, Func<bool> mayAskWsl)
    {
        if (file.UsageMount is { } mount
            && running.Contains(mount.Distro)
            && mayAskWsl()
            && wsl.Run(mount.Distro, WslOutput.DfCommand(mount.Path)) is { } output
            && WslOutput.ParseUsedBytes(output, mount.Path) is { } used)
        {
            cache.Set(file.Path, new UsageReading(used, file.SizeBytes, clock.GetUtcNow()));
            return file with { UsedBytes = used };
        }

        if (cache.Get(file.Path) is { } last)
        {
            if (file.SizeBytes < last.FileSizeBytes)
            {
                // Compacted (or shrunk) since: no more can be in use than the file now holds,
                // and growth from here on is measured from its new size.
                last = last with { UsedBytes = Math.Min(last.UsedBytes, file.SizeBytes), FileSizeBytes = file.SizeBytes };
                cache.Set(file.Path, last);
            }

            // A disk file only grows when the disk inside it fills up, so count any growth
            // since the reading as used. Better to under- than over-promise what a compact
            // gets back.
            long grown = file.SizeBytes - last.FileSizeBytes;
            return file with { UsedBytes = last.UsedBytes + grown, UsedAsOf = last.MeasuredAt };
        }

        return file;
    }
}
