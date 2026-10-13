using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskTray.App;
using DiskTray.Compactor;
using DiskTray.Core.Compact;

namespace DiskTray.Windows.Tests;

/// <summary>
/// The app's end of the pipe (<see cref="CompactorLauncher"/>) talking to the Compactor's
/// (<see cref="PipeHost"/>), both in this process, with the compacting itself faked.
/// </summary>
public class PipeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The Compactor's steps over the real pipe, without touching any disk.</summary>
    private sealed class PipeOnlyHost(PipeHost pipe) : ICompactorHost
    {
        public AppSignal Poll() => pipe.Poll();

        public string? Check(string path) => null;

        public DiskHold Hold(string path, bool dryRun) => new(new NoDisk());

        public void Report(CompactStatus status) => pipe.Report(status);

        public void Log(string message) => pipe.Log(message);

        public void Wait(TimeSpan delay) => Thread.Sleep(50);

        private sealed class NoDisk : IHeldDisk
        {
            public long? Size => 1000;

            public string? Compact(Action<int> progress)
            {
                for (int percent = 0; percent <= 100; percent += 25)
                {
                    progress(percent);
                }

                return null;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Opens the app's end, listening for <paramref name="clientId"/>, and connects the Compactor's end.</summary>
    private static (CompactorLauncher Launcher, PipeHost Host, string Name) Connect(int? clientId = null)
    {
        var launcher = new CompactorLauncher();
        string name = CompactorArgs.NewPipeName();
        launcher.OpenPipe(name);
        launcher.Listen(clientId ?? Environment.ProcessId);
        var pipe = PipeHost.Connect(name, Timeout) ?? throw new InvalidOperationException("Couldn't connect to the pipe.");
        return (launcher, new PipeHost(pipe, null, "isn't asked in this test"), name);
    }

    private static Task<int> RunCompactor(PipeHost host, string name) =>
        Task.Run(() => new CompactorRun(new CompactorArgs(name, [@"C:\disks\one.vhdx", @"C:\disks\two.vhdx"]), new PipeOnlyHost(host)).Run());

    [Fact]
    public async Task Carries_every_status_both_ways_until_the_compactor_is_done()
    {
        var (launcher, host, name) = Connect();
        using (launcher)
        using (host)
        {
            var run = RunCompactor(host, name);

            Statuses.WaitFor(launcher, s => s.State == CompactState.Waiting, Timeout);
            launcher.Signal(CompactorMessages.Go);
            var final = Statuses.WaitFor(launcher, s => s.Finished, Timeout);

            Assert.Equal(CompactState.Done, final.State);
            Assert.Equal(2, final.Results!.Count);
            Assert.Equal(0, await run.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Cancels_when_the_app_says_so()
    {
        var (launcher, host, name) = Connect();
        using (launcher)
        using (host)
        {
            var run = RunCompactor(host, name);

            Statuses.WaitFor(launcher, s => s.State == CompactState.Waiting, Timeout);
            launcher.Signal(CompactorMessages.Cancel);

            Assert.Equal(CompactState.Cancelled, Statuses.WaitFor(launcher, s => s.Finished, Timeout).State);
            Assert.Equal(2, await run.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Stops_waiting_when_the_app_goes_away()
    {
        var (launcher, host, name) = Connect();
        using (host)
        {
            var run = RunCompactor(host, name);

            Statuses.WaitFor(launcher, s => s.State == CompactState.Waiting, Timeout);
            launcher.Dispose();

            Assert.Equal(2, await run.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Stops_waiting_when_its_own_end_of_the_pipe_is_closed_under_the_reader()
    {
        var (launcher, host, name) = Connect();
        using (launcher)
        {
            var run = RunCompactor(host, name);
            Statuses.WaitFor(launcher, s => s.State == CompactState.Waiting, Timeout);

            // Closing it under the reader thread's waiting read, which shows up as a disposed
            // stream rather than an IOException; the reader takes it as the app gone, not a crash.
            host.Dispose();

            Assert.Equal(2, await run.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.Equal(AppSignal.Gone, host.Poll());
        }
    }

    [Fact]
    public void Hangs_up_on_anything_but_the_process_it_started()
    {
        // Listening for a process that isn't this one, which connects anyway.
        var (launcher, host, _) = Connect(clientId: 4);
        using (launcher)
        using (host)
        {
            host.Report(new CompactStatus(CompactState.Waiting));

            var until = DateTime.UtcNow + Timeout;
            while (!launcher.HasExited && DateTime.UtcNow < until)
            {
                Thread.Sleep(50);
            }

            Assert.True(launcher.HasExited);
            Assert.Null(launcher.ReadStatus());
        }
    }

    [Fact]
    public void Counts_a_compactor_that_ends_before_it_connects_as_ended()
    {
        using var launcher = new CompactorLauncher();
        launcher.OpenPipe(CompactorArgs.NewPipeName());
        using var quick = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "exit", "0"]) { CreateNoWindow = true, UseShellExecute = false })!;
        launcher.Listen(quick);

        Assert.True(quick.WaitForExit(Timeout));
        Assert.True(launcher.HasExited);
        Assert.Null(launcher.ReadStatus());
    }

    [Fact]
    public void Lets_only_this_user_and_admins_connect_and_never_over_the_network()
    {
        using var launcher = new CompactorLauncher();
        launcher.OpenPipe(CompactorArgs.NewPipeName());

        var rules = launcher.PipeSecurity()!.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        PipeAccessRule Rule(WellKnownSidType? type, AccessControlType allowOrDeny) => Assert.Single(rules, r =>
            r.IdentityReference.Equals(type is { } known ? new SecurityIdentifier(known, null) : WindowsIdentity.GetCurrent().User) && r.AccessControlType == allowOrDeny);

        Assert.Equal(3, rules.Count);
        Assert.Equal(PipeAccessRights.FullControl, Rule(null, AccessControlType.Allow).PipeAccessRights & PipeAccessRights.FullControl);
        var admins = Rule(WellKnownSidType.BuiltinAdministratorsSid, AccessControlType.Allow).PipeAccessRights;
        Assert.True(admins.HasFlag(PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance), $"Administrators get {admins}");
        Assert.False(admins.HasFlag(PipeAccessRights.ChangePermissions) || admins.HasFlag(PipeAccessRights.TakeOwnership), $"Administrators get {admins}");
        Assert.Equal(PipeAccessRights.FullControl, Rule(WellKnownSidType.NetworkSid, AccessControlType.Deny).PipeAccessRights & PipeAccessRights.FullControl);
    }

    [Fact]
    public void Wont_open_a_pipe_whose_name_is_taken()
    {
        string name = CompactorArgs.NewPipeName();
        using var first = new CompactorLauncher();
        first.OpenPipe(name);
        using var second = new CompactorLauncher();

        var refused = Record.Exception(() => second.OpenPipe(name));

        Assert.True(refused is IOException or UnauthorizedAccessException, $"Opening it again gave {refused?.GetType().Name ?? "no error"}");
    }

    /// <summary>
    /// Why the Compactor opens its end for overlapped I/O: on a handle opened without it,
    /// Windows runs one call at a time, so a write waits behind a read that's waiting for a
    /// line. The Compactor's reader thread always is.
    /// </summary>
    [Theory]
    [InlineData(PipeOptions.None, false)]
    [InlineData(PipeOptions.Asynchronous, true)]
    public async Task A_write_gets_past_a_waiting_read_only_with_overlapped_io(PipeOptions options, bool getsPast)
    {
        string name = "DiskTray-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, options);
        client.Connect(Timeout);
        await connected;

        var reader = new Thread(() =>
        {
            try
            {
                client.ReadByte();
            }
            catch (IOException)
            {
            }
        })
        { IsBackground = true };
        reader.Start();
        Thread.Sleep(500);

        // The app always has a read waiting too. The pipe has no buffer, as the app's has
        // none, so a write only completes once the other end reads it.
        var buffer = new byte[1];
        var serverRead = server.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask();
        var write = Task.Run(() => client.WriteByte(1), TestContext.Current.CancellationToken);
        bool wrote = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken)) == write;

        // Lets the read finish, so the write (and the test) can too.
        var serverWrite = server.WriteAsync(new byte[] { 2 }, TestContext.Current.CancellationToken).AsTask();
        await Task.WhenAll(write, serverRead, serverWrite).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(getsPast, wrote);
        Assert.Equal(1, buffer[0]);
    }
}
