using System.Text.Json;
using DiskTray.Core.Usage;

namespace DiskTray.Core.Cleanup;

/// <summary>
/// What cleanup-running.json says: a clean up has stopped Docker Desktop or shut down WSL and
/// hasn't finished, so if DiskTray exits before the end (Exit, an upgrade, a crash) its next
/// start can finish up.
/// </summary>
/// <param name="Compacting">The Compactor had been told to go, so it carries on without the app.</param>
/// <param name="StoppedAnswering">
/// The Compactor went quiet part way, so the run ended without knowing how compacting went,
/// and DiskTray is waiting for it to end.
/// </param>
/// <param name="Failed">The run ended with an error part way, which DiskTray has already said.</param>
public sealed record CleanupMarker(bool StartDockerAfter, DateTimeOffset StoppedAt, bool Compacting = false, bool StoppedAnswering = false, bool Failed = false)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a marker, or null when there isn't a usable one.</summary>
    public static CleanupMarker? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CleanupMarker>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A notification's title and text.</summary>
public sealed record CleanupNotice(string Title, string Body);

/// <summary>
/// Finishes a clean up that was left halfway, by DiskTray exiting, by a Compactor that
/// stopped answering or by an error: waits for the Compactor to end, then starts Docker
/// Desktop again if the run was going to and it isn't running.
/// </summary>
/// <param name="compactorRunning">Whether a Compactor is running in this Windows session.</param>
public sealed class InterruptedCleanup(
    IDockerCli docker,
    IWslCommands wsl,
    Func<bool> compactorRunning,
    Func<TimeSpan, Task> delay,
    Action<string> log)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for the Compactor. Far longer than compacting takes, so one still
    /// running by then is stuck, and Clean up shouldn't stay locked behind it.
    /// </summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromHours(2);

    /// <summary>What to tell the user, or null if there was nothing to finish.</summary>
    public async Task<CleanupNotice?> FinishAsync(CleanupMarker? marker)
    {
        if (marker is null)
        {
            return null;
        }

        log($"A clean up from {marker.StoppedAt:g} didn't finish{(marker.Failed ? "; it ended with an error" : marker.StoppedAnswering ? "; its Compactor stopped answering" : marker.Compacting ? "; it was compacting" : "")}");
        // Compacting may still be going; starting Docker would only fail until it's done.
        for (var waited = TimeSpan.Zero; compactorRunning(); waited += CheckInterval)
        {
            if (waited >= WaitLimit)
            {
                log($"Stopped waiting for the Compactor after {WaitLimit.TotalHours:0} hours");
                return new CleanupNotice(
                    "Clean up didn't finish",
                    $"The Compactor from a clean up is still running after {WaitLimit.TotalHours:0} hours, so DiskTray stopped waiting for it."
                    + (marker.StartDockerAfter ? " Start Docker Desktop yourself once DiskTray.Compactor has ended (Task Manager › Details)." : ""));
            }

            await delay(CheckInterval);
        }

        var (title, what) = marker switch
        {
            { Failed: true } => ("Clean up didn't finish", "The clean up stopped with an error part way"),
            { StoppedAnswering: true } => ("Clean up ended", "The Compactor that stopped answering has ended. DiskTray can't tell how compacting went, but the flyout shows the disks' sizes now"),
            { Compacting: true } => ("Clean up ended", "DiskTray closed during a clean up. Compacting carried on without it"),
            _ => ("Clean up didn't finish", "DiskTray closed during a clean up, before compacting started, so nothing was compacted"),
        };
        CleanupNotice Notice(string body) => new(title, body);

        if (!marker.StartDockerAfter || !docker.Installed)
        {
            // The error was told already; there's nothing to add.
            return marker.Failed ? null : Notice(what + ".");
        }

        string? status = await Task.Run(() => docker.Run(DockerOutput.DesktopStatusCommand, TimeSpan.FromSeconds(20)));
        if (DockerOutput.DesktopRunning(true, status, await Task.Run(wsl.RunningDistros)) != false)
        {
            // Already running again, or it can't be told: either way, not started twice.
            return marker.Failed ? null : Notice(what + ".");
        }

        log("Starting Docker Desktop, as the unfinished clean up would have");
        bool started = await Task.Run(() => docker.Run(DockerOutput.DesktopStartCommand, CleanupRun.DesktopTimeout) is not null);
        return Notice(started
            ? what + ". DiskTray has started Docker Desktop again."
            : what + ", and DiskTray couldn't start Docker Desktop again. Start it yourself.");
    }
}
