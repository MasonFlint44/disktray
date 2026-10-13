using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>Keeps the <see cref="UsageCache"/> in <c>%LOCALAPPDATA%\DiskTray\usage.json</c>.</summary>
internal static class UsageCacheFile
{
    private const string Name = "usage.json";

    public static UsageCache Load() => UsageCache.FromJson(AppDataFile.Read(Name));

    public static bool Save(UsageCache cache) => AppDataFile.Write(Name, cache.ToJson());
}
