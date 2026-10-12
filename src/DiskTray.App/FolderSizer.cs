using System.IO;
using DiskTray.Core.Detection;

namespace DiskTray.App;

/// <summary>
/// Adds up the size of each pinned folder in the background: once when it's pinned, then
/// hourly, since walking a big folder can take a while. Raises <see cref="Measured"/> after
/// each count so the tray can redraw.
/// </summary>
internal sealed class FolderSizer : IFolderSizes, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private static readonly EnumerationOptions Everything = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Junctions and links point at space counted somewhere else.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FolderSize> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    public FolderSizer()
    {
        _timer = new Timer(_ => MeasureAll(), null, Interval, Interval);
    }

    /// <summary>Raised on a worker thread after a folder has been measured.</summary>
    public event EventHandler? Measured;

    public FolderSize? Get(string path)
    {
        lock (_gate)
        {
            return _sizes.GetValueOrDefault(path);
        }
    }

    /// <summary>Sets which folders to keep measuring, and measures new ones straight away.</summary>
    public void Track(IEnumerable<string> paths)
    {
        var folders = paths.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool added;
        lock (_gate)
        {
            added = folders.Except(_folders, StringComparer.OrdinalIgnoreCase).Any(f => !_sizes.ContainsKey(f));
            _folders = folders;
            foreach (string gone in _sizes.Keys.Where(k => !folders.Contains(k)).ToList())
            {
                _sizes.Remove(gone);
            }
        }

        if (added)
        {
            _ = Task.Run(MeasureAll);
        }
    }

    public void Dispose() => _timer.Dispose();

    private void MeasureAll()
    {
        // One count at a time; a timer tick during a long count just waits for the next hour.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            // Folders pinned while this runs get counted before it stops.
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string? folder;
                lock (_gate)
                {
                    folder = _folders.FirstOrDefault(f => !done.Contains(f));
                }

                if (folder is null)
                {
                    break;
                }

                done.Add(folder);
                long bytes = 0;
                try
                {
                    foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Everything))
                    {
                        bytes += file.Length;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Error($"Couldn't add up the size of {folder}", e);
                    continue;
                }

                lock (_gate)
                {
                    if (_folders.Contains(folder))
                    {
                        _sizes[folder] = new FolderSize(bytes, DateTimeOffset.Now);
                    }
                }

                Measured?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e)
        {
            // On a timer thread, where anything not caught would end the app.
            Log.Error("Folder sizing failed", e);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
