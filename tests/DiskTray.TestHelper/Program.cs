using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskTray.TestHelper;

/// <summary>
/// <c>relay &lt;test pipe&gt; &lt;compactor pipe&gt;</c>: connects to the test's pipe, opens
/// the Compactor's as the app does, says "ready", then passes what each end sends to the
/// other until either closes, and closes the other.
/// <c>linger &lt;seconds&gt;</c>: waits that long, holding on to whatever output it was given.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        switch (args)
        {
            case ["relay", string test, string compactor]:
                await Relay(test, compactor);
                return 0;
            case ["linger", string seconds] when int.TryParse(seconds, out int wait):
                await Task.Delay(TimeSpan.FromSeconds(wait));
                return 0;
            default:
                return 64;
        }
    }

    private static async Task Relay(string testPipe, string compactorPipe)
    {
        using var test = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await test.ConnectAsync(30_000);
        using var compactor = NamedPipeServerStreamAcl.Create(
            compactorPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, AsTheAppDoes());
        Console.WriteLine("ready");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await compactor.WaitForConnectionAsync(timeout.Token);
        await Task.WhenAny(Pass(test, compactor), Pass(compactor, test));
    }

    /// <summary>Who may connect, as the app has it (CompactorLauncher): owned by this account, which the Compactor checks.</summary>
    private static PipeSecurity AsTheAppDoes()
    {
        using var me = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(me.User!);
        security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private static async Task Pass(Stream from, Stream to)
    {
        try
        {
            await from.CopyToAsync(to);
        }
        catch (IOException)
        {
            // One end has gone, which ends this as its closing would.
        }
    }
}
