using DiskTray.Core.Startup;
using Microsoft.Win32;

namespace DiskTray.App;

/// <summary>
/// Start with Windows. The app keeps an entry in the current user's Run key and turns it on
/// or off with Task Manager's switch for it, so it stays listed in Startup apps and can be
/// changed from there or from Settings.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "DiskTray";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>
    /// True when Windows starts this copy of the app: the Run entry points here and isn't
    /// switched off in Task Manager.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (!string.Equals(run?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return !StartupApproval.IsDisabled(approved?.GetValue(ValueName) as byte[]);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't read the Run key", e);
            return false;
        }
    }

    /// <summary>
    /// Points an existing Run entry at this copy when it names another one, keeping whether
    /// it's switched on. Otherwise Windows would go on starting the old copy.
    /// </summary>
    public static void Follow()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (StartupEntry.ShouldFollow(run?.GetValue(ValueName) as string, Command))
            {
                Log.Info($"Pointing Start with Windows at {Command} instead of {run!.GetValue(ValueName)}");
                run.SetValue(ValueName, Command);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Log.Error("Couldn't update the Run entry", e);
        }
    }

    /// <summary>Takes DiskTray out of Startup apps entirely, for an uninstall.</summary>
    public static void Remove()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Log.Error("Couldn't remove the Run entry", e);
        }
    }

    /// <summary>Points the Run entry at this copy and switches it on or off, the way Task Manager does.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                run.SetValue(ValueName, Command);
            }

            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            approved.SetValue(
                ValueName,
                enabled ? StartupApproval.Enabled() : StartupApproval.Disabled(DateTimeOffset.Now),
                RegistryValueKind.Binary);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Log.Error("Couldn't change the Run key", e);
            return false;
        }
    }
}
