using System.Diagnostics;
using DiskTray.Core.Cleanup;

namespace DiskTray.App;

/// <summary>
/// Keeps the <see cref="CleanupMarker"/> in <c>%LOCALAPPDATA%\DiskTray\cleanup-running.json</c>
/// while a clean up has Docker or WSL stopped.
/// </summary>
internal static class CleanupMarkerFile
{
    private const string Name = "cleanup-running.json";

    public static CleanupMarker? Load() => CleanupMarker.FromJson(AppDataFile.Read(Name));

    public static void Save(CleanupMarker marker) => AppDataFile.Write(Name, marker.ToJson());

    public static void Clear() => AppDataFile.Delete(Name);

    /// <summary>
    /// Whether a Compactor is running in this Windows session. Another user's (or another
    /// session's) is from their own clean up, not this one.
    /// </summary>
    public static bool CompactorRunning()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var found = Process.GetProcessesByName("DiskTray.Compactor");
        try
        {
            return found.Any(p => p.SessionId == session);
        }
        finally
        {
            foreach (var process in found)
            {
                process.Dispose();
            }
        }
    }
}
