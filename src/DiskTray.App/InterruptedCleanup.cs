using System.Diagnostics;
using System.Text.Json;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>
/// Remembers, in cleanup-running.json, that a clean up has stopped Docker Desktop or shut
/// down WSL, so that if DiskTray exits before the end (Exit, an upgrade, a crash) the next
/// start can finish up: wait for the Compactor to end, then start Docker Desktop again if
/// the run was going to.
/// </summary>
internal static class InterruptedCleanup
{
    private const string FileName = "cleanup-running.json";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private sealed record Marker(bool StartDockerAfter, DateTimeOffset StoppedAt);

    public static void Save(bool startDockerAfter) =>
        AppDataFile.Write(FileName, JsonSerializer.Serialize(new Marker(startDockerAfter, DateTimeOffset.Now)));

    public static void Clear() => AppDataFile.Delete(FileName);

    /// <summary>
    /// Finishes a clean up the last run of DiskTray left halfway, if there is one. Returns
    /// what to tell the user, or null if there was nothing to finish.
    /// </summary>
    public static async Task<string?> FinishAsync(IDockerCli docker, IWslCommands wsl)
    {
        Marker? marker;
        try
        {
            marker = AppDataFile.Read(FileName) is { } json ? JsonSerializer.Deserialize<Marker>(json) : null;
        }
        catch (JsonException)
        {
            marker = null;
        }

        if (marker is null)
        {
            Clear();
            return null;
        }

        Log.Info($"A clean up from {marker.StoppedAt:g} didn't finish");
        // Compacting may still be going; starting Docker would only fail until it's done.
        while (Process.GetProcessesByName("DiskTray.Compactor").Length > 0)
        {
            await Task.Delay(CheckInterval);
        }

        Clear();
        if (!marker.StartDockerAfter
            || DockerOutput.DesktopRunning(docker.Installed, await Task.Run(() => docker.Run(DockerOutput.DesktopStatusCommand, TimeSpan.FromSeconds(20))), await Task.Run(wsl.RunningDistros)) != false)
        {
            return "DiskTray closed during a clean up. Compacting carried on without it.";
        }

        Log.Info("Starting Docker Desktop, as the unfinished clean up would have");
        bool started = await Task.Run(() => docker.Run(DockerOutput.DesktopStartCommand, CleanupRun.DesktopTimeout) is not null);
        return started
            ? "DiskTray closed during a clean up, so it has started Docker Desktop again now."
            : "DiskTray closed during a clean up, and couldn't start Docker Desktop again. Start it yourself.";
    }
}
