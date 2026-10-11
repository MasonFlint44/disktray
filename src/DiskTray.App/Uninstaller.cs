using System.Diagnostics;
using System.IO;
using System.Reflection;
using DiskTray.Core.Install;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DiskTray.App;

/// <summary>
/// Lists DiskTray in Settings › Apps, and removes everything it added when uninstalled
/// from there (or with <c>DiskTray.exe --uninstall</c>).
/// </summary>
internal static class Uninstaller
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>
    /// Writes or refreshes the Settings › Apps entry, so its version, size and folder stay
    /// right after an update. Only the installed copy lists itself.
    /// </summary>
    public static void Register()
    {
        if (!AppInstall.IsInstalledCopy(ExePath, LocalAppData))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppInstall.UninstallKey);
            var installedOn = DateOnly.TryParseExact(key.GetValue("InstallDate") as string, "yyyyMMdd", out var date)
                ? date
                : DateOnly.FromDateTime(DateTime.Now);
            var entry = AppInstall.Entry(ExePath, Version(), FolderBytes(AppInstall.Folder(LocalAppData)), installedOn);
            foreach (var (name, value) in entry.Values)
            {
                key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Couldn't list DiskTray in Settings › Apps", e);
        }
    }

    /// <summary>
    /// Asks first unless <paramref name="quiet"/>, then closes the running copy and removes
    /// the Startup apps entry, the notification registration, the Settings › Apps entry,
    /// the data folder and, once this has exited, the app's own folder.
    /// </summary>
    /// <param name="closeRunningCopy">Asks the running copy to exit, and waits until it has.</param>
    public static async Task RunAsync(bool quiet, Func<bool> closeRunningCopy)
    {
        if (!quiet)
        {
            var ask = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Uninstall DiskTray",
                Content = "This removes DiskTray from this PC, along with its settings, logs and notifications. Docker and WSL aren't touched.",
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
            };
            if (await ask.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                return;
            }
        }

        Log.Info($"Uninstalling {ExePath}");
        if (!closeRunningCopy())
        {
            Log.Info("The running copy didn't close; uninstalling anyway");
        }

        StartupRegistration.Remove();
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception e)
        {
            Log.Error("Couldn't remove the notification registration", e);
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(AppInstall.UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Couldn't remove the Settings › Apps entry", e);
        }

        // Last, since the log lives there too.
        try
        {
            Directory.Delete(AppInstall.DataFolder(LocalAppData), recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to log it; the folder just stays behind.
            Debug.WriteLine(e);
        }

        bool installed = AppInstall.IsInstalledCopy(ExePath, LocalAppData);
        if (installed)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
            foreach (string arg in AppInstall.RemoveFolderCommand(AppInstall.Folder(LocalAppData)))
            {
                start.ArgumentList.Add(arg);
            }

            Process.Start(start)?.Dispose();
        }

        if (!quiet)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Uninstall DiskTray",
                Content = installed
                    ? "DiskTray has been uninstalled."
                    : $"DiskTray's settings and registrations have been removed. This copy isn't the installed one, so its folder was left alone:\n{Path.GetDirectoryName(ExePath)}",
                CloseButtonText = "Close",
            }.ShowDialogAsync();
        }
    }

    /// <summary>"0.1.0", without the build's commit hash.</summary>
    private static string Version()
    {
        string version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private static long FolderBytes(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
