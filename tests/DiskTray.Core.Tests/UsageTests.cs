using DiskTray.Core.Usage;

namespace DiskTray.Core.Tests;

public class WslOutputTests
{
    private const string GnuDf = """
        Filesystem          1-blocks        Used    Available Capacity Mounted on
        /dev/sdd       1081101176832 37418942464 988689879040       4% /

        """;

    private const string BusyboxDf = """
        Filesystem           1-blocks       Used Available Capacity Mounted on
        /dev/sdf             1081101176832 37396930560 988711890944   4% /mnt/docker-desktop-disk

        """;

    [Fact]
    public void Reads_gnu_df() => Assert.Equal(37_418_942_464, WslOutput.ParseUsedBytes(GnuDf, "/"));

    [Fact]
    public void Reads_busybox_df() =>
        Assert.Equal(37_396_930_560, WslOutput.ParseUsedBytes(BusyboxDf.Replace("\n", "\r\n"), "/mnt/docker-desktop-disk"));

    [Fact]
    public void Reads_mount_points_with_spaces() =>
        Assert.Equal(5, WslOutput.ParseUsedBytes("Filesystem 1-blocks Used Available Capacity Mounted on\nx 10 5 5 50% /mnt/my disk\n", "/mnt/my disk"));

    [Fact]
    public void Rejects_the_parent_file_system()
    {
        // df on a folder that isn't a mount point reports the disk the folder is on.
        const string output = """
            Filesystem           1-blocks       Used Available Capacity Mounted on
            /dev/sdg             130823168  64037888  56584192  53% /
            """;

        Assert.Null(WslOutput.ParseUsedBytes(output, "/mnt/docker-desktop-disk"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("df: /mnt/nope: can't find mount point")]
    [InlineData("Filesystem 1-blocks Used Available Capacity Mounted on\nx 10 lots 5 50% /")]
    public void Ignores_unusable_df_output(string output) => Assert.Null(WslOutput.ParseUsedBytes(output, "/"));

    [Fact]
    public void Reads_running_distro_names() =>
        Assert.True(WslOutput.ParseDistroNames("\uFEFFUbuntu\r\nwsl-vpnkit\r\ndocker-desktop\r\n\r\n")
            .SetEquals(["Ubuntu", "wsl-vpnkit", "docker-desktop"]));

    [Fact]
    public void Matches_distro_names_ignoring_case() => Assert.Contains("ubuntu", WslOutput.ParseDistroNames("Ubuntu\n"));

    [Fact]
    public void Reads_names_that_came_back_as_utf16() =>
        Assert.Equal(["Ubuntu", "Debian"], WslOutput.ParseDistroNames("U\0b\0u\0n\0t\0u\0\r\0\n\0D\0e\0b\0i\0a\0n\0\r\0\n\0"));

    [Fact]
    public void Knows_whats_running_only_when_wsl_answered()
    {
        Assert.Equal(["Ubuntu"], WslOutput.ParseRunningDistros("Ubuntu\r\n", 0));
        // None running: WSL says so and exits non-zero, without an error code.
        Assert.Empty(WslOutput.ParseRunningDistros("There are no running distributions.\r\n", -1)!);
        // Timed out or didn't start, or failed with one of WSL's error codes.
        Assert.Null(WslOutput.ParseRunningDistros(null, null));
        Assert.Null(WslOutput.ParseRunningDistros("Catastrophic failure\r\nError code: Wsl/Service/E_UNEXPECTED\r\n", -1));
    }
}

public class UsageCacheTests
{
    private static readonly UsageReading Reading = new(34, 138, new DateTimeOffset(2026, 10, 11, 14, 14, 0, TimeSpan.Zero));

    [Fact]
    public void Round_trips_through_json()
    {
        var cache = new UsageCache();
        cache.Set(Machine.DockerData, Reading);

        var loaded = UsageCache.FromJson(cache.ToJson());

        Assert.Equal(Reading, loaded.Get(Machine.DockerData.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "disks": [1, 2] }""")]
    [InlineData("""{ "disks": { "C:\\d.vhdx": { "usedBytes": -1, "fileSizeBytes": 5, "measuredAt": "2026-10-11T14:14:00Z" } } }""")]
    public void Starts_empty_when_the_file_is_unusable(string? json) => Assert.Null(UsageCache.FromJson(json).Get(@"C:\d.vhdx"));

    [Fact]
    public void Forgets_files_only_once_theyve_been_gone_a_while()
    {
        var cache = new UsageCache();
        cache.Set(@"C:\a.vhdx", Reading);
        cache.Set(@"C:\b.vhdx", Reading);

        // Docker detection switched off for a day: its reading stays.
        cache.RetainOnly([@"c:\A.vhdx"], Reading.MeasuredAt.AddDays(1));
        Assert.NotNull(cache.Get(@"C:\b.vhdx"));

        cache.RetainOnly([@"c:\A.vhdx"], Reading.MeasuredAt + UsageCache.ForgetAfter);
        Assert.NotNull(cache.Get(@"C:\a.vhdx"));
        Assert.Null(cache.Get(@"C:\b.vhdx"));
    }
}

public class UsageMeterTests
{
    private const string DockerDf = "docker-desktop: df -P -B1 /mnt/docker-desktop-disk";
    private const string UbuntuDf = "Ubuntu: df -P -B1 /";
    private const long DockerUsed = 37_396_930_560;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);

    private static FakeWslCommands RunningWsl()
    {
        var wsl = new FakeWslCommands { Running = { "Ubuntu", "wsl-vpnkit", "docker-desktop" } };
        wsl.Outputs[DockerDf] = $"Filesystem 1-blocks Used Available Capacity Mounted on\n/dev/sdf 1081101176832 {DockerUsed} 988711890944 4% /mnt/docker-desktop-disk\n";
        wsl.Outputs[UbuntuDf] = "Filesystem 1-blocks Used Available Capacity Mounted on\n/dev/sdd 1081101176832 37418942464 988689879040 4% /\n";
        return wsl;
    }

    private static DiskSnapshot Measure(FakeWslCommands wsl, UsageCache cache, DiskSnapshot? snapshot = null) =>
        new UsageMeter(wsl, cache, new FixedClock(Now)).Measure(snapshot ?? Machine.Scan());

    [Fact]
    public void Measures_running_distros()
    {
        var cache = new UsageCache();

        var snapshot = Measure(RunningWsl(), cache);

        Assert.Equal(DockerUsed, snapshot.Docker?.UsedBytes);
        Assert.Equal(Machine.DockerSize - DockerUsed, snapshot.Docker?.ReclaimableBytes);
        Assert.Null(snapshot.Docker?.UsedAsOf);
        Assert.Equal(37_418_942_464, snapshot.PrimaryDistro?.UsedBytes);
        Assert.Equal(new UsageReading(DockerUsed, Machine.DockerSize, Now), cache.Get(Machine.DockerData));
    }

    [Fact]
    public void Never_runs_anything_in_a_stopped_distro()
    {
        var wsl = RunningWsl();
        wsl.Running.Clear();

        var snapshot = Measure(wsl, new UsageCache());

        Assert.Empty(wsl.Calls);
        Assert.All(snapshot.Files, f => Assert.Null(f.UsedBytes));
    }

    [Fact]
    public void Falls_back_to_the_last_reading()
    {
        var wsl = RunningWsl();
        wsl.Running.Remove("docker-desktop");
        var cache = new UsageCache();
        var earlier = Now.AddHours(-4);
        cache.Set(Machine.DockerData, new UsageReading(30_000_000_000, Machine.DockerSize, earlier));

        var docker = Measure(wsl, cache).Docker;

        Assert.Equal(30_000_000_000, docker?.UsedBytes);
        Assert.Equal(earlier, docker?.UsedAsOf);
        Assert.DoesNotContain(DockerDf, wsl.Calls);
    }

    [Fact]
    public void Counts_growth_since_the_last_reading_as_used()
    {
        var wsl = new FakeWslCommands();
        var cache = new UsageCache();
        cache.Set(Machine.DockerData, new UsageReading(30_000_000_000, Machine.DockerSize - 1_000_000_000, Now.AddDays(-2)));

        Assert.Equal(31_000_000_000, Measure(wsl, cache).Docker?.UsedBytes);
    }

    [Fact]
    public void Keeps_the_last_reading_when_df_fails()
    {
        var wsl = RunningWsl();
        wsl.Outputs.Remove(DockerDf);
        var cache = new UsageCache();
        cache.Set(Machine.DockerData, new UsageReading(30_000_000_000, Machine.DockerSize, Now.AddHours(-1)));

        Assert.Equal(Now.AddHours(-1), Measure(wsl, cache).Docker?.UsedAsOf);
    }

    [Fact]
    public void Forgets_disk_files_that_have_been_gone_a_month()
    {
        var cache = new UsageCache();
        cache.Set(@"C:\old\ext4.vhdx", new UsageReading(1, 2, Now - UsageCache.ForgetAfter));
        cache.Set(@"C:\recent\ext4.vhdx", new UsageReading(1, 2, Now.AddDays(-1)));

        Measure(new FakeWslCommands(), cache);

        Assert.Null(cache.Get(@"C:\old\ext4.vhdx"));
        Assert.NotNull(cache.Get(@"C:\recent\ext4.vhdx"));
    }

    [Fact]
    public void Falls_back_to_the_last_reading_when_wsl_cant_be_asked()
    {
        var cache = new UsageCache();
        var ubuntu = Machine.Scan().PrimaryDistro!;
        cache.Set(ubuntu.Path, new UsageReading(30L << 30, ubuntu.SizeBytes, Now.AddHours(-1)));
        var wsl = new FakeWslCommands { Unreachable = true };

        var measured = Measure(wsl, cache);

        Assert.Empty(wsl.Calls);
        Assert.Equal(30L << 30, measured.PrimaryDistro!.UsedBytes);
    }

    [Fact]
    public void Scanner_says_where_to_measure_each_disk()
    {
        var mounts = Machine.Scan().Files.ToDictionary(f => f.Name, f => f.UsageMount);

        Assert.Equal(new WslMount("docker-desktop", "/mnt/docker-desktop-disk"), mounts["Docker data"]);
        Assert.Equal(new WslMount("Ubuntu", "/"), mounts["Ubuntu"]);
    }
}
