using DiskTray.Core.Settings;

namespace DiskTray.App;

/// <summary>Keeps the <see cref="AppSettings"/> in <c>%LOCALAPPDATA%\DiskTray\settings.json</c>.</summary>
internal static class SettingsFile
{
    private const string Name = "settings.json";

    public static AppSettings Load() => AppSettings.FromJson(AppDataFile.Read(Name));

    public static bool Save(AppSettings settings) => AppDataFile.Write(Name, settings.ToJson());
}
