using System.IO;
using System.Text.Json;

namespace DiskTray.App;

/// <summary>Reads and writes the app's files in <c>%LOCALAPPDATA%\DiskTray\</c>. Failures are logged, never thrown.</summary>
internal static class AppDataFile
{
    private const string TempEnding = ".tmp";

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTray");

    /// <summary>The file's text, or null if it doesn't exist or can't be read.</summary>
    public static string? Read(string name) => TryRead(name, out string? text) ? text : null;

    /// <summary>
    /// The file's text, or null when it doesn't exist. False when it's there but couldn't be
    /// read, even after a few tries (something such as a virus scanner can hold it at sign-in).
    /// </summary>
    public static bool TryRead(string name, out string? text)
    {
        string path = Path.Combine(Folder, name);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                text = File.Exists(path) ? File.ReadAllText(path) : null;
                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(200);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Couldn't read {path}", e);
                text = null;
                return false;
            }
        }
    }

    /// <summary>
    /// The file's text when it's JSON, or null. One that isn't (cut short, say) is logged and
    /// kept as <c>name.bad</c> before it gets written over.
    /// </summary>
    public static string? ReadJson(string name)
    {
        string? text = Read(name);
        if (text is null)
        {
            return null;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException e)
        {
            KeepBadCopy(name, text, $"it isn't JSON ({e.Message})");
            return null;
        }
    }

    /// <summary>Saves what couldn't be read as <c>name.bad</c>, so it isn't lost when the file is written again.</summary>
    public static void KeepBadCopy(string name, string text, string why)
    {
        Log.Error($"Couldn't read all of {Path.Combine(Folder, name)}: {why}. Kept it as {name}.bad", null);
        Write(name + ".bad", text);
    }

    public static bool Write(string name, string text)
    {
        string path = Path.Combine(Folder, name);
        // Write then swap, so a crash mid-write can't leave half a file. Named for this
        // write alone, so two at once can't swap in each other's half-written file.
        string temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}{TempEnding}";
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't save {path}", e);
            Delete(Path.GetFileName(temp), quiet: true);
            return false;
        }
    }

    /// <summary>Deletes what writes cut short by a crash left behind. For when DiskTray starts.</summary>
    public static void RemoveLeftovers()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (string temp in Directory.EnumerateFiles(Folder, "*" + TempEnding))
            {
                if (File.GetLastWriteTimeUtc(temp) < DateTime.UtcNow - TimeSpan.FromMinutes(1))
                {
                    Delete(Path.GetFileName(temp), quiet: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't look for leftovers in {Folder}", e);
        }
    }

    public static void Delete(string name) => Delete(name, quiet: false);

    private static void Delete(string name, bool quiet)
    {
        string path = Path.Combine(Folder, name);
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!quiet)
            {
                Log.Error($"Couldn't delete {path}", e);
            }
        }
    }
}
