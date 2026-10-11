using System.Globalization;
using DiskTray.Core.Compact;
using DiskTray.Core.Detection;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Cleanup;

/// <summary>Runs the Docker CLI. Implemented in the app.</summary>
public interface IDockerCli
{
    /// <summary>Runs <c>docker</c> with the arguments, returning its output, or null if it failed or timed out.</summary>
    string? Run(IReadOnlyList<string> args, TimeSpan timeout);
}

/// <summary>Asks WSL what's running, and shuts it down. Implemented in the app.</summary>
public interface IWslControl
{
    IReadOnlySet<string> RunningDistros();

    /// <summary><c>wsl.exe --shutdown</c>. Stops every distro, Docker Desktop's included.</summary>
    bool Shutdown();
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
}

/// <summary>Where the clean up is, worded for the flyout and the tray tooltip.</summary>
public sealed record CleanupProgress(CleanupStep Step, string Text)
{
    /// <summary>Whether it can still be cancelled without leaving anything half done.</summary>
    public bool CanCancel => Step is CleanupStep.WaitingForIdle;
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
    public bool Succeeded => Problem is null && PruneProblems.Count == 0 && !Cancelled;

    public string Title => (Request.DryRun, Cancelled, Succeeded) switch
    {
        (true, _, _) => "Clean up dry run finished",
        (_, true, _) => "Clean up cancelled",
        (_, _, true) => "Clean up finished",
        _ => "Clean up didn't finish",
    };

    /// <summary>What happened, e.g. "C: got back 135 GiB. Docker 129 → 19 GiB · Ubuntu 60 → 35 GiB".</summary>
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
        long gain = results.Sum(r => r.GainBytes);
        if (!Request.DryRun && results.Any(r => r.Problem is null))
        {
            lines.Add($"{Request.SystemDrive} got back {Size(gain)}.");
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
        if (Problem is not null)
        {
            lines.Add(Problem);
        }

        return string.Join("\n", lines.Where(l => l.Length > 0));
    }

    private string NameOf(string path) =>
        Request.Disks.FirstOrDefault(d => string.Equals(d.File.Path, path, StringComparison.OrdinalIgnoreCase)) is { } disk
            ? TrayLayout.ShortName(disk.File)
            : Path.GetFileName(path);
}

