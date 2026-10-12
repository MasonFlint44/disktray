using System.Globalization;
using DiskTray.Core.Compact;
using DiskTray.Core.Detection;
using DiskTray.Core.Tray;
using DiskTray.Core.Usage;

namespace DiskTray.Core.Cleanup;

/// <summary>Runs the Docker CLI. Implemented in the app.</summary>
public interface IDockerCli
{
    /// <summary>Whether Docker Desktop's <c>docker.exe</c> is there. If it isn't, Docker Desktop isn't installed.</summary>
    bool Installed { get; }

    /// <summary>Runs <c>docker</c> with the arguments, returning its output, or null if it failed or timed out.</summary>
    string? Run(IReadOnlyList<string> args, TimeSpan timeout);
}

/// <summary>Asks WSL what's running, and shuts it down. Implemented in the app.</summary>
public interface IWslControl : IWslCommands
{
    /// <summary><c>wsl.exe --shutdown</c>. Stops every distro, Docker Desktop's included.</summary>
    bool Shutdown();

    /// <summary>
    /// Whether a distro's or Docker's disk file is open, which means WSL is using it: false
    /// if it's free or not there, null if that can't be told.
    /// </summary>
    bool? DiskInUse(string path);
}

/// <summary>Starts the elevated Compactor and talks to it over a named pipe. Implemented in the app.</summary>
public interface ICompactorLauncher
{
    /// <summary>
    /// Opens a fresh pipe and starts the Compactor with admin rights, passing it the pipe's
    /// name. Null once it has started; otherwise why it didn't, to follow "because", such as
    /// "admin permission wasn't given".
    /// </summary>
    string? Start(IReadOnlyList<string> disks, bool dryRun);

    /// <summary>Whether the Compactor has ended or hung up.</summary>
    bool HasExited { get; }

    /// <summary>The last status the Compactor sent, or null before the first.</summary>
    CompactStatus? ReadStatus();

    /// <summary>Sends <see cref="CompactorMessages.Go"/> or <see cref="CompactorMessages.Cancel"/>.</summary>
    void Signal(string message);
}

/// <summary>What to do, as picked in the Clean up dialog.</summary>
public sealed record CleanupRequest(
    bool BuildCache,
    bool DanglingImages,
    IReadOnlyList<DockerVolume> Volumes,
    IReadOnlyList<CompactTarget> Disks,
    CompactWhen When,
    bool StartDockerAfter,
    string SystemDrive,
    bool DryRun = false)
{
    public long BuildCacheBytes { get; init; }

    public long DanglingImageBytes { get; init; }

    /// <summary>
    /// Every WSL and Docker disk file, compacted or not. "When WSL is idle" waits until none
    /// is open, as well as until WSL says nothing runs.
    /// </summary>
    public IReadOnlyList<string> WslDisks { get; init; } = [];

    public bool Prunes => BuildCache || DanglingImages || Volumes.Count > 0;

    public static CleanupRequest From(CleanupState state, CleanupChoices choices, CleanupModel model, bool dryRun)
    {
        bool prune = choices.Prune && state.Docker is not null;
        return new CleanupRequest(
            prune && choices.BuildCache,
            prune && choices.DanglingImages && state.Docker!.DanglingImages > 0,
            prune ? state.Docker!.UnusedVolumes.Where(v => choices.Volumes.Contains(v.Name)).ToList() : [],
            model.ToCompact,
            choices.When,
            choices.Compact && choices.StartDockerAfter,
            state.Snapshot.SystemDrive.Label,
            dryRun)
        {
            BuildCacheBytes = state.Docker?.BuildCacheBytes ?? 0,
            DanglingImageBytes = state.Docker?.DanglingImageBytes ?? 0,
            WslDisks = state.Snapshot.Files.Where(f => f.Kind is DiskKind.WslDistro or DiskKind.DockerData).Select(f => f.Path).ToList(),
        };
    }
}

public enum CleanupStep
{
    Pruning,
    AskingForAdmin,
    WaitingForIdle,
    StoppingDocker,
    ShuttingDownWsl,
    Compacting,
    StartingDocker,

    /// <summary>Waiting for a Compactor that a run DiskTray exited during left going.</summary>
    FinishingEarlierRun,
}

