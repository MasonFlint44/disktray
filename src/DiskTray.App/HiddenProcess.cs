using System.Diagnostics;
using System.IO;
using System.Text;

namespace DiskTray.App;

/// <summary>Runs a console program with no window and a timeout, and collects what it prints.</summary>
internal static class HiddenProcess
{
    /// <summary>The output, or null with why it failed (a non-zero exit, a timeout, or it didn't start).</summary>
    /// <param name="killTree">
    /// On a timeout, also end what it started. False for a program that starts something meant
    /// to outlive it (<c>docker desktop start</c> starting Docker Desktop).
    /// </param>
    public static (string? Output, string? Failure) Run(
        string exe,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        bool killTree = true)
    {
        var result = RunToEnd(exe, args, timeout, environment, killTree);
        return (result.ExitCode == 0 ? result.Output : null, result.Failure);
    }

    /// <summary>
    /// Like <see cref="Run"/>, but also gives the exit code and the output of a non-zero
    /// exit. ExitCode is null when it timed out or didn't start.
    /// </summary>
    public static (string? Output, string? Failure, int? ExitCode) RunToEnd(
        string exe,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        bool killTree = true)
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
            // One deadline for exiting and for the output, so the timeout is never doubled.
            var clock = Stopwatch.StartNew();
            if (!process.WaitForExit(timeout) || !Task.WaitAll([output, error], Remaining(timeout, clock)))
            {
                Kill(process, killTree);
                return (null, $"timed out after {timeout.TotalSeconds:0} s", null);
            }

            return process.ExitCode == 0
                ? (output.Result, null, 0)
                : (error.Result + output.Result, $"exited with {process.ExitCode}: {(error.Result + output.Result).Trim()}", process.ExitCode);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, e.Message, null);
        }
    }

    /// <summary>
    /// Ends the process, and whatever it started when <paramref name="tree"/>. One that can't
    /// be ended (it's gone already, or a child won't go) still counts as timed out, rather
    /// than as a failure to run it.
    /// </summary>
    private static void Kill(Process process, bool tree)
    {
        try
        {
            process.Kill(entireProcessTree: tree);
        }
        catch (Exception e) when (e is AggregateException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            Log.Error($"Couldn't end {process.StartInfo.FileName} after it timed out", e);
        }
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch clock) =>
        timeout > clock.Elapsed ? timeout - clock.Elapsed : TimeSpan.Zero;
}
