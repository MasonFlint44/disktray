using System.IO;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Compact;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>Runs <c>wsl.exe</c> hidden, with a timeout.</summary>
internal sealed class WslExe : IWslControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(1);

    // Otherwise wsl.exe writes its own messages as UTF-16.
    private static readonly Dictionary<string, string> Utf8 = new() { ["WSL_UTF8"] = "1" };
    private static readonly string ExePath = Path.Combine(Environment.SystemDirectory, "wsl.exe");

    // The last failure logged for each command, so one that keeps failing logs once.
    private readonly Dictionary<string, string> _lastFailure = [];

    public IReadOnlySet<string>? RunningDistros()
    {
        if (!File.Exists(ExePath))
        {
            // No WSL, so nothing runs.
            return new HashSet<string>();
        }

        var (output, failure, exitCode) = HiddenProcess.RunToEnd(ExePath, ["--list", "--running", "--quiet"], Timeout, Utf8);
        return WslOutput.ParseRunningDistros(output, exitCode) switch
        {
            { } running => running,
            null => LogUnknown(failure),
        };

        IReadOnlySet<string>? LogUnknown(string? why)
        {
            Log.Info($"Couldn't tell which distros are running: wsl --list --running {why}");
            return null;
        }
    }

    /// <summary>
    /// Tries opening the disk file for itself alone, then closes it. A running distro's
    /// disk is held open by WSL's VM, so that fails with a sharing violation.
    /// </summary>
    public bool? DiskInUse(string path)
    {
        bool? inUse = DiskOpenCheck.InUse(path, out string? problem);
        if (inUse is null)
        {
            Log.Info($"Couldn't tell whether {path} is in use: {problem}");
        }

        return inUse;
    }

    public string? Run(string distro, IReadOnlyList<string> command) =>
        Run(["--distribution", distro, "--exec", .. command]);

    /// <summary>Stops every distro, Docker Desktop's included. Only ever run for a clean up the user started.</summary>
    public bool Shutdown()
    {
        var (_, failure) = HiddenProcess.Run(ExePath, ["--shutdown"], ShutdownTimeout, Utf8);
        if (failure is not null)
        {
            Log.Info($"wsl --shutdown {failure}");
        }

        return failure is null;
    }

    /// <summary>Runs wsl.exe, logging a failure once for each command until it works again.</summary>
    private string? Run(IReadOnlyList<string> args)
    {
        string commandLine = string.Join(' ', args);
        var (result, failure) = HiddenProcess.Run(ExePath, args, Timeout, Utf8);

        if (failure is null)
        {
            _lastFailure.Remove(commandLine);
        }
        else if (_lastFailure.GetValueOrDefault(commandLine) != failure)
        {
            _lastFailure[commandLine] = failure;
            Log.Info($"wsl {commandLine} {failure}");
        }

        return result;
    }
}