/// <summary>Where the clean up is, worded for the flyout and the tray tooltip.</summary>
public sealed record CleanupProgress(CleanupStep Step, string Text)
{
    private const string StartsDockerNextTime = "Next time DiskTray starts, it starts Docker Desktop again if the clean up was going to.";

    /// <summary>Whether it can still be cancelled without leaving anything half done.</summary>
    public bool CanCancel => Step is CleanupStep.WaitingForIdle;

    /// <summary>
    /// Whether refreshes should leave WSL alone. From the wait for idle on, a <c>df</c> in a
    /// distro that was running a moment ago could start it again, and keep it from being
    /// compacted.
    /// </summary>
    public bool KeepsWslQuiet => Step is not (CleanupStep.Pruning or CleanupStep.AskingForAdmin);

    /// <summary>What to ask before DiskTray exits now, or null when exiting needs no question.</summary>
    public string? ExitQuestion => Step switch
    {
        CleanupStep.Pruning =>
            "A clean up is pruning Docker. If you exit, Docker may still finish what it's removing, but nothing gets compacted.",
        CleanupStep.StoppingDocker or CleanupStep.ShuttingDownWsl =>
            "A clean up is stopping Docker Desktop and WSL to compact their disks. If you exit now, nothing gets compacted. " + StartsDockerNextTime,
        CleanupStep.Compacting =>
            "A clean up is compacting. Compacting carries on without DiskTray, but you won't hear how it went. " + StartsDockerNextTime,
        _ => null,
    };
}

/// <summary>Points in a run worth recording, so that if DiskTray exits part way its next start can finish up.</summary>
public enum CleanupMilestone
{
    /// <summary>About to stop Docker Desktop or shut down WSL.</summary>
    Stopping,

    /// <summary>About to tell the Compactor to go, after which it carries on without the app.</summary>
    Compacting,
}

