using System.Diagnostics;
using System.IO;
using System.Text;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>Runs <c>wsl.exe</c> hidden, with a timeout.</summary>
internal sealed class WslExe : IWslCommands
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string ExePath = Path.Combine(Environment.SystemDirectory, "wsl.exe");

    // The last failure logged for each command, so one that keeps failing logs once.
    private readonly Dictionary<string, string> _lastFailure = [];

    public IReadOnlySet<string> RunningDistros()
    {
        // Fails when nothing is running, so a failure just means none.
        if (!File.Exists(ExePath) || Run(["--list", "--running", "--quiet"], logFailure: false) is not { } output)
        {
            return new HashSet<string>();
        }

        return WslOutput.ParseDistroNames(output);
    }

    public string? Run(string distro, IReadOnlyList<string> command) =>
        Run(["--distribution", distro, "--exec", .. command], logFailure: true);

    private string? Run(IReadOnlyList<string> args, bool logFailure)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Otherwise wsl.exe writes its own messages as UTF-16.
        start.Environment["WSL_UTF8"] = "1";

        string commandLine = string.Join(' ', args);
        string? failure;
        string? result = null;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("wsl.exe didn't start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout) || !Task.WaitAll([output, error], Timeout))
            {
                process.Kill(entireProcessTree: true);
                failure = $"timed out after {Timeout.TotalSeconds:0} s";
            }
            else if (process.ExitCode != 0)
            {
                failure = $"exited with {process.ExitCode}: {(error.Result + output.Result).Trim()}";
            }
            else
            {
                failure = null;
                result = output.Result;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            failure = e.Message;
        }

        if (logFailure)
        {
            if (failure is null)
            {
                _lastFailure.Remove(commandLine);
            }
            else if (_lastFailure.GetValueOrDefault(commandLine) != failure)
            {
                _lastFailure[commandLine] = failure;
                Log.Info($"wsl {commandLine} {failure}");
            }
        }

        return result;
    }
}
