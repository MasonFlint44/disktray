using System.Runtime.InteropServices;
using DiskTray.Core.Compact;

// virtdisk.dll, kernel32.dll and advapi32.dll only ever from System32, never a copy beside the exe.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace DiskTray.Compactor;

/// <summary>
/// Started by DiskTray with admin rights: <c>DiskTray.Compactor.exe --pipe &lt;name&gt;
/// [--dry-run] --disk &lt;path.vhdx&gt;…</c>. Connects to the app's pipe, checks each disk,
/// waits for "go" (sent once WSL is shut down), compacts each disk and reports back.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var parsed = CompactorArgs.Parse(args, out _);
        if (parsed is null)
        {
            return 64;
        }

        using var pipe = PipeHost.Connect(parsed.Pipe, TimeSpan.FromSeconds(15));
        if (pipe is null)
        {
            return 65;
        }

        using var user = UserAccess.OfPipeServer(pipe.SafePipeHandle, App, out string? userProblem);
        using var host = new PipeHost(pipe, user, userProblem);
        if (user is null)
        {
            host.Log($"The Compactor {userProblem}, so it won't compact anything");
        }

        // An install or upgrade under way may already have checked for a running Compactor,
        // and be about to replace this one's files. Windows Installer holds this while it
        // installs anything, so a clean up waits for that.
        if (InstallerBusy())
        {
            host.Report(new CompactStatus(
                CompactState.Failed,
                Message: "Nothing was compacted, because Windows is installing something (a DiskTray upgrade, perhaps). Try again once it's done.",
                DryRun: parsed.DryRun));
            return 4;
        }

        try
        {
            return new CompactorRun(parsed, host).Run();
        }
        catch (Exception e)
        {
            host.Log($"Compactor failed: {e}");
            host.Report(new CompactStatus(CompactState.Failed, Message: $"The Compactor failed: {e.Message}", DryRun: parsed.DryRun));
            return 70;
        }
    }

    /// <summary>The only program the Compactor takes requests from: the DiskTray.exe beside it.</summary>
    private static string App => Path.Combine(AppContext.BaseDirectory, "DiskTray.exe");

    /// <summary>Whether Windows Installer is running an install. One whose lock can't be opened (it's there, but not to this) counts as yes.</summary>
    private static bool InstallerBusy()
    {
        try
        {
            if (Mutex.TryOpenExisting(@"Global\_MSIExecute", out var installing))
            {
                installing.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
