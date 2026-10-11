using System.IO;
using DiskTray.Core.Usage;

namespace DiskTray.App;

/// <summary>Keeps the <see cref="UsageCache"/> in <c>%LOCALAPPDATA%\DiskTray\usage.json</c>.</summary>
internal static class UsageCacheFile
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTray", "usage.json");

    public static UsageCache Load()
    {
        try
        {
            return UsageCache.FromJson(File.Exists(FilePath) ? File.ReadAllText(FilePath) : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't read the usage cache", e);
            return new UsageCache();
        }
    }

    public static void Save(UsageCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write then swap, so a crash mid-write can't leave half a file.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, cache.ToJson());
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save the usage cache", e);
        }
    }
}
