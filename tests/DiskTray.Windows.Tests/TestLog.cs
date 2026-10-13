using System.IO;
using System.Runtime.CompilerServices;
using DiskTray.App;

namespace DiskTray.Windows.Tests;

/// <summary>Points DiskTray's log at a temp file before any test runs, so they never write to the real one.</summary>
internal static class TestLog
{
    [ModuleInitializer]
    internal static void UseTempFile()
    {
        Log.FilePath = Path.Combine(Path.GetTempPath(), "DiskTray.Tests-logs", $"disktray-{Environment.ProcessId}.log");
        // What a failure needed from it is in its message by then.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                File.Delete(Log.FilePath);
                File.Delete(Log.FilePath + ".old");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        };
    }

    /// <summary>A failure message with the log's last lines, which say what a check found.</summary>
    public static string Explain(string? problem)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Log.FilePath);
        }
        catch (IOException)
        {
            lines = [];
        }

        return $"{problem}\nThe log ended:\n{string.Join('\n', lines.TakeLast(8))}";
    }
}
