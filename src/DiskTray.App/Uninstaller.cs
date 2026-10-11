using System.Diagnostics;
using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace DiskTray.App;

/// <summary>
/// Removes what DiskTray adds for each user, which the installer can't see: the Startup apps
/// entry, the notification registration and <c>%LOCALAPPDATA%\DiskTray</c>. The installer
/// runs <c>DiskTray.exe --remove-user-data</c> as the user who uninstalls it.
/// </summary>
internal static class Uninstaller
{
    /// <summary>
    /// Where builds before the installer listed themselves in Settings › Apps. The installer
    /// lists DiskTray now, so a leftover entry would show it twice.
    /// </summary>
    private const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskTray";

    /// <summary>Takes out the Settings › Apps entry that builds before the installer wrote.</summary>
    public static void RemoveOldEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Couldn't remove the old Settings › Apps entry", e);
        }
    }

    /// <param name="closeRunningCopy">Asks the running copy to exit, and waits until it has.</param>
    public static void RemoveUserData(Func<bool> closeRunningCopy)
    {
        Log.Info($"Removing this user's DiskTray settings and registrations ({Environment.ProcessPath})");
        if (!closeRunningCopy())
        {
            Log.Info("The running copy didn't close; removing them anyway");
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

        RemoveOldEntry();

        // Last, since the log lives there too.
        try
        {
            Directory.Delete(AppDataFile.Folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to log it; the folder just stays behind.
            Debug.WriteLine(e);
        }
    }
}
