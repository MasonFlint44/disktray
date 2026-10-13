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
    [InlineData(@"C:\disks/../Windows/data.vhdx")]
    [InlineData(@"C:\disks\.\data.vhdx")]
    [InlineData(@"C:\disks\\data.vhdx")]
    [InlineData(@"C:\disks.\data.vhdx")]
    [InlineData(@"C:\disks \data.vhdx")]
    [InlineData(@"C:\disks\other.txt:data.vhdx")]
    [InlineData(@"C:\disks\*.vhdx")]
    [InlineData(@"\\?\C:\disks\data.vhdx")]
    [InlineData(@"C:\disks\NUL.vhdx")]
    [InlineData(@"C:\disks\com1.vhdx")]
    [InlineData(@"C:\disks\LPT¹.vhdx")]
    [InlineData(@"C:\aux\data.vhdx")]
    [InlineData(@"C:\CON.old\data.vhdx")]
    public void Refuses_anything_but_a_local_vhdx(string disk)
    {
        Assert.Null(CompactorArgs.Parse((Args with { Disks = [disk] }).ToArgs(), out string? problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData(@"C:\a.vhdx")]
    [InlineData(@"c:\Users\me\AppData\Local\Docker\wsl\disk\docker_data.VHDX")]
    [InlineData(@"D:\My Disks\WSL images\ext4.vhdx")]
    [InlineData(@"E:\Données\ディスク\ext4.vhdx")]
    [InlineData(@"C:\a.b\.hidden\ext4.vhdx")]
    [InlineData(@"C:\console\com10.vhdx")]
    [InlineData(@"C:\nullable\auxiliary.vhdx")]
    public void Takes_a_plain_local_vhdx(string disk) => Assert.True(CompactorArgs.IsVhdx(disk));

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

public class DiskOpenCheckTests
{
    [Fact]
    public void Counts_a_missing_file_as_free() =>
        Assert.False(DiskOpenCheck.InUse(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ext4.vhdx"), out _));

    [Fact]
    public void Counts_a_closed_file_as_free()
    {
        string path = Path.GetTempFileName();
        try
        {
            Assert.False(DiskOpenCheck.InUse(path, out string? problem));
            Assert.Null(problem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]
    [InlineData(unchecked((int)0x80070021), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(unchecked((int)0x80040020), false)]
    [InlineData(32, false)]
    public void Reads_sharing_and_lock_violations_as_in_use(int hresult, bool inUse) =>
        Assert.Equal(inUse, DiskOpenCheck.IsSharingError(hresult));
}

internal sealed class FakeCompactorHost : ICompactorHost
{
    /// <summary>What the app says on every poll.</summary>
    public AppSignal Signal { get; set; } = AppSignal.Go;

    /// <summary>The disks there are, with their sizes.</summary>
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many more times each disk reports being in use when held for compacting.</summary>
    public Dictionary<string, int> BusyFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why compacting fails, or null for it to work.</summary>
    public string? Failure { get; set; }

    /// <summary>Disks the checks before waiting refuse, with why.</summary>
    public Dictionary<string, string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Disks refused once held (they changed since the first check), with why.</summary>
    public Dictionary<string, string> RefusedWhenHeld { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every disk that was held, in order.</summary>
    public List<string> Held { get; } = [];

    /// <summary>Every disk that was let go of again.</summary>
    public List<string> LetGo { get; } = [];

    public List<CompactStatus> Statuses { get; } = [];

    public List<string> Compacted { get; } = [];

    /// <summary>Called on each wait, to let a test send signals part way.</summary>
    public Action<int>? OnWait { get; set; }

    private int _waits;

    public AppSignal Poll() => Signal;

    public string? Check(string path) => Refused.GetValueOrDefault(path);

    public DiskHold Hold(string path, bool dryRun)
    {
        // Only holding the file's data finds WSL still using it; a dry run only looks.
        if (!dryRun && BusyFor.TryGetValue(path, out int left) && left > 0)
        {
            BusyFor[path] = left - 1;
            return new DiskHold(null, "it couldn't be opened (error 32)", InUse: true);
        }

        if (!Sizes.ContainsKey(path))
        {
            return new DiskHold(null, "it wasn't found");
        }

        if (RefusedWhenHeld.TryGetValue(path, out string? why))
        {
            return new DiskHold(null, why);
        }

        Held.Add(path);
        return new DiskHold(new HeldDisk(this, path));
    }

    public void Report(CompactStatus status) => Statuses.Add(status);

    public void Log(string message)
    {
    }

    public void Wait(TimeSpan delay) => OnWait?.Invoke(++_waits);

    private sealed class HeldDisk(FakeCompactorHost host, string path) : IHeldDisk
    {
        public long? Size => host.Sizes.TryGetValue(path, out long size) ? size : null;

        public string? Compact(Action<int> progress)
        {
            host.Compacted.Add(path);
            if (host.Failure is not null)
            {
                return host.Failure;
            }

            progress(50);
            progress(100);
            host.Sizes[path] /= 4;
            return null;
        }

        public void Dispose() => host.LetGo.Add(path);
    }
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
        Assert.Equal($"Couldn't compact {A}: The parameter is incorrect. (87).", host.Statuses[^1].Results![0].Problem);
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
        Assert.Equal($"Couldn't compact {A}: it wasn't found.", host.Statuses[^1].Results![0].Problem);
        Assert.Equal([B], host.Compacted);
    }

    [Fact]
    public void Refuses_a_disk_that_changed_after_the_first_check()
    {
        var host = Host();
        host.RefusedWhenHeld[A] = "it's a link, or isn't where its path says";

        Assert.Equal(1, Run(host));

        Assert.Equal(new DiskResult(A, null, null, $"Couldn't compact {A}: it's a link, or isn't where its path says."), host.Statuses[^1].Results![0]);
        Assert.Equal([B], host.Compacted);
    }

    [Fact]
    public void Measures_and_compacts_the_disk_it_holds_and_lets_go_of_each()
    {
        var host = Host();
        host.BusyFor[A] = 2;

        Assert.Equal(0, Run(host));

        // Held once each, after WSL let go; sizes come from the held file, before and after.
        Assert.Equal([A, B], host.Held);
        Assert.Equal([A, B], host.LetGo);
        Assert.Equal([new DiskResult(A, 1000, 250), new DiskResult(B, 400, 100)], host.Statuses[^1].Results);
    }

    [Fact]
    public void Keeps_reporting_while_it_waits_for_a_disk()
    {
        var host = Host();
        host.BusyFor[A] = 5;

        Run(host);

        // Waiting status, then one when the disk starts and one for each second it waited.
        Assert.True(host.Statuses.Count(s => s is { State: CompactState.Compacting, Disk: 0, Percent: 0 }) >= 6);
    }

    [Theory]
    [InlineData(AppSignal.None, AppSignal.Go, AppSignal.Go)]
    [InlineData(AppSignal.Go, AppSignal.Gone, AppSignal.Go)]
    [InlineData(AppSignal.Go, AppSignal.Cancel, AppSignal.Go)]
    [InlineData(AppSignal.Cancel, AppSignal.Go, AppSignal.Cancel)]
    [InlineData(AppSignal.None, AppSignal.Gone, AppSignal.Gone)]
    [InlineData(AppSignal.Gone, AppSignal.None, AppSignal.Gone)]
    [InlineData(AppSignal.None, AppSignal.None, AppSignal.None)]
    public void Keeps_the_first_thing_the_app_says(AppSignal current, AppSignal next, AppSignal kept) =>
        Assert.Equal(kept, CompactorMessages.Latch(current, next));

    [Fact]
    public void Refuses_before_saying_its_ready_when_it_would_refuse_every_disk()
    {
        var host = Host();
        host.Refused[A] = "the account that asked can't change it";
        host.Refused[B] = "it's a link";

        Assert.Equal(3, Run(host));

        // The app reads a first status other than Waiting as "don't stop anything".
        var only = Assert.Single(host.Statuses);
        Assert.Equal(CompactState.Failed, only.State);
        Assert.Equal($"Nothing was shut down or compacted. Didn't compact {A}: the account that asked can't change it. Didn't compact {B}: it's a link.", only.Message);
        Assert.Equal([new DiskResult(A, null, null, $"Didn't compact {A}: the account that asked can't change it."), new DiskResult(B, null, null, $"Didn't compact {B}: it's a link.")], only.Results);
        Assert.Empty(host.Compacted);
        Assert.Empty(host.Held);
    }

    [Fact]
    public void Leaves_out_a_disk_it_would_refuse_and_compacts_the_rest()
    {
        var host = Host();
        host.Refused[A] = "the account that asked can't change it";
        host.BusyFor[A] = 1000;

        Assert.Equal(1, Run(host));

        Assert.Equal(CompactState.Waiting, host.Statuses[0].State);
        Assert.Equal([B], host.Compacted);
        Assert.Equal([B], host.Held);
        var final = host.Statuses[^1];
        Assert.Equal(CompactState.Failed, final.State);
        Assert.Equal([new DiskResult(A, null, null, $"Didn't compact {A}: the account that asked can't change it."), new DiskResult(B, 400, 100)], final.Results);
    }

    [Fact]
    public void Makes_the_same_checks_on_a_dry_run()
    {
        var host = Host();
        host.Refused[B] = "it's a link";

        Assert.Equal(1, Run(host, dryRun: true));

        Assert.Empty(host.Compacted);
        Assert.Equal([new DiskResult(A, 1000, 1000), new DiskResult(B, null, null, $"Didn't compact {B}: it's a link.")], host.Statuses[^1].Results);
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
    public bool Installed { get; set; } = true;

    public bool DesktopRunning { get; set; } = true;

    /// <summary>Set to make <c>docker desktop status</c> fail, as on an old Docker Desktop.</summary>
    public bool StatusFails { get; set; }

    /// <summary>What each command prints when it works, by its first arguments joined with spaces.</summary>
    public Dictionary<string, string> Outputs { get; } = [];

    /// <summary>Commands that fail, by their first arguments joined with spaces.</summary>
    public HashSet<string> Failing { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>Runs when <c>desktop stop</c> is, whether it works or not, to change what's true after it.</summary>
    public Action? OnStop { get; set; }

    public string? Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        string command = string.Join(' ', args);
        if (command == string.Join(' ', DockerOutput.DesktopStatusCommand))
        {
            return StatusFails ? null : DesktopRunning ? """{"Status":"running"}""" : """{"Status":"stopped"}""";
        }

        Calls.Add(command);
        if (command.StartsWith("desktop stop", StringComparison.Ordinal))
        {
            OnStop?.Invoke();
        }

        if (Failing.Any(f => command.StartsWith(f, StringComparison.Ordinal)))
        {
            return null;
        }

        if (command.StartsWith("desktop stop", StringComparison.Ordinal))
        {
            DesktopRunning = false;
        }

        return Outputs.FirstOrDefault(o => command.StartsWith(o.Key, StringComparison.Ordinal)).Value ?? "";
    }
}

internal sealed class FakeWsl(List<string> events) : IWslControl
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to make asking WSL what's running fail, as when it hangs.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Disk files that are open, and ones whose state can't be told.</summary>
    public HashSet<string> OpenDisks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> UnknownDisks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the next calls to <see cref="RunningDistros"/> say, before it goes back to <see cref="Running"/>.</summary>
    public Queue<HashSet<string>> RunningScript { get; } = new();

    /// <summary>Runs on each call to <see cref="RunningDistros"/>, as if something happened while WSL was asked.</summary>
    public Action? OnRunningDistros { get; set; }

    public bool ShutdownFails { get; set; }

    public IReadOnlySet<string>? RunningDistros()
    {
        OnRunningDistros?.Invoke();
        return Unreachable ? null : RunningScript.Count > 0 ? RunningScript.Dequeue() : Running;
    }

    public string? Run(string distro, IReadOnlyList<string> command) => null;

    public bool? DiskInUse(string path) => UnknownDisks.Contains(path) ? null : OpenDisks.Contains(path);

    public bool Shutdown()
    {
        events.Add("wsl --shutdown");
        if (ShutdownFails)
        {
            return false;
        }

        Running.Clear();
        OpenDisks.Clear();
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

    /// <summary>Runs when the Compactor is started, to check what's happened by then.</summary>
    public Action? OnStart { get; set; }

    public string? Start(IReadOnlyList<string> disks, bool dryRun)
    {
        OnStart?.Invoke();
        events.Add(Approve ? "admin granted" : "admin refused");
        Started = Approve ? new CompactorArgs(CompactorArgs.NewPipeName(), disks, dryRun) : null;
        return Approve ? null : "admin permission wasn't given";
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
    private readonly List<CleanupMilestone> _milestones = [];
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
            _ => { },
            _milestones.Add);
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

    /// <summary>Runs an idle-mode clean up that gets cancelled after <paramref name="checks"/> idle checks.</summary>
    private async Task<CleanupResult> WaitThenCancel(int checks = 5)
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        using var cancel = new CancellationTokenSource();
        return await Run(Request(CompactWhen.WhenIdle, prune: false), cancel, delays =>
        {
            if (delays == checks)
            {
                cancel.Cancel();
            }
        });
    }

    [Fact]
    public async Task Keeps_waiting_while_wsl_cant_say_whats_running()
    {
        _docker.DesktopRunning = false;
        _wsl.Unreachable = true;

        var result = await WaitThenCancel();

        Assert.True(result.Cancelled);
        Assert.DoesNotContain("wsl --shutdown", _events);
        Assert.Contains(_progress, p => p.Text.Contains("couldn't check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keeps_waiting_while_a_wsl_disk_is_open_even_if_wsl_says_nothing_runs()
    {
        _docker.DesktopRunning = false;
        _wsl.OpenDisks.Add(Ubuntu.Path);

        var result = await WaitThenCancel();

        Assert.True(result.Cancelled);
        Assert.DoesNotContain("wsl --shutdown", _events);
        Assert.Contains(_progress, p => p.Text.Contains("still open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keeps_waiting_when_a_disk_cant_be_checked()
    {
        _docker.DesktopRunning = false;
        _wsl.UnknownDisks.Add(Docker.Path);

        Assert.True((await WaitThenCancel()).Cancelled);
        Assert.DoesNotContain("wsl --shutdown", _events);
    }

    [Fact]
    public async Task Watches_every_wsl_disk_not_only_the_ones_being_compacted()
    {
        const string other = @"C:\wsl\debian\ext4.vhdx";
        _docker.DesktopRunning = false;
        _wsl.OpenDisks.Add(other);
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        using var cancel = new CancellationTokenSource();

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false) with { WslDisks = [other] }, cancel, delays =>
        {
            if (delays == 3)
            {
                cancel.Cancel();
            }
        });

        Assert.True(result.Cancelled);
        Assert.DoesNotContain("wsl --shutdown", _events);
    }

    [Fact]
    public async Task Goes_ahead_once_everything_is_known_to_be_stopped()
    {
        _docker.DesktopRunning = false;
        _wsl.Unreachable = true;
        _wsl.OpenDisks.Add(Ubuntu.Path);
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false), onDelay: delays =>
        {
            if (delays == 2)
            {
                _wsl.Unreachable = false;
            }
            else if (delays == 3)
            {
                _wsl.OpenDisks.Clear();
            }
        });

        Assert.True(result.Succeeded);
        Assert.Equal(["admin granted", "wsl --shutdown", "signal go"], _events);
    }

    [Fact]
    public async Task Counts_dockers_distros_as_docker_running_when_its_status_cant_be_read()
    {
        _docker.StatusFails = true;
        _wsl.Running.Add("docker-desktop");

        Assert.True((await WaitThenCancel()).Cancelled);
        Assert.DoesNotContain("wsl --shutdown", _events);
        Assert.Contains(_progress, p => p.Text == "Compacts once you quit Docker Desktop");
    }

    [Fact]
    public async Task Stops_waiting_when_the_compactor_goes_away()
    {
        _wsl.Running.Add("Ubuntu");
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false), onDelay: delays =>
        {
            if (delays == 2)
            {
                _launcher.HasExited = true;
                _wsl.Running.Clear();
                _docker.DesktopRunning = false;
            }
        });

        Assert.False(result.Succeeded);
        Assert.Contains("closed before compacting", result.Problem);
        Assert.DoesNotContain("wsl --shutdown", _events);
    }

    [Fact]
    public async Task Shuts_nothing_down_right_away_when_docker_desktops_state_is_unknown()
    {
        _docker.StatusFails = true;
        _wsl.Unreachable = true;
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));

        var result = await Run(Request());

        Assert.Equal(["admin granted", "signal cancel"], _events);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("desktop", StringComparison.Ordinal));
        Assert.Contains("couldn't tell whether Docker Desktop is running", result.Problem);
        Assert.Equal(17L << 30, result.PrunedBytes);
    }

    [Fact]
    public async Task Shuts_nothing_down_when_docker_desktop_doesnt_stop()
    {
        _docker.Failing.Add("desktop stop");
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));

        var result = await Run(Request(prune: false));

        Assert.Equal(["admin granted", "signal cancel"], _events);
        Assert.Contains("Docker Desktop didn't stop", result.Problem);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("desktop start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Goes_on_when_docker_desktop_stop_fails_but_docker_has_stopped()
    {
        // The command timed out, say, just after Docker had stopped.
        _docker.Failing.Add("desktop stop");
        _docker.OnStop = () => _docker.DesktopRunning = false;
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request(prune: false));

        Assert.True(result.Succeeded);
        Assert.Equal(["admin granted", "wsl --shutdown", "signal go"], _events);
        Assert.Equal(["desktop stop --timeout 180", "desktop start --detach"], _docker.Calls);
    }

    [Fact]
    public async Task Starts_docker_again_when_it_cant_tell_whether_a_failed_stop_stopped_it()
    {
        _docker.Failing.Add("desktop stop");
        _docker.OnStop = () =>
        {
            _docker.StatusFails = true;
            _wsl.Unreachable = true;
        };
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));

        var result = await Run(Request(prune: false));

        Assert.Equal(["admin granted", "signal cancel"], _events);
        Assert.Equal(["desktop stop --timeout 180", "desktop start --detach"], _docker.Calls);
        Assert.Contains("couldn't tell whether Docker Desktop stopped. It asked it to start again", result.Problem);
    }

    [Fact]
    public async Task Leaves_docker_stopped_or_not_when_unsure_and_not_asked_to_start_it()
    {
        _docker.Failing.Add("desktop stop");
        _docker.OnStop = () => _docker.StatusFails = _wsl.Unreachable = true;
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));

        var result = await Run(Request(prune: false) with { StartDockerAfter = false });

        Assert.Equal(["desktop stop --timeout 180"], _docker.Calls);
        Assert.Contains("Check on it, then try again", result.Problem);
    }

    [Fact]
    public async Task Doesnt_ask_docker_anything_when_it_isnt_installed()
    {
        _docker.Installed = false;
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request(prune: false) with { StartDockerAfter = false });

        Assert.True(result.Succeeded);
        Assert.Empty(_docker.Calls);
        Assert.Equal(["admin granted", "wsl --shutdown", "signal go"], _events);
    }

    [Fact]
    public async Task Asks_for_admin_before_pruning()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();
        _launcher.OnStart = () => Assert.Empty(_docker.Calls);

        await Run(Request());

        Assert.Equal(CleanupStep.AskingForAdmin, _progress[0].Step);
        Assert.Equal(CleanupStep.Pruning, _progress[1].Step);
    }

    [Fact]
    public async Task Counts_what_docker_says_it_freed()
    {
        _docker.Outputs["builder prune"] = "ID\tRECLAIMABLE\tSIZE\nabc\ttrue\t9GB\nTotal:\t9GB\n";
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request());

        // 9 GB from Docker for the build cache instead of the 12 GiB estimate, plus the two volumes.
        Assert.Equal(9_000_000_000L + (5L << 30), result.PrunedBytes);
    }

    [Fact]
    public void Gives_each_drive_its_own_total()
    {
        var onD = Ubuntu with { Path = @"D:\wsl\ubuntu\ext4.vhdx" };
        var request = Request() with { Disks = [new CompactTarget(Docker, null), new CompactTarget(onD, null)] };
        var status = new CompactStatus(CompactState.Done, 1, 100, Results: [new DiskResult(Docker.Path, 100L << 30, 20L << 30), new DiskResult(onD.Path, 60L << 30, 35L << 30)]);

        var body = new CleanupResult(request, 0, [], status, null).Body(CultureInfo.InvariantCulture);

        Assert.StartsWith("C: got back 80 GiB · D: got back 25 GiB.\n", body);
    }

    [Fact]
    public void Leaves_out_a_drive_whose_disks_all_failed()
    {
        var onD = Ubuntu with { Path = @"D:\wsl\ubuntu\ext4.vhdx" };
        var request = Request() with { Disks = [new CompactTarget(Docker, null), new CompactTarget(onD, null)] };
        var status = new CompactStatus(CompactState.Failed, 1, 100, Results:
            [new DiskResult(Docker.Path, 100L << 30, 20L << 30), new DiskResult(onD.Path, null, null, $"Couldn't compact {onD.Path}: it wasn't found.")]);

        var body = new CleanupResult(request, 0, [], status, null).Body(CultureInfo.InvariantCulture);

        Assert.StartsWith("C: got back 80 GiB.\n", body);
        Assert.DoesNotContain("D: got back", body);
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
    public async Task Gives_up_when_the_compactor_never_reports()
    {
        var result = await Run(Request(prune: false));

        Assert.Equal("The Compactor didn't start.", result.Problem);
        Assert.Equal(["admin granted", "signal cancel"], _events);
        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public async Task Shuts_nothing_down_when_the_compactor_goes_while_docker_desktop_stops()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _docker.OnStop = () => _launcher.HasExited = true;

        var result = await Run(Request(prune: false));

        Assert.Contains("closed before compacting, so WSL wasn't shut down", result.Problem);
        Assert.DoesNotContain("wsl --shutdown", _events);
        // Docker Desktop was stopped for it, so it's started again as asked.
        Assert.Equal(["desktop stop --timeout 180", "desktop start --detach"], _docker.Calls);
    }

    [Fact]
    public async Task Shuts_nothing_down_when_the_compactor_goes_during_the_last_idle_check()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _docker.DesktopRunning = false;
        int asked = 0;
        // The second question is the last one before shutting down.
        _wsl.OnRunningDistros = () => _launcher.HasExited = ++asked >= 2;

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false));

        Assert.Contains("closed before compacting", result.Problem);
        Assert.DoesNotContain("wsl --shutdown", _events);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("desktop start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Says_so_when_the_compactor_ends_without_a_last_status_and_still_starts_docker()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Compacting, 0, 10));
        // Gone after "go", having sent nothing final.
        var result = await Run(Request(prune: false), onDelay: delays =>
        {
            if (_events.Contains("signal go"))
            {
                _launcher.HasExited = true;
            }
        });

        Assert.Contains("stopped without saying how it went", result.Problem);
        Assert.Contains("desktop start --detach", _docker.Calls);
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

    [Fact]
    public async Task Compacts_nothing_when_wsl_doesnt_shut_down()
    {
        _wsl.ShutdownFails = true;
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request(prune: false));

        Assert.Equal(["admin granted", "wsl --shutdown", "signal cancel"], _events);
        Assert.Contains("WSL didn't shut down", result.Problem);
        // Docker was stopped for this, so it's started again as asked.
        Assert.Equal(["desktop stop --timeout 180", "desktop start --detach"], _docker.Calls);
    }

    [Fact]
    public async Task Cancel_clicked_during_an_idle_check_still_cancels()
    {
        _docker.DesktopRunning = false;
        using var cancel = new CancellationTokenSource();
        _wsl.OnRunningDistros = cancel.Cancel;
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false), cancel);

        Assert.True(result.Cancelled);
        Assert.Equal(["admin granted", "signal cancel"], _events);
    }

    [Fact]
    public async Task Asks_wsl_once_more_just_before_shutting_it_down()
    {
        _docker.DesktopRunning = false;
        // Idle at first, then a terminal opens while Docker and the disks are checked.
        _wsl.RunningScript.Enqueue([]);
        _wsl.RunningScript.Enqueue(["Ubuntu"]);
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        var result = await Run(Request(CompactWhen.WhenIdle, prune: false));

        Assert.True(result.Succeeded);
        Assert.Contains(_progress, p => p.Text.Contains("something started just now", StringComparison.Ordinal));
        Assert.Equal(["admin granted", "wsl --shutdown", "signal go"], _events);
    }

    [Fact]
    public async Task Compacts_the_other_disks_when_one_has_a_path_it_cant()
    {
        var odd = Ubuntu with { Path = "D:/wsl/ubuntu/ext4.vhdx" };
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = new CompactStatus(CompactState.Done, 0, 100, Results: [new DiskResult(Docker.Path, Docker.SizeBytes, 20L << 30)]);

        var result = await Run(Request(prune: false) with { Disks = [new CompactTarget(Docker, null), new CompactTarget(odd, null)] });

        Assert.Equal([Docker.Path], _launcher.Started!.Disks);
        Assert.Equal("Ubuntu wasn't compacted: DiskTray can only compact a disk with a plain local path, not D:/wsl/ubuntu/ext4.vhdx.", Assert.Single(result.Skipped));
        Assert.False(result.Succeeded);
        Assert.Contains("Ubuntu wasn't compacted", result.Body(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Stops_watching_a_compactor_that_goes_quiet_and_leaves_docker_alone()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Compacting, 0, 10));
        var waited = TimeSpan.Zero;

        var result = await Run(Request(prune: false), onDelay: _ => waited += CleanupRun.StatusInterval);

        Assert.Contains("stopped answering", result.Problem);
        Assert.Contains("starts Docker Desktop again once the Compactor has ended", result.Problem);
        Assert.True(result.CompactorMayBeRunning);
        Assert.DoesNotContain("desktop start --detach", _docker.Calls);
        Assert.InRange(waited, CompactorRun.QuietLimit, CompactorRun.QuietLimit + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Keeps_the_run_record_only_while_the_compactor_may_still_be_running()
    {
        var saved = new CleanupMarker(StartDockerAfter: true, DateTimeOffset.UnixEpoch, Compacting: true);
        var quiet = new CleanupResult(Request(), 0, [], null, "The Compactor stopped answering.") { CompactorMayBeRunning = true };
        var finished = quiet with { CompactorMayBeRunning = false, Problem = null };

        Assert.Equal(saved with { StoppedAnswering = true }, quiet.RecordAfter(saved));
        Assert.Null(finished.RecordAfter(saved));
        // A dry run saves no record, so there's nothing to keep.
        Assert.Null(quiet.RecordAfter(null));
    }

    [Fact]
    public async Task Stops_nothing_when_the_compactor_refuses_every_disk_before_it_waits()
    {
        _wsl.Running.Add("Ubuntu");
        _launcher.Final = new CompactStatus(
            CompactState.Failed,
            Message: "Nothing was shut down or compacted. Didn't compact it: the account that asked can't change it.",
            Results: [new DiskResult(Docker.Path, null, null, "Didn't compact it: the account that asked can't change it.")]);

        var result = await Run(Request());

        Assert.Equal(["admin granted"], _events);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("desktop", StringComparison.Ordinal));
        Assert.Equal("Nothing was shut down or compacted. Didn't compact it: the account that asked can't change it.", result.Problem);
        Assert.Equal(17L << 30, result.PrunedBytes);
        Assert.Empty(_milestones);
    }

    [Fact]
    public async Task Waits_as_long_as_the_compactor_keeps_sending()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        // The same percentage, sent once a second for longer than the quiet limit.
        for (int i = 0; i < CompactorRun.QuietLimit.TotalSeconds * 2; i++)
        {
            _launcher.Script.Enqueue(new CompactStatus(CompactState.Compacting, 0, 10));
        }

        _launcher.Final = Done();

        var result = await Run(Request(prune: false));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Records_when_it_stops_things_and_when_compacting_starts()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Final = Done();

        await Run(Request(prune: false));

        Assert.Equal([CleanupMilestone.Stopping, CleanupMilestone.Compacting], _milestones);
    }

    [Fact]
    public async Task Records_nothing_on_a_dry_run_or_when_admin_is_refused()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting, DryRun: true));
        _launcher.Final = Done(dryRun: true);
        await Run(Request(dryRun: true));

        _launcher.Approve = false;
        await Run(Request());

        Assert.Empty(_milestones);
    }

    [Fact]
    public async Task Shows_progress_for_a_disk_number_out_of_range_as_the_last_disk()
    {
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Waiting));
        _launcher.Script.Enqueue(new CompactStatus(CompactState.Compacting, 7, 50));
        _launcher.Final = Done();

        await Run(Request(prune: false));

        Assert.Equal("Compacting Ubuntu (2 of 2) · 50%", _progress.Single(p => p.Step == CleanupStep.Compacting).Text);
    }

    [Fact]
    public void Says_a_cancelled_dry_run_was_cancelled()
    {
        var result = new CleanupResult(Request(CompactWhen.WhenIdle, dryRun: true), 0, [], null, null, Cancelled: true);

        Assert.Equal("Clean up dry run cancelled", result.Title);
        Assert.Equal("Clean up dry run didn't finish", (result with { Cancelled = false, Problem = "No." }).Title);
        Assert.Equal("Clean up dry run finished", (result with { Cancelled = false }).Title);
    }

    [Theory]
    [InlineData(CleanupStep.AskingForAdmin, false, false)]
    [InlineData(CleanupStep.Pruning, true, false)]
    [InlineData(CleanupStep.WaitingForIdle, false, true)]
    [InlineData(CleanupStep.StoppingDocker, true, true)]
    [InlineData(CleanupStep.ShuttingDownWsl, true, true)]
    [InlineData(CleanupStep.Compacting, true, true)]
    [InlineData(CleanupStep.StartingDocker, false, true)]
    [InlineData(CleanupStep.FinishingEarlierRun, false, true)]
    public void Asks_before_exiting_only_when_exiting_would_leave_something_half_done(CleanupStep step, bool asks, bool quiet)
    {
        var progress = new CleanupProgress(step, "");

        Assert.Equal(asks, progress.ExitQuestion is not null);
        Assert.Equal(quiet, progress.KeepsWslQuiet);
    }

    [Theory]
    [InlineData(CleanupStep.Pruning, "pruning Docker")]
    [InlineData(CleanupStep.AskingForAdmin, "waiting to compact")]
    [InlineData(CleanupStep.WaitingForIdle, "waiting to compact")]
    [InlineData(CleanupStep.StoppingDocker, null)]
    [InlineData(CleanupStep.ShuttingDownWsl, null)]
    [InlineData(CleanupStep.Compacting, null)]
    [InlineData(CleanupStep.StartingDocker, null)]
    [InlineData(CleanupStep.FinishingEarlierRun, null)]
    public void Says_when_being_closed_from_outside_drops_a_clean_up_the_next_start_wont_finish(CleanupStep step, string? says)
    {
        string? notice = new CleanupProgress(step, "").ClosedNotice;

        if (says is null)
        {
            Assert.Null(notice);
        }
        else
        {
            Assert.Contains(says, notice);
            Assert.Contains("nothing was compacted", notice);
        }
    }

    [Fact]
    public void Says_what_exiting_does_at_each_step()
    {
        Assert.Contains("nothing gets compacted", new CleanupProgress(CleanupStep.Pruning, "").ExitQuestion);
        Assert.Contains("nothing gets compacted", new CleanupProgress(CleanupStep.ShuttingDownWsl, "").ExitQuestion);
        Assert.Contains("Compacting carries on without DiskTray", new CleanupProgress(CleanupStep.Compacting, "").ExitQuestion);
    }

    [Theory]
    [InlineData(true, new string[0], "quit Docker Desktop")]
    [InlineData(false, new[] { "docker-desktop" }, "quit Docker Desktop")]
    [InlineData(false, new[] { "Ubuntu", "Debian" }, "close Debian and Ubuntu")]
    public void Says_what_its_waiting_for(bool desktop, string[] distros, string expected) =>
        Assert.Equal(expected, CleanupRun.WaitingFor(desktop, distros.ToHashSet()));
}
