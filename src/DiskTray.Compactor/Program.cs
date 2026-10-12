using System.Runtime.InteropServices;
using DiskTray.Core.Compact;

// virtdisk.dll, kernel32.dll and advapi32.dll only ever from System32, never a copy beside the exe.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace DiskTray.Compactor;

/// <summary>
/// Started by DiskTray with admin rights: <c>DiskTray.Compactor.exe --pipe &lt;name&gt;
/// [--dry-run] --disk &lt;path.vhdx&gt;…</c>. Connects to the app's pipe, waits for "go" (sent
/// once WSL is shut down), compacts each disk and reports back.
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

        using var user = UserAccess.OfPipeServer(pipe.SafePipeHandle, out string? userProblem);
        using var host = new PipeHost(pipe, user, userProblem);
        if (user is null)
        {
            host.Log($"The Compactor {userProblem}, so it won't compact anything");
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
}
