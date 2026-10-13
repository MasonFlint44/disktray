namespace DiskTray.Core.Compact;

/// <summary>What the Compactor needs from Windows. Implemented in the Compactor.</summary>
public interface ICompactorHost
{
    /// <summary>What the app has said since the last call, without waiting.</summary>
    AppSignal Poll();

    /// <summary>
    /// Why the disk won't be compacted, judged before anything is shut down: its path can't
    /// be trusted, or the account that asked couldn't change it. Null when it can be. Changes
    /// nothing, and works while WSL still has the disk open.
    /// </summary>
    string? Check(string path);

    /// <summary>
    /// Holds the disk for compacting, making the <see cref="Check"/> again with it held, so
    /// it's the held file that's checked, measured and compacted. On a dry run it's only
    /// looked at, which works while WSL has it open.
    /// </summary>
    DiskHold Hold(string path, bool dryRun);

    /// <summary>Sends the status to the app. Carries on if the app has gone.</summary>
    void Report(CompactStatus status);

    /// <summary>Sends a line for the app's log.</summary>
    void Log(string message);

    void Wait(TimeSpan delay);
}

/// <summary>A disk the Compactor holds: it can't be renamed, replaced or turned into a link until disposed.</summary>
public interface IHeldDisk : IDisposable
{
    /// <summary>The file's size now, or null if it can't be read.</summary>
    long? Size { get; }

    /// <summary>
    /// Compacts the detached disk with Windows' virtual disk API, reporting the percentage
    /// done every second or so, changed or not, so the app can tell a slow compact from a
    /// stuck one. Returns why it didn't work, or null when it did.
    /// </summary>
    string? Compact(Action<int> progress);
}

/// <summary>
/// What came of holding a disk: the held disk, or why it couldn't be held. InUse means
/// something (WSL) still has it open, which can be worth waiting out.
/// </summary>
public sealed record DiskHold(IHeldDisk? Disk, string? Problem = null, bool InUse = false);

/// <summary>
/// The elevated Compactor's job: check each disk, wait until the app says WSL is shut down,
/// then compact each disk, reporting as it goes. It never shuts anything down itself; the
/// app does that without admin rights, after this has started, so turning down the admin
/// prompt leaves everything running.
/// </summary>
public sealed class CompactorRun(CompactorArgs args, ICompactorHost host)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest the Compactor goes without sending a status once it's been told to go.
    /// Every step reports at least once a second, except cancelling a compact that Windows
    /// stopped reporting on, which waits up to a minute for it; this leaves room for that and
    /// for a slow machine.
    /// </summary>
    public static readonly TimeSpan QuietLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long a disk can stay in use after WSL shuts down before giving up on it.</summary>
    public static readonly TimeSpan InUseTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs to the end and returns the exit code: 0 when every disk was compacted, 1 when
    /// some weren't, 2 when cancelled, 3 when every disk was refused before anything was
    /// shut down.
    /// </summary>
    public int Run()
    {
        // Before saying it's ready, so the app never stops Docker or shuts down WSL only for
        // disks this would refuse.
        var refused = new Dictionary<int, DiskResult>();
        for (int i = 0; i < args.Disks.Count; i++)
        {
            if (host.Check(args.Disks[i]) is { } reason)
            {
                refused[i] = new DiskResult(args.Disks[i], null, null, $"Didn't compact {args.Disks[i]}: {reason}.");
            }
        }

        if (refused.Count == args.Disks.Count)
        {
            var results = refused.OrderBy(r => r.Key).Select(r => r.Value).ToList();
            Report(new CompactStatus(
                CompactState.Failed,
                Message: "Nothing was shut down or compacted. " + string.Join(" ", results.Select(r => r.Problem)),
                Results: results,
                DryRun: args.DryRun));
            return 3;
        }

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

        var done = new List<DiskResult>();
        for (int i = 0; i < args.Disks.Count; i++)
        {
            done.Add(refused.TryGetValue(i, out var result) ? result : Compact(i, args.Disks[i], done));
        }

        bool allDone = done.All(r => r.Problem is null);
        Report(new CompactStatus(
            allDone ? CompactState.Done : CompactState.Failed,
            args.Disks.Count - 1,
            100,
            allDone ? null : string.Join(" ", done.Select(r => r.Problem).OfType<string>()),
            done,
            args.DryRun));
        return allDone ? 0 : 1;
    }

    private DiskResult Compact(int index, string disk, IReadOnlyList<DiskResult> done)
    {
        void Progress(int percent) =>
            Report(new CompactStatus(CompactState.Compacting, index, percent, Results: done, DryRun: args.DryRun));

        Progress(0);
        // WSL can take a few seconds to let go of a disk after it shuts down.
        var waited = TimeSpan.Zero;
        DiskHold hold;
        while ((hold = host.Hold(disk, args.DryRun)).InUse)
        {
            if (waited >= InUseTimeout)
            {
                return new DiskResult(disk, null, null, $"{disk} is still in use: WSL or Docker may have started again, or something else (such as Disk Management, if it's attached there) has it open.");
            }

            host.Wait(PollInterval);
            waited += PollInterval;
            // Still here: without a status now and then, the app would take the wait for a hang.
            Progress(0);
        }

        if (hold.Disk is not { } held)
        {
            return new DiskResult(disk, null, null, $"Couldn't compact {disk}: {hold.Problem}.");
        }

        using (held)
        {
            long? before = held.Size;

            // Checked and looked at, as for a real run; only holding the file's data and compacting are left out.
            if (args.DryRun)
            {
                host.Log($"Dry run: would compact {disk} ({before} bytes).");
                Progress(100);
                return new DiskResult(disk, before, before);
            }

            host.Log($"Compacting {disk} ({before} bytes).");
            string? problem = held.Compact(Progress);
            long? after = held.Size;
            host.Log(problem is null ? $"{disk} is now {after} bytes." : $"Couldn't compact {disk}: {problem}");
            return new DiskResult(disk, before, after, problem is null ? null : $"Couldn't compact {disk}: {problem}.");
        }
    }

    private void Report(CompactStatus status) => host.Report(status);
}
