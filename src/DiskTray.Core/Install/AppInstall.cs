using System.Globalization;
using DiskTray.Core.Detection;

namespace DiskTray.Core.Install;

/// <summary>
/// The entry that lists DiskTray in Settings › Apps (Add or remove programs), under
/// <see cref="AppInstall.UninstallKey"/> in the current user's registry.
/// </summary>
/// <param name="InstallDate">yyyyMMdd, which Settings › Apps shows.</param>
public sealed record UninstallEntry(
    string DisplayName,
    string DisplayIcon,
    string DisplayVersion,
    string InstallLocation,
    string UninstallString,
    string QuietUninstallString,
    int EstimatedSizeKiB,
    string InstallDate)
{
    /// <summary>The values to write, by name. Numbers are DWORDs.</summary>
    public IReadOnlyDictionary<string, object> Values => new Dictionary<string, object>
    {
        ["DisplayName"] = DisplayName,
        ["DisplayIcon"] = DisplayIcon,
        ["DisplayVersion"] = DisplayVersion,
        ["InstallLocation"] = InstallLocation,
        ["UninstallString"] = UninstallString,
        ["QuietUninstallString"] = QuietUninstallString,
        ["EstimatedSize"] = EstimatedSizeKiB,
        ["InstallDate"] = InstallDate,
        // There's nothing to change or repair, so Settings › Apps only offers Uninstall.
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
    };
}

/// <summary>
/// Where DiskTray installs and what removing it takes. It installs per user, without an
/// installer, by copying into <c>%LOCALAPPDATA%\Programs\DiskTray</c>.
/// </summary>
public static class AppInstall
{
    public const string Name = "DiskTray";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskTray";

    /// <summary><c>%LOCALAPPDATA%\Programs\DiskTray</c></summary>
    public static string Folder(string localAppData) => WindowsPath.Join(localAppData, "Programs", Name);

    /// <summary><c>%LOCALAPPDATA%\DiskTray</c>, where settings, caches and logs live.</summary>
    public static string DataFolder(string localAppData) => WindowsPath.Join(localAppData, Name);

    /// <summary>
    /// Whether the exe is the installed copy, as opposed to a build somewhere else, which
    /// shouldn't list itself in Settings › Apps or be deleted by an uninstall.
    /// </summary>
    public static bool IsInstalledCopy(string exePath, string localAppData) =>
        string.Equals(Parent(exePath), Folder(localAppData), StringComparison.OrdinalIgnoreCase)
        && string.Equals(WindowsPath.GetName(exePath), Name + ".exe", StringComparison.OrdinalIgnoreCase);

    /// <param name="installedOn">When it was first installed: the entry's old date, or today.</param>
    public static UninstallEntry Entry(string exePath, string version, long folderBytes, DateOnly installedOn) => new(
        Name,
        $"\"{exePath}\",0",
        version,
        Parent(exePath),
        $"\"{exePath}\" --uninstall",
        $"\"{exePath}\" --uninstall --quiet",
        (int)Math.Clamp(folderBytes / 1024, 0, int.MaxValue),
        installedOn.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

    /// <summary>
    /// The hidden command that deletes the app's folder once DiskTray has exited, since a
    /// running exe can't delete itself. It waits a few seconds, then removes the folder.
    /// </summary>
    public static IReadOnlyList<string> RemoveFolderCommand(string folder)
    {
        if (folder.Contains('"') || WindowsPath.GetName(folder) != Name)
        {
            throw new ArgumentException($"Won't delete {folder}; it isn't DiskTray's folder.", nameof(folder));
        }

        return ["/d", "/c", $"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{folder}\""];
    }

    private static string Parent(string path)
    {
        string trimmed = path.TrimEnd('\\');
        int slash = trimmed.LastIndexOf('\\');
        return slash > 0 ? trimmed[..slash] : trimmed;
    }
}
