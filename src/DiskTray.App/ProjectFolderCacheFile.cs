using DiskTray.Core.Cleanup;

namespace DiskTray.App;

/// <summary>Keeps the <see cref="ProjectFolderCache"/> in <c>%LOCALAPPDATA%\DiskTray\projects.json</c>.</summary>
internal static class ProjectFolderCacheFile
{
    private const string Name = "projects.json";

    public static ProjectFolderCache Load() => ProjectFolderCache.FromJson(AppDataFile.ReadJson(Name));

    public static void Save(ProjectFolderCache cache) => AppDataFile.Write(Name, cache.ToJson());
}
