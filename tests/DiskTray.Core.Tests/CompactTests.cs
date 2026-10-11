using System.Globalization;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Compact;

namespace DiskTray.Core.Tests;

public class CompactorArgsTests
{
    private static readonly CompactorArgs Args = new(CompactorArgs.NewPipeName(), [Machine.DockerData, Machine.UbuntuBase + @"\ext4.vhdx"]);

    [Fact]
    public void Round_trips_through_the_command_line()
    {
        var parsed = CompactorArgs.Parse((Args with { DryRun = true }).ToArgs(), out string? problem);

        Assert.Null(problem);
        Assert.Equal(Args.Pipe, parsed!.Pipe);
        Assert.Equal(Args.Disks, parsed.Disks);
        Assert.True(parsed.DryRun);
    }

    [Theory]
    [InlineData(@"C:\disks\data.vhd")]
    [InlineData(@"disks\data.vhdx")]
    [InlineData(@"\\server\share\data.vhdx")]
    [InlineData("C:\\disks\\a\".vhdx")]
    [InlineData("C:\\disks\\a\r\ncompact vdisk.vhdx")]
    [InlineData(@"C:\disks\..\Windows\data.vhdx")]
    public void Refuses_anything_but_a_local_vhdx(string disk)
    {
        Assert.Null(CompactorArgs.Parse((Args with { Disks = [disk] }).ToArgs(), out string? problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData]
    [InlineData("--pipe", "DiskTray-compact-0123456789abcdef0123456789abcdef")]
    [InlineData("--disk", @"C:\a.vhdx")]
    [InlineData("--pipe", @"\\.\pipe\other", "--disk", @"C:\a.vhdx")]
    [InlineData("--pipe", "DiskTray-compact-0123456789abcdef0123456789abcdef", "--disk", @"C:\a.vhdx", "--format", "C:")]
    public void Refuses_incomplete_or_unknown_arguments(params string[] args) =>
        Assert.Null(CompactorArgs.Parse(args, out _));
}

public class CompactStatusTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var status = new CompactStatus(CompactState.Done, 1, 100, null, [new DiskResult(@"C:\a.vhdx", 100, 40)]);

        var copy = CompactStatus.FromJson(status.ToJson())!;

        Assert.Equal(CompactState.Done, copy.State);
        Assert.Equal(60, copy.Results![0].GainBytes);
        Assert.Contains("\"done\"", status.ToJson());
        Assert.DoesNotContain("\n", status.ToJson());
    }

    [Fact]
    public void Sends_status_and_log_lines_over_the_pipe()
    {
        var status = new CompactStatus(CompactState.Compacting, 1, 40);

        Assert.Equal(status with { Results = null }, CompactorMessages.Parse(CompactorMessages.Status(status)).Status! with { Results = null });
        Assert.Equal("one two", CompactorMessages.Parse(CompactorMessages.Log("one\ntwo")).Log);
        Assert.Equal((null, null), CompactorMessages.Parse("something else"));
    }

    [Theory]
    [InlineData("go", AppSignal.Go)]
    [InlineData("cancel\r", AppSignal.Cancel)]
    [InlineData("hello", AppSignal.None)]
    [InlineData(null, AppSignal.Gone)]
    public void Reads_what_the_app_says(string? line, AppSignal signal) => Assert.Equal(signal, CompactorMessages.ParseSignal(line));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"state\":\"comp")]
    public void Reads_a_half_written_file_as_nothing_yet(string? json) => Assert.Null(CompactStatus.FromJson(json));
}

internal sealed class FakeCompactorHost : ICompactorHost
{
    /// <summary>What the app says on every poll.</summary>
    public AppSignal Signal { get; set; } = AppSignal.Go;

    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many more times each disk reports being in use.</summary>
    public Dictionary<string, int> BusyFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why compacting fails, or null for it to work.</summary>
    public string? Failure { get; set; }

    public List<CompactStatus> Statuses { get; } = [];

    public List<string> Compacted { get; } = [];

    /// <summary>Called on each wait, to let a test send signals part way.</summary>
    public Action<int>? OnWait { get; set; }

    private int _waits;

    public AppSignal Poll() => Signal;

    public long? FileSize(string path) => Sizes.TryGetValue(path, out long size) ? size : null;

    public bool InUse(string path)
    {
        if (!BusyFor.TryGetValue(path, out int left) || left == 0)
        {
            return false;
        }

        BusyFor[path] = left - 1;
        return true;
    }

    public string? CompactDisk(string path, Action<int> progress)
    {
        Compacted.Add(path);
        if (Failure is not null)
        {
            return Failure;
        }

        progress(50);
        progress(100);
        Sizes[path] /= 4;
        return null;
    }

