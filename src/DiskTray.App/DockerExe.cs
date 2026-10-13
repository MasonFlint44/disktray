using System.IO;
using DiskTray.Core.Cleanup;
using Microsoft.Win32;

namespace DiskTray.App;

/// <summary>
/// Runs Docker Desktop's <c>docker.exe</c> hidden. When Docker Desktop isn't running, commands
/// just fail; nothing here starts it except <c>docker desktop start</c> after a clean up.
/// </summary>
internal sealed class DockerExe : IDockerCli
{
    private readonly string? _exePath = FindExe();

    public bool Installed => _exePath is not null;

    public string? Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        if (_exePath is null)
        {
            return null;
        }

        // Docker Desktop, once it's started, may be one of the things docker.exe started.
        bool starts = args.SequenceEqual(DockerOutput.DesktopStartCommand);
        var (output, failure) = HiddenProcess.Run(_exePath, args, timeout, killTree: !starts);
        if (failure is not null)
        {
            Log.Info($"docker {string.Join(' ', args)} {failure}");
        }

        return output;
    }

    /// <summary>docker.exe from Docker Desktop's install folder, as its installer records it.</summary>
    private static string? FindExe()
    {
        string? bin = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Docker Inc.\Docker\1.0");
            bin = key?.GetValue("BinPath") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Couldn't read where Docker Desktop is installed", e);
        }

        bin ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin");
        string exe = Path.Combine(bin, "docker.exe");
        return File.Exists(exe) ? exe : null;
    }
}
