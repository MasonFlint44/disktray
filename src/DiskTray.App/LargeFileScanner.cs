using System.IO;
using DiskTray.Core.Detection;

namespace DiskTray.App;

/// <summary>
/// Looks through the user folder once a day for files over the size threshold, on a
/// low-priority background thread, and keeps the result in largefiles.json.
/// </summary>
internal sealed class LargeFileScanner : IDisposable
{
    private const string FileName = "largefiles.json";

    /// <summary>How long after startup to look, so it doesn't slow down signing in.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private static readonly EnumerationOptions Everything = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Junctions and links lead to files counted somewhere else, or round in circles.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private long _minBytes;
    private bool _enabled;
    private int _scanning;

    /// <summary>Set when a scan is asked for while one is going, which then goes round again with the new settings.</summary>
    private int _again;

    public LargeFileScanner(bool enabled, long minBytes)
    {
        _enabled = enabled;
        _minBytes = minBytes;
        Last = LargeFileScan.FromJson(AppDataFile.ReadJson(FileName));
        _timer = new Timer(_ => ScanIfDue(), null, StartupDelay, CheckInterval);
    }

    /// <summary>Raised on a worker thread when a scan starts or finishes.</summary>
    public event EventHandler? Changed;

    public LargeFileScan? Last { get; private set; }

    public bool Scanning => _scanning == 1;

    /// <summary>
    /// Follows Settings › Detection. Turning it on with no scan yet, or lowering the
    /// threshold, means looking now.
    /// </summary>
    public void Configure(bool enabled, long minBytes)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _minBytes = minBytes;
        }

        if (enabled && (Last is null || Last.IsDue(DateTimeOffset.Now, minBytes)))
        {
            ScanNow();
        }
    }

    private void ScanNow() => _ = Task.Run(() => Scan(force: true));

    public void Dispose() => _timer.Dispose();

    private void ScanIfDue() => Scan(force: false);

    private void Scan(bool force)
    {
        long minBytes;
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            minBytes = _minBytes;
        }

        if (!force && Last is { } last && !last.IsDue(DateTimeOffset.Now, minBytes))
        {
            return;
        }

        if (Interlocked.Exchange(ref _scanning, 1) == 1)
        {
            // The one going may be for an old threshold: it looks again once it's done.
            if (force)
            {
                Interlocked.Exchange(ref _again, 1);
            }

            return;
        }

        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            Changed?.Invoke(this, EventArgs.Empty);
            string root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var found = new List<LargeFile>();
            var started = DateTimeOffset.Now;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Everything))
            {
                try
                {
                    if (file.Length >= minBytes)
                    {
                        found.Add(new LargeFile(file.FullName, file.Length));
                    }
                }
                catch (IOException)
                {
                    // Gone since it was listed.
                }
            }

            Last = new LargeFileScan(started, minBytes, found);
            AppDataFile.Write(FileName, Last.ToJson());
            Log.Info($"Large-file scan found {found.Count} files over {minBytes >> 30} GiB in {DateTimeOffset.Now - started:mm\\:ss}");
        }
        catch (Exception e)
        {
            // On a timer thread, where anything not caught would end the app.
            Log.Error("Large-file scan failed", e);
        }
        finally
        {
            thread.Priority = priority;
            Interlocked.Exchange(ref _scanning, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (Interlocked.Exchange(ref _again, 0) == 1)
        {
            Scan(force: false);
        }
    }
}
