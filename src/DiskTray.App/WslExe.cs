using System.IO;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>Runs <c>wsl.exe</c> hidden, with a timeout.</summary>
internal sealed class WslExe : IWslCommands, IWslControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(1);

    // Otherwise wsl.exe writes its own messages as UTF-16.
    private static readonly Dictionary<string, string> Utf8 = new() { ["WSL_UTF8"] = "1" };
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

    private string? Run(IReadOnlyList<string> args, bool logFailure)
    {
        string commandLine = string.Join(' ', args);
        var (result, failure) = HiddenProcess.Run(ExePath, args, Timeout, Utf8);

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
