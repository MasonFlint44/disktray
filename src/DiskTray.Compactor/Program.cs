using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using DiskTray.Core.Compact;

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

        // Identification only, so whatever is on the other end can't act as this admin process.
        using var pipe = new NamedPipeClientStream(".", parsed.Pipe, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(TimeSpan.FromSeconds(15));
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            return 65;
        }

        using var host = new PipeHost(pipe);
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
