using DiskTray.Core.Cleanup;

namespace DiskTray.Core.Tests;

public class InterruptedCleanupTests
{
    private static readonly DateTimeOffset StoppedAt = new(2026, 10, 11, 14, 0, 0, TimeSpan.Zero);

    private readonly FakeDocker _docker = new() { DesktopRunning = false };
    private readonly FakeWsl _wsl = new([]);
    private int _compactorChecksLeft;
    private int _delays;

    private Task<string?> Finish(CleanupMarker? marker) =>
        new InterruptedCleanup(
            _docker,
            _wsl,
            () => _compactorChecksLeft-- > 0,
            _ =>
            {
                _delays++;
                return Task.CompletedTask;
            },
            _ => { }).FinishAsync(marker);

    [Fact]
    public async Task Does_nothing_when_no_clean_up_was_left_halfway()
    {
        Assert.Null(await Finish(null));
        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public async Task Waits_for_the_compactor_then_starts_docker_again()
    {
        _compactorChecksLeft = 3;

        string? said = await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true));

        Assert.Equal(3, _delays);
        Assert.Equal(["desktop start --detach"], _docker.Calls);
        Assert.Equal("DiskTray closed during a clean up. Compacting carried on without it. DiskTray has started Docker Desktop again.", said);
    }

    [Fact]
    public async Task Says_nothing_was_compacted_when_it_closed_before_compacting_started()
    {
        string? said = await Finish(new CleanupMarker(StartDockerAfter: false, StoppedAt));

        Assert.Equal("DiskTray closed during a clean up, before compacting started, so nothing was compacted.", said);
        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public async Task Leaves_docker_alone_when_its_running_already_or_cant_be_told()
    {
        var marker = new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true);
        _docker.DesktopRunning = true;
        await Finish(marker);

        _docker.StatusFails = true;
        _wsl.Unreachable = true;
        await Finish(marker);

        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public async Task Doesnt_try_to_start_docker_when_it_isnt_installed()
    {
        _docker.Installed = false;

        await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true));

        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public async Task Says_so_when_docker_wont_start()
    {
        _docker.Failing.Add("desktop start");

        string? said = await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true));

        Assert.EndsWith("and DiskTray couldn't start Docker Desktop again. Start it yourself.", said);
    }

    [Fact]
    public void Round_trips_the_marker_through_json()
    {
        var marker = new CleanupMarker(true, StoppedAt, Compacting: true);

        Assert.Equal(marker, CleanupMarker.FromJson(marker.ToJson()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void Reads_an_unusable_marker_as_none(string? json) => Assert.Null(CleanupMarker.FromJson(json));

    [Fact]
    public void Reads_a_marker_from_before_compacting_was_recorded_as_not_compacting() =>
        Assert.False(CleanupMarker.FromJson("""{"startDockerAfter":true,"stoppedAt":"2026-10-11T14:00:00+00:00"}""")!.Compacting);
}