/// <summary>How the clean up went.</summary>
/// <param name="PruneProblems">What Docker refused to remove.</param>
/// <param name="Compact">The Compactor's last status, or null if it never ran.</param>
/// <param name="Problem">Why compacting didn't happen or didn't finish, when it didn't.</param>
public sealed record CleanupResult(
    CleanupRequest Request,
    long PrunedBytes,
    IReadOnlyList<string> PruneProblems,
    CompactStatus? Compact,
    string? Problem,
    bool Cancelled = false)
{
    /// <summary>Why disks that were picked weren't given to the Compactor at all.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public bool Succeeded => Problem is null && PruneProblems.Count == 0 && Skipped.Count == 0 && !Cancelled;

    public string Title => "Clean up " + (Request.DryRun ? "dry run " : "") + (Cancelled ? "cancelled" : Succeeded ? "finished" : "didn't finish");

    /// <summary>What happened, e.g. "C: got back 135 GiB. Docker 129 → 19 GiB · Ubuntu 60 → 35 GiB".</summary>
    /// <remarks>Each drive that held a compacted disk gets its own total.</remarks>
    public string Body(IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        string Size(long bytes) => ByteSize.Format(bytes, provider);
        var lines = new List<string>();

        if (Request.DryRun)
        {
            lines.Add("Nothing was changed.");
        }

        var results = Compact?.Results ?? [];
        if (!Request.DryRun && results.Any(r => r.Problem is null))
        {
            lines.Add(string.Join(" · ", results
                .GroupBy(r => DriveOf(r.Path), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => !string.Equals(g.Key, Request.SystemDrive, StringComparison.OrdinalIgnoreCase))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key} got back {Size(g.Sum(r => r.GainBytes))}")) + ".");
            lines.Add(string.Join(" · ", results.Where(r => r.Problem is null && r.BeforeBytes is not null).Select(r =>
                $"{NameOf(r.Path)} {Size(r.BeforeBytes!.Value)} → {Size(r.AfterBytes ?? r.BeforeBytes!.Value)}")));
        }
        else if (Request.DryRun && Request.Disks.Count > 0 && Compact?.State == CompactState.Done)
        {
            lines.Add($"It went through compacting {string.Join(" and ", Request.Disks.Select(d => TrayLayout.ShortName(d.File)))}.");
        }

        if (Request.Prunes && PruneProblems.Count == 0)
        {
            string pruned = $"{(Request.DryRun ? "Would have pruned" : "Pruned")} {Size(PrunedBytes)} inside Docker.";
            lines.Add(Request.Disks.Count == 0 && !Request.DryRun ? pruned + " Compact the disks to give it back to " + Request.SystemDrive : pruned);
        }

        lines.AddRange(PruneProblems);
        lines.AddRange(Skipped);
        if (Problem is not null)
        {
            lines.Add(Problem);
        }

        return string.Join("\n", lines.Where(l => l.Length > 0));
    }

    /// <summary>"D:" for a disk on D:, or the system drive when the path doesn't say.</summary>
    private string DriveOf(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) + ":" : Request.SystemDrive;

    private string NameOf(string path) =>
        Request.Disks.FirstOrDefault(d => string.Equals(d.File.Path, path, StringComparison.OrdinalIgnoreCase)) is { } disk
            ? TrayLayout.ShortName(disk.File)
            : Path.GetFileName(path);
}

/// <summary>
/// Carries out a clean up: gets the elevated Compactor going (it waits), prunes Docker, then
/// stops Docker Desktop and shuts down WSL (right away, or once nothing is running) before
/// telling the Compactor to compact. Nothing is stopped unless the Compactor is running.
/// Docker Desktop is started again at the end if asked, from here rather than the Compactor,
/// so it doesn't run with admin rights.
/// </summary>
/// <param name="milestone">Told just before each <see cref="CleanupMilestone"/>, outside dry runs.</param>
public sealed class CleanupRun(
    IDockerCli docker,
    IWslControl wsl,
    ICompactorLauncher compactor,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log,
    Action<CleanupMilestone>? milestone = null)
{
    public static readonly TimeSpan PruneTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DesktopTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long the Compactor has to write its first status once admin is granted.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Cancelling only stops the wait for WSL to go idle; once compacting starts it runs to the end.</summary>
    public async Task<CleanupResult> RunAsync(CleanupRequest request, IProgress<CleanupProgress> progress, CancellationToken cancel)
    {
        // A disk the Compactor would refuse is left out and reported, rather than stopping
        // the others from being compacted.
        var disks = request.Disks.Where(d => CompactorArgs.IsVhdx(d.File.Path)).ToList();
        var skipped = request.Disks.Where(d => !CompactorArgs.IsVhdx(d.File.Path))
            .Select(d => $"{TrayLayout.ShortName(d.File)} wasn't compacted: DiskTray can only compact a disk with a plain local path, not {d.File.Path}.")
            .ToList();

        // The admin prompt comes first, while DiskTray still has the foreground from the
        // Clean up click, so it shows in front rather than flashing in the taskbar after a
        // long prune. The Compactor waits for "go" in the meantime. Off this thread, since
        // the prompt blocks until it's answered and this is the app's UI thread.
        string? notCompacting = null;
        if (disks.Count > 0)
        {
            progress.Report(new CleanupProgress(CleanupStep.AskingForAdmin, "Waiting for admin permission to compact"));
            var paths = disks.Select(d => d.File.Path).ToList();
            notCompacting = await Task.Run(() => compactor.Start(paths, request.DryRun)) is { } notStarted
                ? $"Nothing was compacted, because {notStarted}."
                : await WaitForCompactorAsync();
        }

        var (pruned, pruneProblems) = request.Prunes ? await PruneAsync(request, progress) : (0, []);

        CleanupResult Result(CompactStatus? status, string? problem, bool cancelled = false) =>
            new(request, pruned, pruneProblems, status, problem, cancelled) { Skipped = skipped };

        if (disks.Count == 0)
        {
            return Result(null, null);
        }

        if (notCompacting is not null)
        {
            return Result(compactor.ReadStatus(), notCompacting);
        }

        if (request.When == CompactWhen.WhenIdle)
        {
            switch (await WaitForIdleAsync(request, progress, cancel))
            {
                case Idle.Cancelled:
                    compactor.Signal(CompactorMessages.Cancel);
                    return Result(null, null, cancelled: true);
                case Idle.CompactorGone:
                    return Result(compactor.ReadStatus(), CompactorGone);
            }
        }
        else
        {
            if (compactor.HasExited)
            {
                return Result(compactor.ReadStatus(), CompactorGone);
            }

            // Shutting WSL down under a running Docker Desktop only gets it restarted, so it
            // has to stop first, and anything short of knowing it's stopped means nothing
            // gets shut down.
            var distros = await Task.Run(wsl.RunningDistros);
            switch (await DesktopRunningAsync(distros))
            {
                case null:
                    compactor.Signal(CompactorMessages.Cancel);
                    return Result(null, "Nothing was compacted, because DiskTray couldn't tell whether Docker Desktop is running. Quit it, then try again.");
                case true:
                    progress.Report(new CleanupProgress(CleanupStep.StoppingDocker, "Stopping Docker Desktop"));
                    Reached(request, CleanupMilestone.Stopping);
                    if (await DockerStep(request, DockerOutput.DesktopStopCommand(DesktopTimeout), DesktopTimeout + TimeSpan.FromSeconds(10)) is null
                        && await AfterFailedStopAsync(request, progress) is { } notStopped)
                    {
                        return Result(null, notStopped);
                    }

                    break;
            }
        }

        progress.Report(new CleanupProgress(CleanupStep.ShuttingDownWsl, "Shutting down WSL"));
        Reached(request, CleanupMilestone.Stopping);
        if (!await Step(request, "wsl --shutdown", wsl.Shutdown))
        {
            // The disks would still be open, and the Compactor would only wait for them.
            compactor.Signal(CompactorMessages.Cancel);
            await StartDockerAgainAsync(request, progress);
            return Result(null, "Nothing was compacted, because WSL didn't shut down. Close what's running in WSL and try again, or restart Windows first.");
        }

        Reached(request, CleanupMilestone.Compacting);
        compactor.Signal(CompactorMessages.Go);
        var (status, quiet) = await WatchCompactorAsync(disks, progress);
        if (quiet)
        {
            // It may still be compacting, and Docker starting now would fight it for its disk.
            return Result(status, "The Compactor stopped answering, so DiskTray can't tell whether compacting finished. See the log for details."
                + (request.StartDockerAfter ? " Start Docker Desktop yourself once DiskTray.Compactor has ended (Task Manager › Details)." : ""));
        }

        await StartDockerAgainAsync(request, progress);
        return status switch
        {
            null => Result(null, "The Compactor stopped without saying how it went. See the log for details."),
            { State: CompactState.Done } => Result(status, null),
            _ => Result(status, status.Message ?? "Compacting didn't finish."),
        };
    }

    /// <summary>
    /// After <c>docker desktop stop</c> failed, which can also happen late, once Docker has
    /// stopped: asks again. Stopped, the run carries on (null). Otherwise it's cancelled, and
    /// the reason returned: still running is left running; not knowing gets Docker started
    /// again if the run was going to, since it may be stopped.
    /// </summary>
    private async Task<string?> AfterFailedStopAsync(CleanupRequest request, IProgress<CleanupProgress> progress)
    {
        switch (await DesktopRunningAsync(await Task.Run(wsl.RunningDistros)))
        {
            case false:
                log("docker desktop stop failed, but Docker Desktop has stopped; going on");
                return null;
            case true:
                compactor.Signal(CompactorMessages.Cancel);
                return "Nothing was compacted, because Docker Desktop didn't stop. Quit it, then try again.";
            default:
                compactor.Signal(CompactorMessages.Cancel);
                await StartDockerAgainAsync(request, progress);
                return "Nothing was compacted, because DiskTray couldn't tell whether Docker Desktop stopped."
                    + (request.StartDockerAfter ? " It asked it to start again, in case it had." : " Check on it, then try again.");
        }
    }

    private void Reached(CleanupRequest request, CleanupMilestone point)
    {
        if (!request.DryRun)
        {
            milestone?.Invoke(point);
        }
    }

    /// <summary>Starts Docker Desktop if the run was asked to, whether compacting worked or not.</summary>
    private async Task StartDockerAgainAsync(CleanupRequest request, IProgress<CleanupProgress> progress)
    {
        if (request.StartDockerAfter)
        {
            progress.Report(new CleanupProgress(CleanupStep.StartingDocker, "Starting Docker Desktop"));
            await DockerStep(request, DockerOutput.DesktopStartCommand, DesktopTimeout);
        }
    }

    private const string CompactorGone = "The Compactor closed before compacting, so nothing was shut down. See the log for details.";

    private async Task<(long Pruned, IReadOnlyList<string> Problems)> PruneAsync(CleanupRequest request, IProgress<CleanupProgress> progress)
    {
        progress.Report(new CleanupProgress(CleanupStep.Pruning, "Pruning Docker"));
        long pruned = 0;
        var problems = new List<string>();

        // Counts what Docker says it freed, when it says; the estimate from before otherwise.
        async Task Prune(IReadOnlyList<string> command, long estimate, string failure)
        {
            if (await DockerStep(request, command, PruneTimeout) is { } output)
            {
                pruned += request.DryRun ? estimate : DockerOutput.ParseReclaimed(output) ?? estimate;
            }
            else
            {
                problems.Add(failure);
            }
        }

        if (request.BuildCache)
        {
            await Prune(DockerOutput.PruneBuildCacheCommand, request.BuildCacheBytes, "Docker couldn't clear the build cache.");
        }

        if (request.DanglingImages)
        {
            await Prune(DockerOutput.PruneDanglingImagesCommand, request.DanglingImageBytes, "Docker couldn't remove the dangling images.");
        }

        // One at a time, so a volume that's come back into use doesn't stop the rest.
        foreach (var volume in request.Volumes)
        {
            await Prune(DockerOutput.RemoveVolumesCommand([volume.Name]), volume.SizeBytes, $"Docker couldn't remove the volume {volume.Name}; a container may be using it again.");
        }

        return (pruned, problems);
    }

    /// <summary>Runs a Docker command and returns what it printed, or null if it failed. In a dry run it's only logged.</summary>
    private async Task<string?> DockerStep(CleanupRequest request, IReadOnlyList<string> command, TimeSpan timeout)
    {
        string? output = null;
        await Step(request, "docker " + string.Join(' ', command), () => (output = docker.Run(command, timeout)) is not null);
        return request.DryRun ? "" : output;
    }

    /// <summary>Runs a step, or in a dry run only logs it.</summary>
    private async Task<bool> Step(CleanupRequest request, string what, Func<bool> run)
    {
        if (request.DryRun)
        {
            log($"Dry run: would run {what}");
            return true;
        }

        log($"Running {what}");
        bool ok = await Task.Run(run);
        if (!ok)
        {
            log($"{what} failed");
        }

        return ok;
    }

    /// <summary>Waits for the Compactor's first status. Returns why it didn't come, or null once it has.</summary>
    private async Task<string?> WaitForCompactorAsync()
    {
        for (var waited = TimeSpan.Zero; waited < StartTimeout; waited += StatusInterval)
        {
            if (compactor.ReadStatus() is { } status)
            {
                return status.State == CompactState.Waiting ? null : status.Message ?? "The Compactor stopped before it started.";
            }

            if (compactor.HasExited)
            {
                return "The Compactor closed before it started. See the log for details.";
            }

            await delay(StatusInterval, CancellationToken.None);
        }

        return "The Compactor didn't start.";
    }

    private enum Idle
    {
        Ready,
        Cancelled,
        CompactorGone,
    }

    /// <summary>
    /// Waits until Docker Desktop has quit, WSL says no distros run, and no WSL or Docker disk
    /// is open. Anything it can't find out counts as still running, so a hung or failing
    /// <c>wsl.exe</c> never gets taken as idle.
    /// </summary>
    private async Task<Idle> WaitForIdleAsync(CleanupRequest request, IProgress<CleanupProgress> progress, CancellationToken cancel)
    {
        var disks = request.WslDisks.Concat(request.Disks.Select(d => d.File.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        while (true)
        {
            if (compactor.HasExited)
            {
                return Idle.CompactorGone;
            }

            var distros = await Task.Run(wsl.RunningDistros);
            bool? desktop = await DesktopRunningAsync(distros);
            string waitingFor;
            if (desktop == true || distros is { Count: > 0 })
            {
                waitingFor = WaitingFor(desktop == true, distros ?? new HashSet<string>());
            }
            else if (desktop is null || distros is null || await Task.Run(() => AnyInUse(disks)) is not { } inUse)
            {
                waitingFor = "quit Docker Desktop and close WSL (DiskTray couldn't check just now)";
            }
            else if (inUse)
            {
                waitingFor = "quit Docker Desktop and close WSL (one of its disks is still open)";
            }
            else if (cancel.IsCancellationRequested)
            {
                // Cancel was clicked while this was checking.
                return Idle.Cancelled;
            }
            else if (await Task.Run(wsl.RunningDistros) is { Count: 0 })
            {
                // Asked once more, since the first answer is as old as the checks after it.
                return Idle.Ready;
            }
            else
            {
                waitingFor = "quit Docker Desktop and close WSL (something started just now)";
            }

            if (request.DryRun)
            {
                log($"Dry run: would wait until you {waitingFor}");
                return Idle.Ready;
            }

            progress.Report(new CleanupProgress(CleanupStep.WaitingForIdle, $"Compacts once you {waitingFor}"));
            try
            {
                await delay(IdleCheckInterval, cancel);
            }
            catch (OperationCanceledException)
            {
                return Idle.Cancelled;
            }

            if (cancel.IsCancellationRequested)
            {
                return Idle.Cancelled;
            }
        }
    }

    /// <summary>True if any of the disks is open, false if none is, null if one can't be told.</summary>
    private bool? AnyInUse(IReadOnlyList<string> disks)
    {
        bool unknown = false;
        foreach (string disk in disks)
        {
            switch (wsl.DiskInUse(disk))
            {
                case true:
                    return true;
                case null:
                    unknown = true;
                    break;
            }
        }

        return unknown ? null : false;
    }

    /// <summary>"quit Docker Desktop and close Ubuntu".</summary>
    public static string WaitingFor(bool desktopRunning, IReadOnlySet<string> distros)
    {
        var parts = new List<string>();
        if (desktopRunning || distros.Any(WslDistro.IsDockerOwnedName))
        {
            parts.Add("quit Docker Desktop");
        }

        var own = distros.Where(d => !WslDistro.IsDockerOwnedName(d)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (own.Count > 0)
        {
            parts.Add("close " + string.Join(" and ", own));
        }

        return string.Join(" and ", parts);
    }

    /// <summary>
    /// Follows the Compactor until it finishes. Quiet when it went <see cref="CompactorRun.QuietLimit"/>
    /// without sending anything, which it never does while it's working.
    /// </summary>
    private async Task<(CompactStatus? Status, bool Quiet)> WatchCompactorAsync(IReadOnlyList<CompactTarget> disks, IProgress<CleanupProgress> progress)
    {
        CompactStatus? last = null;
        object? lastHeard = null;
        var quietFor = TimeSpan.Zero;
        while (true)
        {
            var heard = compactor.ReadStatus();
            var status = heard ?? last;
            if (status is { Finished: true })
            {
                return (status, false);
            }

            if (status is { State: CompactState.Compacting } && status != last)
            {
                int index = Math.Clamp(status.Disk, 0, disks.Count - 1);
                string of = disks.Count > 1 ? $" ({index + 1} of {disks.Count})" : "";
                progress.Report(new CleanupProgress(CleanupStep.Compacting, $"Compacting {disks[index].File.Name}{of} · {status.Percent}%"));
            }

            last = status;

            // Read once more after it exits, in case the final status landed just before.
            if (compactor.HasExited)
            {
                return (compactor.ReadStatus() is { Finished: true } final ? final : null, false);
            }

            // Each status read off the pipe is a new one, even when it says the same as the last.
            quietFor = heard is not null && !ReferenceEquals(heard, lastHeard) ? TimeSpan.Zero : quietFor + StatusInterval;
            lastHeard = heard ?? lastHeard;
            if (quietFor >= CompactorRun.QuietLimit)
            {
                log($"The Compactor sent nothing for {CompactorRun.QuietLimit.TotalMinutes:0} minutes");
                return (status, true);
            }

            await delay(StatusInterval, CancellationToken.None);
        }
    }

    /// <summary>See <see cref="DockerOutput.DesktopRunning"/>. Off the caller's thread, since the app calls this from its UI thread.</summary>
    private async Task<bool?> DesktopRunningAsync(IReadOnlySet<string>? distros) =>
        docker.Installed
            ? DockerOutput.DesktopRunning(true, await Task.Run(() => docker.Run(DockerOutput.DesktopStatusCommand, TimeSpan.FromSeconds(20))), distros)
            : false;
}
