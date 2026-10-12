using System.Text.Json;
using DiskTray.Core.Usage;

namespace DiskTray.Core.Cleanup;

/// <summary>
/// What cleanup-running.json says: a clean up has stopped Docker Desktop or shut down WSL and
/// hasn't finished, so if DiskTray exits before the end (Exit, an upgrade, a crash) its next
/// start can finish up.
/// </summary>
/// <param name="Compacting">The Compactor had been told to go, so it carries on without the app.</param>
public sealed record CleanupMarker(bool StartDockerAfter, DateTimeOffset StoppedAt, bool Compacting = false)
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

/// <summary>
/// Finishes a clean up the last run of DiskTray left halfway: waits for the Compactor to end,
/// then starts Docker Desktop again if the run was going to and it isn't running.
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

    /// <summary>What to tell the user, or null if there was nothing to finish.</summary>
    public async Task<string?> FinishAsync(CleanupMarker? marker)
    {
        if (marker is null)
        {
            return null;
        }

        log($"A clean up from {marker.StoppedAt:g} didn't finish{(marker.Compacting ? "; it was compacting" : "")}");
        // Compacting may still be going; starting Docker would only fail until it's done.
        while (compactorRunning())
        {
            await delay(CheckInterval);
        }

        string what = marker.Compacting
            ? "DiskTray closed during a clean up. Compacting carried on without it"
            : "DiskTray closed during a clean up, before compacting started, so nothing was compacted";
        if (!marker.StartDockerAfter || !docker.Installed)
        {
            return what + ".";
        }

        string? status = await Task.Run(() => docker.Run(DockerOutput.DesktopStatusCommand, TimeSpan.FromSeconds(20)));
        if (DockerOutput.DesktopRunning(true, status, await Task.Run(wsl.RunningDistros)) != false)
        {
            // Already running again, or it can't be told: either way, not started twice.
            return what + ".";
        }

        log("Starting Docker Desktop, as the unfinished clean up would have");
        bool started = await Task.Run(() => docker.Run(DockerOutput.DesktopStartCommand, CleanupRun.DesktopTimeout) is not null);
        return started
            ? what + ". DiskTray has started Docker Desktop again."
            : what + ", and DiskTray couldn't start Docker Desktop again. Start it yourself.";
    }
}
