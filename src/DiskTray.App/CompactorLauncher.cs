using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using DiskTray.Core.Cleanup;
using DiskTray.Core.Compact;

namespace DiskTray.App;

/// <summary>
/// Starts DiskTray.Compactor.exe (next to DiskTray.exe) with admin rights and talks to it over
/// a fresh named pipe: it sends status and log lines, this sends "go" or "cancel".
/// </summary>
internal sealed class CompactorLauncher : ICompactorLauncher, IDisposable
{
    private const int Cancelled = 1223;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Process? _process;
    private CompactStatus? _status;
    private volatile bool _hungUp;

    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "DiskTray.Compactor.exe");

    public bool HasExited => _hungUp || _process is null || HasProcessExited(_process);

    public string? Start(IReadOnlyList<string> disks, bool dryRun)
    {
        if (!File.Exists(ExePath))
        {
            Log.Info($"Can't compact: {ExePath} is missing");
            return "DiskTray.Compactor.exe is missing";
        }

        // Before the admin prompt, so a Compactor something else could have swapped never gets it.
        if (ProtectedFolder.Problem(ExePath) is { } unprotected)
        {
            Log.Info($"Can't compact: {unprotected}");
            return unprotected;
        }

        var args = new CompactorArgs(CompactorArgs.NewPipeName(), disks, dryRun);

        // Created before the Compactor starts, and only once, so nothing else can be on this name.
        _pipe = new NamedPipeServerStream(args.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
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
        _ = Task.Run(() => ListenAsync(_pipe, _process));
        return null;
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
                _writer?.WriteLine(message);
                Log.Info($"Told the Compactor: {message}");
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Log.Error($"Couldn't tell the Compactor {message}", e);
            }
        }
    }

    public void Dispose() => Close();

    private async Task ListenAsync(NamedPipeServerStream pipe, Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token);

            // Only the process just started may be on the other end.
            if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != process.Id)
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
        finally
        {
            _hungUp = true;
        }
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
