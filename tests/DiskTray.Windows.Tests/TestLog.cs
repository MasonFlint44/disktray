using System.IO;
using System.Runtime.CompilerServices;
using DiskTray.App;

namespace DiskTray.Windows.Tests;

/// <summary>Points DiskTray's log at a temp file before any test runs, so they never write to the real one.</summary>
internal static class TestLog
{
    [ModuleInitializer]
    internal static void UseTempFile() =>
        Log.FilePath = Path.Combine(Path.GetTempPath(), "DiskTray.Tests-logs", $"disktray-{Environment.ProcessId}.log");

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
