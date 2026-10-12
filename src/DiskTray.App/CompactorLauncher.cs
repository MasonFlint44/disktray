using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Compact;

namespace DiskTray.App;

/// <summary>
/// Starts DiskTray.Compactor.exe (next to DiskTray.exe) with admin rights and talks to it over
/// a fresh named pipe: it sends status and log lines, this sends "go" or "cancel".
/// </summary>
/// <param name="owner">The window the admin prompt belongs to, so it comes up in front of it.</param>
internal sealed class CompactorLauncher(nint owner = 0) : ICompactorLauncher, IDisposable
{
    private const int Cancelled = 1223;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Process? _process;
    private CompactStatus? _status;
    private volatile bool _hungUp;
    private volatile bool _connected;
    private volatile bool _listening;

    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "DiskTray.Compactor.exe");

    /// <summary>
    /// Once it has connected, only the pipe closing counts, so a status it sent just before
    /// exiting has been read by then. Before that, the process ending does.
    /// </summary>
    public bool HasExited => _hungUp || !_listening || (_process is not null && !_connected && HasProcessExited(_process));

    public string? Start(IReadOnlyList<string> disks, bool dryRun)
    {
        if (!File.Exists(ExePath))
        {
            Log.Info($"Can't compact: {ExePath} is missing");
            return "DiskTray.Compactor.exe is missing";
        }

        // Checked here too, so a bad path gets a reason rather than a Compactor that quits.
        if (disks.FirstOrDefault(d => !CompactorArgs.IsVhdx(d)) is { } bad)
        {
            Log.Info($"Can't compact {bad}: not a full local .vhdx path");
            return $"{bad} isn't a disk file DiskTray can compact";
        }

        // Before the admin prompt, so a Compactor something else could have swapped never gets it.
        if (ProtectedFolder.Problem(ExePath) is { } unprotected)
        {
            Log.Info($"Can't compact: {unprotected}");
            return unprotected;
        }

        var args = new CompactorArgs(CompactorArgs.NewPipeName(), disks, dryRun);
        OpenPipe(args.Pipe);
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            // The only way to hand the prompt an owner window; Windows shows its own message
            // if starting fails for some other reason than the prompt being turned down.
            ErrorDialog = owner != 0,
            ErrorDialogParentHandle = owner,
            // Disk paths were checked when parsed, and can't hold quotes.
            Arguments = string.Join(' ', args.ToArgs().Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a)),
            WorkingDirectory = Environment.SystemDirectory,
        };

        try
        {
            _process = Process.Start(start);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            Log.Info("Admin permission to compact wasn't given");
            Close();
            return "admin permission wasn't given";
        }
        catch (Win32Exception e)
        {
            Log.Error("Couldn't start the Compactor", e);
            Close();
            return "the Compactor didn't start";
        }

        if (_process is null)
        {
            Close();
            return "the Compactor didn't start";
        }

        Log.Info($"Started the Compactor ({_process.Id}){(dryRun ? " for a dry run" : "")}: {string.Join(", ", disks)}");
        Listen(_process.Id);
        return null;
    }

    /// <summary>
    /// Creates the pipe. Before the Compactor starts, and only once, so nothing else can be on
    /// this name; and only this user and admins (the Compactor may run as a separate admin
    /// account) can connect, never over the network.
    /// </summary>
    internal void OpenPipe(string name) =>
        _pipe = NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, PipeAccess());

    /// <summary>Who may connect to the pipe, as Windows has it.</summary>
    internal PipeSecurity? PipeSecurity() => _pipe?.GetAccessControl();

    /// <summary>Waits for the process <paramref name="clientId"/> to connect to the pipe, then reads what it sends.</summary>
    internal void Listen(int clientId)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("The pipe isn't open.");
        _listening = true;
        _ = Task.Run(() => ListenAsync(pipe, clientId));
    }

    public CompactStatus? ReadStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void Signal(string message)
    {
        lock (_gate)
        {
            try
            {
                if (_writer is null)
                {
                    Log.Info($"Couldn't tell the Compactor {message}: it isn't connected");
                    return;
                }

                _writer.WriteLine(message);
                Log.Info($"Told the Compactor: {message}");
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Log.Error($"Couldn't tell the Compactor {message}", e);
            }
        }
    }

    public void Dispose() => Close();

    private async Task ListenAsync(NamedPipeServerStream pipe, int clientId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token);

            // Only the process just started may be on the other end.
            if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != clientId)
            {
                Log.Info($"Something other than the Compactor ({client}) connected to its pipe; hanging up");
                _hungUp = true;
                pipe.Disconnect();
                return;
            }

            lock (_gate)
            {
                _writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
            }

            _connected = true;

            using var reader = new StreamReader(pipe);
            while (await reader.ReadLineAsync() is { } line)
            {
                var (status, log) = CompactorMessages.Parse(line);
                if (status is not null)
                {
                    lock (_gate)
                    {
                        _status = status;
                    }
                }
                else if (log is not null)
                {
                    Log.Info($"Compactor: {log}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("The Compactor didn't connect");
        }
        catch (IOException e)
        {
            Log.Error("Lost the pipe to the Compactor", e);
        }
        catch (ObjectDisposedException)
        {
            // Closed from this side, when the run gave up on it.
            Log.Info("Closed the pipe to the Compactor");
        }
        finally
        {
            _hungUp = true;
        }
    }

    private static PipeSecurity PipeAccess()
    {
        using var me = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // ReadWrite alone lacks what a client opening for writing asks for (FILE_APPEND_DATA,
        // which is CreateNewInstance on a pipe). The pipe allows one instance, which is this one.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private void Close()
    {
        lock (_gate)
        {
            _writer = null;
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    // A process started elevated may not let this one ask; then the pipe hanging up says it ended.
    private static bool HasProcessExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
