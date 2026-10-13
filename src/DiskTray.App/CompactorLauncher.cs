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
    private bool _startedProcess;
    private CompactStatus? _status;
    private volatile PipeState _state = PipeState.NotListening;

    private enum PipeState
    {
        /// <summary>Nothing has been started, or starting failed.</summary>
        NotListening,

        /// <summary>Waiting for the Compactor to connect; its process ending means it has ended.</summary>
        Listening,

        /// <summary>The Compactor is on the other end; only the pipe closing means it has ended.</summary>
        Connected,

        /// <summary>The pipe has closed, or the Compactor never connected.</summary>
        Ended,
    }

    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "DiskTray.Compactor.exe");

    /// <summary>
    /// Once it has connected, only the pipe closing counts, so a status it sent just before
    /// exiting has been read by then. Before that, the process ending does.
    /// </summary>
    public bool HasExited => _state switch
    {
        PipeState.Listening => _process is not null && HasProcessExited(_process),
        PipeState.Connected => false,
        _ => true,
    };

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

        // A .NET build of the Compactor (a development one) would take settings the user can
        // change, such as a profiler to load, into a process running as admin. The released
        // one is native, which the exe itself shows.
        if (NotNative(ExePath) is { } notNative)
        {
            Log.Info($"Can't compact: {notNative}");
            return notNative;
        }

        // Before the admin prompt, so a Compactor something else could have swapped never gets it.
        if (ProtectedFolder.Problem(ExePath) is { } unprotected)
        {
            Log.Info($"Can't compact: {unprotected}");
            return unprotected;
        }

        var args = new CompactorArgs(CompactorArgs.NewPipeName(), disks, dryRun);
        try
        {
            OpenPipe(args.Pipe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't open the pipe to the Compactor", e);
            Close();
            return "DiskTray couldn't open its pipe to the Compactor";
        }

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
            _startedProcess = true;
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
        Listen(_process);
        return null;
    }

    /// <summary>
    /// Why the Compactor at <paramref name="exe"/> isn't the native build, or null when it is.
    /// Judged by the exe itself, so a .NET build passes neither with its .dll beside it nor
    /// without (one left behind by an upgrade, say), nor as a single file.
    /// </summary>
    internal static string? NotNative(string exe) =>
        NativeExe.IsNativeAot(exe) && !File.Exists(Path.ChangeExtension(exe, ".dll"))
            ? null
            : "this DiskTray.Compactor.exe isn't the native build DiskTray releases, so it isn't safe to run as admin. Install DiskTray from a release to compact";

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

    /// <summary>
    /// Waits for <paramref name="process"/> to connect to the pipe, then reads what it sends.
    /// Until it connects, the process ending counts as the Compactor having ended.
    /// </summary>
    internal void Listen(Process process)
    {
        _process = process;
        Listen(process.Id);
    }

    /// <summary>Waits for the process <paramref name="clientId"/> to connect to the pipe, then reads what it sends.</summary>
    internal void Listen(int clientId)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("The pipe isn't open.");
        _state = PipeState.Listening;
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

    public void Dispose()
    {
        Close();
        // Only the one it started; one handed to Listen is its caller's.
        if (_startedProcess)
        {
            _process?.Dispose();
        }
    }

    private async Task ListenAsync(NamedPipeServerStream pipe, int clientId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            while (true)
            {
                await pipe.WaitForConnectionAsync(timeout.Token);

                // Only the process just started may be on the other end. Something else (only
                // this account can connect) is hung up on, and the pipe kept for the Compactor.
                if (NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) && client == clientId)
                {
                    break;
                }

                Log.Info($"Something other than the Compactor ({client}) connected to its pipe; hanging up");
                pipe.Disconnect();
            }

            lock (_gate)
            {
                _writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
            }

            _state = PipeState.Connected;

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
            _state = PipeState.Ended;
        }
    }

    private static PipeSecurity PipeAccess()
    {
        using var me = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        // Owned by this account, which the Compactor checks is the one on the other end,
        // even when DiskTray runs as admin (whose files are owned by Administrators by default).
        security.SetOwner(me.User!);
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
