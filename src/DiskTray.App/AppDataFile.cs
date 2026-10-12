using System.IO;

namespace DiskTray.App;

/// <summary>Reads and writes the app's files in <c>%LOCALAPPDATA%\DiskTray\</c>. Failures are logged, never thrown.</summary>
internal static class AppDataFile
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTray");

    /// <summary>The file's text, or null if it doesn't exist or can't be read.</summary>
    public static string? Read(string name)
    {
        string path = Path.Combine(Folder, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't read {path}", e);
            return null;
        }
    }

    public static bool Write(string name, string text)
    {
        string path = Path.Combine(Folder, name);
        try
        {
            Directory.CreateDirectory(Folder);
            // Write then swap, so a crash mid-write can't leave half a file.
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't save {path}", e);
            return false;
        }
    }

    public static void Delete(string name)
    {
        string path = Path.Combine(Folder, name);
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't delete {path}", e);
        }
    }
}
