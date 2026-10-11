using System.IO;

namespace DiskTray.App;

/// <summary>
/// Appends to <c>%LOCALAPPDATA%\DiskTray\logs\disktray.log</c>. A tray app has no console,
/// so this is the only place errors show up. Logging never throws.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTray", "logs", "disktray.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report it.
            }
        }
    }
}