/// <summary>
/// Carries out a clean up: prunes Docker, then gets the elevated Compactor going and, only
/// once it's running, stops Docker Desktop and shuts down WSL (right away, or once nothing is
/// running) before telling it to compact. Docker Desktop is started again at the end if asked,
/// from here rather than the Compactor, so it doesn't run with admin rights.
/// </summary>
public sealed class CleanupRun(
    IDockerCli docker,
    IWslControl wsl,
    ICompactorLauncher compactor,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
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
        var (pruned, pruneProblems) = request.Prunes ? await PruneAsync(request, progress) : (0, []);
        if (request.Disks.Count == 0)
        {
            return new CleanupResult(request, pruned, pruneProblems, null, null);
        }

        CleanupResult Result(CompactStatus? status, string? problem, bool cancelled = false) =>
            new(request, pruned, pruneProblems, status, problem, cancelled);

        progress.Report(new CleanupProgress(CleanupStep.AskingForAdmin, "Waiting for admin permission to compact"));
        if (compactor.Start(request.Disks.Select(d => d.File.Path).ToList(), request.DryRun) is { } notStarted)
        {
            return Result(null, $"Nothing was compacted, because {notStarted}.");
        }

        if (await WaitForCompactorAsync() is { } noStatus)
        {
            return Result(compactor.ReadStatus(), noStatus);
        }

        if (request.When == CompactWhen.WhenIdle)
        {
            if (!await WaitForIdleAsync(request, progress, cancel))
            {
                compactor.Signal(CompactorMessages.Cancel);
                return Result(null, null, cancelled: true);
            }
        }
        else if (await DesktopRunningAsync())
        {
            progress.Report(new CleanupProgress(CleanupStep.StoppingDocker, "Stopping Docker Desktop"));
            await Step(request, "docker desktop stop", () => docker.Run(["desktop", "stop", "--timeout", ((int)DesktopTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)], DesktopTimeout + TimeSpan.FromSeconds(10)) is not null);
        }

        progress.Report(new CleanupProgress(CleanupStep.ShuttingDownWsl, "Shutting down WSL"));
        await Step(request, "wsl --shutdown", wsl.Shutdown);

        compactor.Signal(CompactorMessages.Go);
        var status = await WatchCompactorAsync(request, progress);

        if (request.StartDockerAfter)
        {
            progress.Report(new CleanupProgress(CleanupStep.StartingDocker, "Starting Docker Desktop"));
            await Step(request, "docker desktop start", () => docker.Run(["desktop", "start", "--detach"], DesktopTimeout) is not null);
        }

        return status switch
        {
            null => Result(null, "The Compactor stopped without saying how it went. See the log for details."),
            { State: CompactState.Done } => Result(status, null),
            _ => Result(status, status.Message ?? "Compacting didn't finish."),
        };
    }

    private async Task<(long Pruned, IReadOnlyList<string> Problems)> PruneAsync(CleanupRequest request, IProgress<CleanupProgress> progress)
    {
        progress.Report(new CleanupProgress(CleanupStep.Pruning, "Pruning Docker"));
        long pruned = 0;
        var problems = new List<string>();

        async Task Prune(IReadOnlyList<string> command, long bytes, string failure)
        {
            if (await Step(request, "docker " + string.Join(' ', command), () => docker.Run(command, PruneTimeout) is not null))
            {
                pruned += bytes;
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

    /// <summary>Waits until Docker Desktop has quit and no distros run. False if cancelled first.</summary>
    private async Task<bool> WaitForIdleAsync(CleanupRequest request, IProgress<CleanupProgress> progress, CancellationToken cancel)
    {
        while (true)
        {
            bool desktop = await DesktopRunningAsync();
            var distros = await Task.Run(wsl.RunningDistros);
            if (!desktop && distros.Count == 0)
            {
                return true;
            }

            string waitingFor = WaitingFor(desktop, distros);
            if (request.DryRun)
            {
                log($"Dry run: would wait for {waitingFor}");
                return true;
            }

            progress.Report(new CleanupProgress(CleanupStep.WaitingForIdle, $"Compacts once you {waitingFor}"));
            try
            {
                await delay(IdleCheckInterval, cancel);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (cancel.IsCancellationRequested)
            {
                return false;
            }
        }
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

    private async Task<CompactStatus?> WatchCompactorAsync(CleanupRequest request, IProgress<CleanupProgress> progress)
    {
        CompactStatus? last = null;
        while (true)
        {
            var status = compactor.ReadStatus() ?? last;
            if (status is { Finished: true })
            {
                return status;
            }

            if (status is { State: CompactState.Compacting } && status != last)
            {
                var disk = request.Disks[Math.Clamp(status.Disk, 0, request.Disks.Count - 1)];
                string of = request.Disks.Count > 1 ? $" ({status.Disk + 1} of {request.Disks.Count})" : "";
                progress.Report(new CleanupProgress(CleanupStep.Compacting, $"Compacting {disk.File.Name}{of} · {status.Percent}%"));
            }

            last = status;

            // Read once more after it exits, in case the final status landed just before.
            if (compactor.HasExited)
            {
                return compactor.ReadStatus() is { Finished: true } final ? final : null;
            }

            await delay(StatusInterval, CancellationToken.None);
        }
    }

    // Off the caller's thread, since the app calls this from its UI thread.
    private Task<bool> DesktopRunningAsync() => Task.Run(() =>
        docker.Run(DockerOutput.DesktopStatusCommand, TimeSpan.FromSeconds(20)) is { } output && DockerOutput.ParseDesktopRunning(output));
}
