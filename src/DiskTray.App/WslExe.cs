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

    // Shared by every WslExe (refreshes have their own), so a clean up's shutdown can wait
    // for a refresh's df that's already going, and keep any more out until the clean up ends.
    private static readonly object DistroGate = new();
    private static int _distroRuns;
    private static bool _distrosKeptOut;

    // The last failure logged for each command, so one that keeps failing logs once.
    private readonly Dictionary<string, string> _lastFailure = [];

    /// <summary>Lets commands run in distros again, once the clean up that shut WSL down is over.</summary>
    public static void LetDistrosRun()
    {
        lock (DistroGate)
        {
            _distrosKeptOut = false;
        }
    }

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

    /// <summary>Runs the command in the distro, or returns null without running it while a clean up keeps distros quiet.</summary>
    public string? Run(string distro, IReadOnlyList<string> command)
    {
        lock (DistroGate)
        {
            if (_distrosKeptOut)
            {
                return null;
            }

            _distroRuns++;
        }

        try
        {
            return Run(["--distribution", distro, "--exec", .. command]);
        }
        finally
        {
            lock (DistroGate)
            {
                _distroRuns--;
                Monitor.PulseAll(DistroGate);
            }
        }
    }

    /// <summary>
    /// Stops every distro, Docker Desktop's included. Only ever run for a clean up the user
    /// started. A command already running in a distro (a refresh's df) would start it again
    /// straight after, so this waits for those to end, and keeps out more until
    /// <see cref="LetDistrosRun"/>.
    /// </summary>
    public bool Shutdown()
    {
        lock (DistroGate)
        {
            _distrosKeptOut = true;
            var until = DateTime.UtcNow + Timeout + TimeSpan.FromSeconds(5);
            while (_distroRuns > 0 && until - DateTime.UtcNow is { Ticks: > 0 } left)
            {
                Monitor.Wait(DistroGate, left);
            }
        }

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
