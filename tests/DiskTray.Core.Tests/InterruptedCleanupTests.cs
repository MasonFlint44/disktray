using DiskTray.Core.Cleanup;

namespace DiskTray.Core.Tests;

public class InterruptedCleanupTests
{
    private static readonly DateTimeOffset StoppedAt = new(2026, 10, 11, 14, 0, 0, TimeSpan.Zero);

    private readonly FakeDocker _docker = new() { DesktopRunning = false };
    private readonly FakeWsl _wsl = new([]);
    private int _compactorChecksLeft;
    private int _delays;

    private Task<CleanupNotice?> Finish(CleanupMarker? marker) =>
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

        var said = await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true));

        Assert.Equal(3, _delays);
        Assert.Equal(["desktop start --detach"], _docker.Calls);
        Assert.Equal(new CleanupNotice("Clean up ended", "DiskTray closed during a clean up. Compacting carried on without it. DiskTray has started Docker Desktop again."), said);
    }

    [Fact]
    public async Task Waits_for_a_compactor_that_stopped_answering_then_starts_docker_again()
    {
        _compactorChecksLeft = 2;

        var said = await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true, StoppedAnswering: true));

        Assert.Equal(2, _delays);
        Assert.Equal(["desktop start --detach"], _docker.Calls);
        Assert.Equal("Clean up ended", said!.Title);
        Assert.StartsWith("The Compactor that stopped answering has ended. DiskTray can't tell how compacting went", said.Body);
    }

    [Fact]
    public async Task Stops_waiting_for_a_compactor_that_never_ends()
    {
        _compactorChecksLeft = int.MaxValue;

        var said = await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true));

        Assert.Equal((int)(InterruptedCleanup.WaitLimit / InterruptedCleanup.CheckInterval), _delays);
        Assert.Empty(_docker.Calls);
        Assert.Equal("Clean up didn't finish", said!.Title);
        Assert.Contains("still running after 2 hours", said.Body);
        Assert.Contains("Start Docker Desktop yourself once DiskTray.Compactor has ended", said.Body);
    }

    [Fact]
    public async Task Says_nothing_was_compacted_when_it_closed_before_compacting_started()
    {
        var said = await Finish(new CleanupMarker(StartDockerAfter: false, StoppedAt));

        Assert.Equal(new CleanupNotice("Clean up didn't finish", "DiskTray closed during a clean up, before compacting started, so nothing was compacted."), said);
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

        var said = await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true));

        Assert.EndsWith("and DiskTray couldn't start Docker Desktop again. Start it yourself.", said!.Body);
    }

    [Fact]
    public async Task After_an_error_starts_docker_again_once_the_compactor_has_ended()
    {
        _compactorChecksLeft = 2;

        var said = await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Compacting: true, Failed: true));

        Assert.Equal(2, _delays);
        Assert.Equal(["desktop start --detach"], _docker.Calls);
        Assert.Equal(new CleanupNotice("Clean up didn't finish", "The clean up stopped with an error part way. DiskTray has started Docker Desktop again."), said);
    }

    [Fact]
    public async Task After_an_error_says_nothing_more_when_there_is_nothing_to_start()
    {
        Assert.Null(await Finish(new CleanupMarker(StartDockerAfter: false, StoppedAt, Failed: true)));
        _docker.DesktopRunning = true;
        Assert.Null(await Finish(new CleanupMarker(StartDockerAfter: true, StoppedAt, Failed: true)));
        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public void Round_trips_the_marker_through_json()
    {
        var marker = new CleanupMarker(true, StoppedAt, Compacting: true, StoppedAnswering: true, Failed: true);

        Assert.Equal(marker, CleanupMarker.FromJson(marker.ToJson()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void Reads_an_unusable_marker_as_none(string? json) => Assert.Null(CleanupMarker.FromJson(json));

    [Fact]
    public void Reads_a_marker_from_before_compacting_was_recorded_as_not_compacting()
    {
        var marker = CleanupMarker.FromJson("""{"startDockerAfter":true,"stoppedAt":"2026-10-11T14:00:00+00:00"}""")!;

        Assert.False(marker.Compacting);
        Assert.False(marker.StoppedAnswering);
    }
}
