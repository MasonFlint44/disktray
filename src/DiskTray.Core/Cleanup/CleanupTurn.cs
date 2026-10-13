namespace DiskTray.Core.Cleanup;

/// <summary>
/// Lets one clean up go at a time. A run's turn lasts until its Compactor has ended: when a
/// run returns while its Compactor may still be compacting (it stopped answering, or the run
/// ended with an error), the wait for it is part of the same turn, so no other run can shut
/// WSL down or start Docker Desktop meanwhile.
/// </summary>
public sealed class CleanupTurn
{
    /// <summary>Whether a clean up, or the wait for an earlier one's Compactor, is going.</summary>
    public bool Taken { get; private set; }

    /// <summary>Takes the turn, or says no while it's taken.</summary>
    public bool TryTake()
    {
        if (Taken)
        {
            return false;
        }

        Taken = true;
        return true;
    }

    /// <summary>
    /// Ends the turn: refreshes, and first, when <paramref name="waitForCompactor"/>, starts
    /// waiting for the Compactor, so the refresh already sees that wait (and keeps away from
    /// WSL). The turn is given back only once both are done, however they end.
    /// </summary>
    public async Task EndAsync(bool waitForCompactor, Func<Task> waitForEarlierRun, Func<Task> refresh)
    {
        try
        {
            var waiting = waitForCompactor ? waitForEarlierRun() : Task.CompletedTask;
            try
            {
                await refresh();
            }
            finally
            {
                await waiting;
            }
        }
        finally
        {
            Taken = false;
        }
    }
}
