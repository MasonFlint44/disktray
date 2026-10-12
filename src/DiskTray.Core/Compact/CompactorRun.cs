namespace DiskTray.Core.Compact;

/// <summary>What the Compactor needs from Windows. Implemented in the Compactor.</summary>
public interface ICompactorHost
{
    /// <summary>What the app has said since the last call, without waiting.</summary>
    AppSignal Poll();

    long? FileSize(string path);

    /// <summary>Whether something (WSL) has the file open.</summary>
    bool InUse(string path);

    /// <summary>
    /// Compacts the detached disk with Windows' virtual disk API, reporting the percentage done
    /// every second or so, changed or not, so the app can tell a slow compact from a stuck one.
    /// Returns why it failed, or null when it worked.
    /// </summary>
    string? CompactDisk(string path, Action<int> progress);

    /// <summary>Sends the status to the app. Carries on if the app has gone.</summary>
    void Report(CompactStatus status);

    /// <summary>Sends a line for the app's log.</summary>
    void Log(string message);

    void Wait(TimeSpan delay);
}

/// <summary>
/// The elevated Compactor's job: wait until the app says WSL is shut down, then compact each
/// disk, reporting as it goes. It never shuts anything down itself; the app does
/// that without admin rights, after this has started, so turning down the admin prompt
/// leaves everything running.
/// </summary>
public sealed class CompactorRun(CompactorArgs args, ICompactorHost host)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest the Compactor goes without sending a status once it's been told to go.
    /// Every step reports at least once a second; this leaves room for a slow machine.
    /// </summary>
    public static readonly TimeSpan QuietLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long a disk can stay in use after WSL shuts down before giving up on it.</summary>
    public static readonly TimeSpan InUseTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Runs to the end and returns the exit code: 0 when every disk was compacted.</summary>
    public int Run()
    {
        Report(new CompactStatus(CompactState.Waiting, DryRun: args.DryRun));
        while (true)
        {
            var signal = host.Poll();
            if (signal == AppSignal.Go)
            {
                break;
            }

            if (signal is AppSignal.Cancel or AppSignal.Gone)
            {
                Report(new CompactStatus(CompactState.Cancelled, Message: "Cancelled before compacting anything.", DryRun: args.DryRun));
                return 2;
            }

            host.Wait(PollInterval);
        }

        var results = new List<DiskResult>();
        for (int i = 0; i < args.Disks.Count; i++)
        {
            results.Add(Compact(i, args.Disks[i], results));
        }

        bool allDone = results.All(r => r.Problem is null);
        Report(new CompactStatus(
            allDone ? CompactState.Done : CompactState.Failed,
            args.Disks.Count - 1,
            100,
            allDone ? null : string.Join(" ", results.Select(r => r.Problem).OfType<string>()),
            results,
            args.DryRun));
        return allDone ? 0 : 1;
    }

    private DiskResult Compact(int index, string disk, IReadOnlyList<DiskResult> done)
    {
        void Progress(int percent) =>
            Report(new CompactStatus(CompactState.Compacting, index, percent, Results: done, DryRun: args.DryRun));

        Progress(0);
        long? before = host.FileSize(disk);
        if (before is null)
        {
            return new DiskResult(disk, null, null, $"{disk} wasn't found.");
        }

        if (args.DryRun)
        {
            host.Log($"Dry run: would compact {disk} ({before} bytes).");
            Progress(100);
            return new DiskResult(disk, before, before);
        }

        // WSL can take a few seconds to let go of a disk after it shuts down.
        var waited = TimeSpan.Zero;
        while (host.InUse(disk))
        {
            if (waited >= InUseTimeout)
            {
                return new DiskResult(disk, before, before, $"{disk} is still in use, so WSL or Docker must have started again.");
            }

            host.Wait(PollInterval);
            waited += PollInterval;
            // Still here: without a status now and then, the app would take the wait for a hang.
            Progress(0);
        }

        host.Log($"Compacting {disk} ({before} bytes).");
        string? problem = host.CompactDisk(disk, Progress);
        long? after = host.FileSize(disk);
        host.Log(problem is null ? $"{disk} is now {after} bytes." : $"Couldn't compact {disk}: {problem}");
        return new DiskResult(disk, before, after, problem is null ? null : $"Couldn't compact {disk}: {problem}");
    }

    private void Report(CompactStatus status) => host.Report(status);
}