    public void Report(CompactStatus status) => Statuses.Add(status);

    public void Log(string message)
    {
    }

    public void Wait(TimeSpan delay) => OnWait?.Invoke(++_waits);
}

public class CompactorRunTests
{
    private const string A = @"C:\d\docker_data.vhdx";
    private const string B = @"C:\d\ext4.vhdx";

    private static FakeCompactorHost Host() => new()
    {
        Sizes = { [A] = 1000, [B] = 400 },
    };

    private static int Run(FakeCompactorHost host, bool dryRun = false) =>
        new CompactorRun(new CompactorArgs(CompactorArgs.NewPipeName(), [A, B], dryRun), host).Run();

    [Fact]
    public void Compacts_each_disk_and_reports_the_sizes()
    {
        var host = Host();

        Assert.Equal(0, Run(host));

        var final = host.Statuses[^1];
        Assert.Equal(CompactState.Done, final.State);
        Assert.Equal([new DiskResult(A, 1000, 250), new DiskResult(B, 400, 100)], final.Results);
        Assert.Contains(host.Statuses, s => s is { State: CompactState.Compacting, Disk: 1, Percent: 50 });
    }

    [Fact]
    public void Waits_for_the_go_signal()
    {
        var host = Host();
        host.Signal = AppSignal.None;
        host.OnWait = waits =>
        {
            if (waits == 3)
            {
                host.Signal = AppSignal.Go;
            }
        };

        Assert.Equal(0, Run(host));
        Assert.Equal(CompactState.Waiting, host.Statuses[0].State);
    }

    [Fact]
    public void Gives_up_without_touching_a_disk_when_cancelled_or_the_app_goes_away()
    {
        var cancelled = Host();
        cancelled.Signal = AppSignal.Cancel;
        var orphaned = Host();
        orphaned.Signal = AppSignal.Gone;

        Assert.Equal(2, Run(cancelled));
        Assert.Equal(2, Run(orphaned));
        Assert.Empty(cancelled.Compacted);
        Assert.Empty(orphaned.Compacted);
        Assert.Equal(CompactState.Cancelled, orphaned.Statuses[^1].State);
    }

    [Fact]
    public void Reports_a_disk_that_couldnt_be_compacted_and_goes_on()
    {
        var host = Host();
        host.Failure = "The parameter is incorrect. (87)";

        Assert.Equal(1, Run(host));

        Assert.Equal([A, B], host.Compacted);
        Assert.Equal(CompactState.Failed, host.Statuses[^1].State);
        Assert.Equal($"Couldn't compact {A}: The parameter is incorrect. (87)", host.Statuses[^1].Results![0].Problem);
    }

    [Fact]
    public void Waits_for_wsl_to_let_go_of_a_disk_but_not_forever()
    {
        var briefly = Host();
        briefly.BusyFor[A] = 3;
        var stuck = Host();
        stuck.BusyFor[A] = 1000;

        Assert.Equal(0, Run(briefly));
        Assert.Equal(1, Run(stuck));

        var result = stuck.Statuses[^1].Results![0];
        Assert.Contains("still in use", result.Problem);
        Assert.Equal([B], stuck.Compacted);
        Assert.Equal(new DiskResult(B, 400, 100), stuck.Statuses[^1].Results![1]);
    }

    [Fact]
    public void Skips_a_missing_disk()
    {
        var host = Host();
        host.Sizes.Remove(A);

        Assert.Equal(1, Run(host));
        Assert.Contains("wasn't found", host.Statuses[^1].Results![0].Problem);
    }

    [Fact]
    public void Runs_nothing_on_a_dry_run()
    {
        var host = Host();
        host.BusyFor[A] = 1000;

        Assert.Equal(0, Run(host, dryRun: true));

        Assert.Empty(host.Compacted);
        Assert.All(host.Statuses, s => Assert.True(s.DryRun));
        Assert.Equal([new DiskResult(A, 1000, 1000), new DiskResult(B, 400, 400)], host.Statuses[^1].Results);
    }
}

internal sealed class FakeDocker : IDockerCli
{
    public bool DesktopRunning { get; set; } = true;

    /// <summary>Commands that fail, by their first arguments joined with spaces.</summary>
    public HashSet<string> Failing { get; } = [];

    public List<string> Calls { get; } = [];

    public string? Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        string command = string.Join(' ', args);
        if (command == string.Join(' ', DockerOutput.DesktopStatusCommand))
        {
            return DesktopRunning ? """{"Status":"running"}""" : """{"Status":"stopped"}""";
        }

