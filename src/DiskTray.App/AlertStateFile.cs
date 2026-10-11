using DiskTray.Core.Alerts;

namespace DiskTray.App;

/// <summary>Keeps the <see cref="AlertState"/> in <c>%LOCALAPPDATA%\DiskTray\alerts.json</c>.</summary>
internal static class AlertStateFile
{
    private const string Name = "alerts.json";

    public static AlertState Load() => AlertState.FromJson(AppDataFile.Read(Name));

    public static void Save(AlertState state) => AppDataFile.Write(Name, state.ToJson());
}
