using System.IO;
using System.IO.Pipes;
using DiskTray.Core.Compact;

namespace DiskTray.Compactor;

/// <summary>
/// The Compactor's side of the pipe to the app, plus the file checks and the compact itself.
/// It writes no files, so nothing it does with admin rights lands in a folder other
/// programs could have tampered with.
/// </summary>
internal sealed class PipeHost : ICompactorHost, IDisposable
{
    private const int SharingViolation = unchecked((int)0x80070020);

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private volatile int _signal = (int)AppSignal.None;

    public PipeHost(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
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
                if (signal != AppSignal.None)
                {
                    _signal = (int)signal;
                }

                if (signal == AppSignal.Gone)
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "Pipe reader" };
        thread.Start();
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

    public bool InUse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException e) when (e.HResult == SharingViolation)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not a sharing problem; the compact will say what's wrong.
            return false;
        }
    }

    public string? CompactDisk(string path, Action<int> progress)
    {
        using var hold = PathHold.Take(path, out string? problem);
        return hold is null ? $"Didn't compact {Path.GetFileName(path)}: {problem}." : VirtualDisk.Compact(path, progress);
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