        Calls.Add(command);
        if (command.StartsWith("desktop stop", StringComparison.Ordinal))
        {
            DesktopRunning = false;
        }

        return Failing.Any(f => command.StartsWith(f, StringComparison.Ordinal)) ? null : "";
    }
}

internal sealed class FakeWsl(List<string> events) : IWslControl
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> RunningDistros() => Running;

    public bool Shutdown()
    {
        events.Add("wsl --shutdown");
        Running.Clear();
        return true;
    }
}

internal sealed class FakeLauncher(List<string> events) : ICompactorLauncher
{
    public bool Approve { get; set; } = true;

    public CompactorArgs? Started { get; private set; }

    /// <summary>What the Compactor reports, read by the app one per status check.</summary>
    public Queue<CompactStatus?> Script { get; } = new();

    public CompactStatus? Final { get; set; }

    public bool HasExited { get; set; }

    public bool Start(IReadOnlyList<string> disks, bool dryRun)
    {
        events.Add(Approve ? "admin granted" : "admin refused");
        Started = Approve ? new CompactorArgs(CompactorArgs.NewPipeName(), disks, dryRun) : null;
        return Approve;
    }

    public CompactStatus? ReadStatus() => Script.Count > 0 ? Script.Dequeue() : Final;

    public void Signal(string message) => events.Add("signal " + message);
}

public class CleanupRunTests
{
    private static readonly DiskFile Docker = Machine.Scan().Docker!;
    private static readonly DiskFile Ubuntu = Machine.Scan().PrimaryDistro!;

    private readonly List<string> _events = [];
    private readonly List<CleanupProgress> _progress = [];
    private readonly FakeDocker _docker = new();
    private readonly FakeWsl _wsl;
    private readonly FakeLauncher _launcher;

    public CleanupRunTests()
    {
        _wsl = new FakeWsl(_events);
        _launcher = new FakeLauncher(_events);
    }

    private static CleanupRequest Request(CompactWhen when = CompactWhen.RightAway, bool dryRun = false, bool prune = true, bool compact = true) =>
        new(
            prune,
            false,
            prune ? [new DockerVolume("acme_sqlserver", 3L << 30, "acme"), new DockerVolume("acme_vscode", 2L << 30, "acme")] : [],
            compact ? [new CompactTarget(Docker, 20L << 30), new CompactTarget(Ubuntu, 35L << 30)] : [],
            when,
            StartDockerAfter: compact,
            "C:",
            dryRun)
        {
            BuildCacheBytes = 12L << 30,
        };

    private CompactStatus Done(bool dryRun = false) => new(
        CompactState.Done,
        1,
        100,
        Results: [new DiskResult(Docker.Path, Docker.SizeBytes, 20L << 30), new DiskResult(Ubuntu.Path, Ubuntu.SizeBytes, 35L << 30)],
        DryRun: dryRun);

    private Task<CleanupResult> Run(CleanupRequest request, CancellationTokenSource? cancel = null, Action<int>? onDelay = null)
    {
        int delays = 0;
        var run = new CleanupRun(
            _docker,
            _wsl,
            _launcher,
            (time, token) =>
            {
                onDelay?.Invoke(++delays);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            _ => { });
        return run.RunAsync(request, new SyncProgress(_progress.Add), cancel?.Token ?? TestContext.Current.CancellationToken);
    }

    private sealed class SyncProgress(Action<CleanupProgress> report) : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => report(value);
    }

    [Fact]
    public async Task Prunes_then_shuts_down_only_after_admin_is_granted()
    {
        _wsl.Running.Add("Ubuntu");
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request());

        Assert.Equal(
            ["builder prune --force", "volume rm acme_sqlserver", "volume rm acme_vscode", "desktop stop --timeout 180", "desktop start --detach"],
            _docker.Calls);
        Assert.Equal(["admin granted", "wsl --shutdown", "signal go"], _events);
        Assert.True(result.Succeeded);
        Assert.Equal(17L << 30, result.PrunedBytes);
        Assert.Equal("Clean up finished", result.Title);
        Assert.Equal(
            "C: got back 134 GiB.\nDocker 129 GiB → 20 GiB · Ubuntu 60 GiB → 35 GiB\nPruned 17 GiB inside Docker.",
            result.Body(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Leaves_wsl_and_docker_alone_when_admin_is_refused()
    {
        _launcher.Approve = false;

        var result = await Run(Request());

        Assert.DoesNotContain("wsl --shutdown", _events);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("desktop", StringComparison.Ordinal));
        Assert.Contains("admin permission wasn't given", result.Problem);
        Assert.Equal(17L << 30, result.PrunedBytes);
    }

    [Fact]
    public async Task Changes_nothing_on_a_dry_run()
    {
        _wsl.Running.Add("Ubuntu");
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting, DryRun: true));
        _launcher.Final = Done(dryRun: true);

