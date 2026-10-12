using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using DiskTray.Core.Compact;

namespace DiskTray.Compactor;

/// <summary>
/// The Compactor's side of the pipe to the app, plus the file checks and the compact itself.
/// It writes no files, so nothing it does with admin rights lands in a folder other
/// programs could have tampered with.
/// </summary>
internal sealed class PipeHost : ICompactorHost, IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly UserAccess? _user;
    private readonly string? _userProblem;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private volatile int _signal = (int)AppSignal.None;

    /// <param name="user">The account that asked, or null when it couldn't be told (then nothing is compacted).</param>
    /// <param name="userProblem">Why it couldn't be told.</param>
    public PipeHost(NamedPipeClientStream pipe, UserAccess? user, string? userProblem)
    {
        _pipe = pipe;
        _user = user;
        _userProblem = userProblem;
        _writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(pipe);
        var thread = new Thread(() =>
        {
            while (true)
            {
                string? line;
                try
                {
                    line = reader.ReadLine();
                }
                catch (IOException)
                {
                    line = null;
                }

                var signal = CompactorMessages.ParseSignal(line);
                _signal = (int)CompactorMessages.Latch((AppSignal)_signal, signal);
                if (signal == AppSignal.Gone)
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "Pipe reader" };
        thread.Start();
    }

    /// <summary>
    /// Connects to the app's pipe. Opened for overlapped I/O: on a handle opened without it
    /// Windows runs one call at a time, so the reader thread waiting for a line would hold up
    /// every status this sends.
    /// </summary>
    /// <returns>The pipe, or null when it can't be reached.</returns>
    public static NamedPipeClientStream? Connect(string name, TimeSpan timeout)
    {
        // Identification only, so whatever is on the other end can't act as this admin process.
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeout);
            return pipe;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return null;
        }
    }

    public AppSignal Poll() => (AppSignal)_signal;

    public long? FileSize(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Can't be told counts as free: the compact will say what's wrong.
    public bool InUse(string path) => DiskOpenCheck.InUse(path, out _) == true;

    public string? CompactDisk(string path, Action<int> progress)
    {
        string name = Path.GetFileName(path);
        using var hold = PathHold.Take(path, out string? problem);
        if (hold is null)
        {
            return $"Didn't compact {name}: {problem}.";
        }

        // Checked while held, so it's the file that gets compacted.
        return _user?.CanWrite(path) switch
        {
            true => VirtualDisk.Compact(path, progress),
            false => $"Didn't compact {name}: the account that asked can't change it, so it isn't DiskTray's to compact.",
            null => $"Didn't compact {name}: the Compactor {_userProblem ?? "couldn't check that the account that asked can change it"}.",
        };
    }

    public void Report(CompactStatus status) => Send(CompactorMessages.Status(status));

    public void Log(string message) => Send(CompactorMessages.Log(message));

    public void Wait(TimeSpan delay) => Thread.Sleep(delay);

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    private void Send(string line)
    {
        lock (_gate)
        {
            try
            {
                if (_pipe.IsConnected)
                {
                    _writer.WriteLine(line);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The app has gone; keep compacting, since stopping part way gains nothing.
            }
        }
    }
}
