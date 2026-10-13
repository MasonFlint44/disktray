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
}
