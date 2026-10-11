using System.Diagnostics;
using System.IO;
using System.Text;

namespace DiskTray.App;

/// <summary>Runs a console program with no window and a timeout, and collects what it prints.</summary>
internal static class HiddenProcess
{
    /// <summary>The output, or null with why it failed (a non-zero exit, a timeout, or it didn't start).</summary>
    public static (string? Output, string? Failure) Run(
        string exe,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Windows tools misbehave with a \\wsl.localhost working folder.
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} didn't start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout) || !Task.WaitAll([output, error], timeout))
            {
                process.Kill(entireProcessTree: true);
                return (null, $"timed out after {timeout.TotalSeconds:0} s");
            }

            return process.ExitCode == 0
                ? (output.Result, null)
                : (null, $"exited with {process.ExitCode}: {(error.Result + output.Result).Trim()}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, e.Message);
        }
    }
}
