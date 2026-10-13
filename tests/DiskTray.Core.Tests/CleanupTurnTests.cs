using DiskTray.Core.Cleanup;

namespace DiskTray.Core.Tests;

public class CleanupTurnTests
{
    [Fact]
    public void Lets_one_clean_up_go_at_a_time()
    {
        var turn = new CleanupTurn();

        Assert.True(turn.TryTake());
        Assert.False(turn.TryTake());
        Assert.True(turn.Taken);
    }

    [Fact]
    public async Task Gives_the_turn_back_after_refreshing_when_the_compactor_has_ended()
    {
        var turn = new CleanupTurn();
        turn.TryTake();
        bool waited = false;

        await turn.EndAsync(false, () => Task.FromResult(waited = true), () => Task.CompletedTask);

        Assert.False(waited);
        Assert.False(turn.Taken);
    }

    [Fact]
    public async Task Keeps_the_turn_while_it_waits_for_a_compactor_that_may_still_be_running()
    {
        var turn = new CleanupTurn();
        turn.TryTake();
        var compactorEnds = new TaskCompletionSource();
        var order = new List<string>();

        var ending = turn.EndAsync(
            true,
            () =>
            {
                order.Add("wait");
                return compactorEnds.Task;
            },
            () =>
            {
                order.Add("refresh");
                return Task.CompletedTask;
            });

        // The wait starts first, so the refresh already counts it; and nothing can start meanwhile.
        Assert.Equal(["wait", "refresh"], order);
        Assert.True(turn.Taken);
        Assert.False(turn.TryTake());

        compactorEnds.SetResult();
        await ending;

        Assert.False(turn.Taken);
    }

    [Fact]
    public async Task Gives_the_turn_back_even_when_the_refresh_or_the_wait_fails()
    {
        var turn = new CleanupTurn();
        turn.TryTake();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            turn.EndAsync(true, () => Task.FromException(new InvalidOperationException()), () => Task.CompletedTask));
        Assert.False(turn.Taken);

        turn.TryTake();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            turn.EndAsync(true, () => Task.CompletedTask, () => Task.FromException(new InvalidOperationException())));
        Assert.False(turn.Taken);
    }
}
