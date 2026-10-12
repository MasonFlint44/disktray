namespace DiskTray.Core.Cleanup;

/// <summary>
/// Hands a run's progress to the UI thread, like <see cref="Progress{T}"/>, but drops any
/// report still queued once <see cref="Done"/> has been called there. Otherwise a report
/// made just before a run returns can land after the run has been marked finished, and
/// leave it looking like it's still going.
/// </summary>
/// <param name="post">Queues an action on the UI thread.</param>
/// <param name="onReport">Runs on the UI thread for each report made before <see cref="Done"/>.</param>
public sealed class RunProgress<T>(Action<Action> post, Action<T> onReport) : IProgress<T>
{
    private volatile bool _done;

    public void Report(T value) => post(() =>
    {
        if (!_done)
        {
            onReport(value);
        }
    });

    /// <summary>Called on the UI thread when the run has finished.</summary>
    public void Done() => _done = true;
}
