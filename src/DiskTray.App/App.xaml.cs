using System.Windows;
using DiskTray.Core.Detection;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>
/// Tray-only app: there is no main window, so the app runs until the user picks Exit from
/// the tray menu, or another copy is started with <c>--exit</c>.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\DiskTray";
    private const string ExitEventName = @"Local\DiskTray.Exit";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private TrayController? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A tray app that crashes just vanishes, so log errors and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled error", args.Exception);
            args.Handled = true;
            _tray?.ShowUnexpectedError();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled error", args.ExceptionObject as Exception);

        try
        {
            await StartAsync(e.Args);
        }
        catch (Exception ex)
        {
            // There's no window, so without this a failed start would leave an invisible
            // process running.
            Log.Error("Startup failed", ex);
            Shutdown(1);
        }
    }

    private async Task StartAsync(string[] args)
    {
        // Clean up only logs what it would do, for trying it out without changing anything.
        bool dryRun = args.Contains("--dry-run");
        args = args.Where(a => a != "--dry-run").ToArray();
        switch (args)
        {
            case ["--render-icons", string folder]:
                IconPreview.Run(CreateMonitor(), folder);
                Shutdown();
                return;
            case ["--exit"]:
                // Lets scripts and the installer close the running copy cleanly, so its icons
                // leave the tray instead of lingering until hovered, and its files can be
                // replaced once this returns.
                CloseRunningCopy(wait: true);
                Shutdown();
                return;
            case ["--remove-user-data"]:
                // The installer runs this when DiskTray is uninstalled.
                Uninstaller.RemoveUserData(() => CloseRunningCopy(wait: true));
                Shutdown();
                return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitSignal, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);

        Log.Info($"Started {Environment.ProcessPath}{(dryRun ? " for clean up dry runs" : "")}");
        Uninstaller.RemoveOldEntry();
        var settings = SettingsFile.Load();
        if (!settings.StartupRegistered && StartupRegistration.Set(true))
        {
            settings = settings with { StartupRegistered = true };
            SettingsFile.Save(settings);
        }
        else
        {
            StartupRegistration.Follow();
        }

        Themes.Choose(settings.Theme);
        Notifier.Start();
        _tray = new TrayController(CreateMonitor(), settings, dryRun);
        await _tray.StartAsync();
    }

    /// <summary>Asks the running copy to exit, and optionally waits until it has. False if it's still running.</summary>
    private static bool CloseRunningCopy(bool wait)
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var signal))
        {
            return true;
        }

        using (signal)
        {
            signal.Set();
        }

        if (!wait)
        {
            return true;
        }

        // The running copy holds the instance mutex until it has exited.
        using var instance = new Mutex(initiallyOwned: false, InstanceMutexName);
        try
        {
            if (!instance.WaitOne(TimeSpan.FromSeconds(10)))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // It ended without letting go, which still means it's gone.
        }

        instance.ReleaseMutex();
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private static DiskMonitor CreateMonitor()
    {
        var folders = new FolderSizer();
        var scanner = new DiskScanner(
            new SystemDiskProbe(),
            new LxssRegistry(),
            new ScanPaths(
                (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + @"\",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            TimeProvider.System,
            folders);
        var cache = UsageCacheFile.Load();
        return new DiskMonitor(scanner, new UsageMeter(new WslExe(), cache, TimeProvider.System), cache, folders);
    }
}