        var result = await Run(Request(dryRun: true));

        Assert.Empty(_docker.Calls);
        Assert.DoesNotContain("wsl --shutdown", _events);
        Assert.True(_launcher.Started!.DryRun);
        Assert.Equal("Clean up dry run finished", result.Title);
        Assert.Equal(
            "Nothing was changed.\nIt went through compacting Docker and Ubuntu.\nWould have pruned 17 GiB inside Docker.",
            result.Body(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Changes_nothing_on_a_dry_run_that_waits_for_idle()
    {
        _wsl.Running.Add("Ubuntu");
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting, DryRun: true));
        _launcher.Final = Done(dryRun: true);

        await Run(Request(CompactWhen.WhenIdle, dryRun: true));

        Assert.Empty(_docker.Calls);
        Assert.DoesNotContain("wsl --shutdown", _events);
    }

    [Fact]
    public async Task Waits_until_docker_has_quit_and_no_distros_run()
    {
        _wsl.Running.Add("Ubuntu");
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false), onDelay: delays =>
        {
            if (delays == 2)
            {
                _docker.DesktopRunning = false;
            }
            else if (delays == 4)
            {
                _wsl.Running.Clear();
            }
        });

        Assert.True(result.Succeeded);
        Assert.Contains(_progress, p => p.Text == "Compacts once you quit Docker Desktop and close Ubuntu");
        Assert.Contains(_progress, p => p.Text == "Compacts once you close Ubuntu");
        Assert.True(_progress.First(p => p.Step == CleanupStep.WaitingForIdle).CanCancel);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("desktop stop", StringComparison.Ordinal));
        Assert.Equal(["admin granted", "wsl --shutdown", "signal go"], _events);
    }

    [Fact]
    public async Task Cancelling_the_wait_tells_the_compactor_and_shuts_nothing_down()
    {
        _wsl.Running.Add("Ubuntu");
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        using var cancel = new CancellationTokenSource();

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false), cancel, delays =>
        {
            if (delays == 2)
            {
                cancel.Cancel();
            }
        });

        Assert.True(result.Cancelled);
        Assert.Equal(["admin granted", "signal cancel"], _events);
        Assert.Equal("Clean up cancelled", result.Title);
    }

    [Fact]
    public async Task Reports_compacting_progress()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Compacting, 0, 40));
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Compacting, 1, 10));
        _launcher.Final = Done();

        await Run(Request(prune: false));

        Assert.Equal(
            ["Compacting Docker data (1 of 2) · 40%", "Compacting Ubuntu (2 of 2) · 10%"],
            _progress.Where(p => p.Step == CleanupStep.Compacting).Select(p => p.Text));
    }

    [Fact]
    public async Task Starts_docker_again_even_when_compacting_fails()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = new CompactStatus(CompactState.Failed, Message: "Couldn't compact it.", Results: [new DiskResult(Docker.Path, 1, 1, "Couldn't compact it.")]);

        var result = await Run(Request(prune: false));

        Assert.Contains("desktop start --detach", _docker.Calls);
        Assert.Equal("Couldn't compact it.", result.Problem);
        Assert.Equal("Clean up didn't finish", result.Title);
    }

    [Fact]
    public async Task Gives_up_when_the_compactor_closes_without_starting()
    {
        _launcher.HasExited = true;

        var result = await Run(Request(prune: false));

        Assert.Contains("closed before it started", result.Problem);
        Assert.DoesNotContain("wsl --shutdown", _events);
    }

    [Fact]
    public async Task Keeps_going_when_a_volume_cant_be_removed()
    {
        _docker.Failing.Add("volume rm acme_sqlserver");

        var result = await Run(Request(compact: false));

        Assert.Equal(14L << 30, result.PrunedBytes);
        Assert.Contains("acme_sqlserver", result.PruneProblems.Single());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Says_what_to_do_after_only_pruning()
    {
        var result = await Run(Request(compact: false));

        Assert.Equal("Pruned 17 GiB inside Docker. Compact the disks to give it back to C:", result.Body(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(true, new string[0], "quit Docker Desktop")]
    [InlineData(false, new[] { "docker-desktop" }, "quit Docker Desktop")]
    [InlineData(false, new[] { "Ubuntu", "Debian" }, "close Debian and Ubuntu")]
    public void Says_what_its_waiting_for(bool desktop, string[] distros, string expected) =>
        Assert.Equal(expected, CleanupRun.WaitingFor(desktop, distros.ToHashSet()));
}
