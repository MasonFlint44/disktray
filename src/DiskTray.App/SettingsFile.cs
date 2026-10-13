using DiskTray.Core.Settings;

namespace DiskTray.App;

/// <summary>Keeps the <see cref="AppSettings"/> in <c>%LOCALAPPDATA%\DiskTray\settings.json</c>.</summary>
internal static class SettingsFile
{
    private const string Name = "settings.json";

    /// <summary>
    /// The saved settings. Values that can't be read are logged and get their defaults, and
    /// the file is kept as settings.json.bad first. When the file is there but can't be read
    /// at all, the defaults are used as if DiskTray were already in Startup apps, so starting
    /// doesn't switch that back on.
    /// </summary>
    public static AppSettings Load()
    {
        if (!AppDataFile.TryRead(Name, out string? text))
        {
            return AppSettings.FromJson(null) with { StartupRegistered = true };
        }

        var (settings, dropped) = AppSettings.Read(text);
        if (dropped.Count > 0)
        {
            AppDataFile.KeepBadCopy(Name, text!, $"used the defaults for {string.Join(", ", dropped)}");
        }

        return settings;
    }

    public static bool Save(AppSettings settings) => AppDataFile.Write(Name, settings.ToJson());
}
