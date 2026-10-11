namespace DiskTray.Core.Detection;

/// <summary>
/// Helpers for Windows paths that behave the same on any OS, so detection logic can be
/// tested in WSL. <see cref="System.IO.Path"/> uses '/' on Linux.
/// </summary>
public static class WindowsPath
{
    public static string Join(string first, params string[] rest)
    {
        string result = first.TrimEnd('\\', '/');
        foreach (string part in rest)
        {
            result += '\\' + part.Trim('\\', '/');
        }

        return result;
    }

    /// <summary>Turns <c>\\?\C:\x</c> into <c>C:\x</c> and <c>\\?\UNC\server\share</c> into <c>\\server\share</c>.</summary>
    public static string StripLongPathPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>Returns the drive root (<c>C:\</c>) of a drive-letter path, or null for anything else.</summary>
    public static string? GetDriveRoot(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            ? $"{char.ToUpperInvariant(path[0])}:\\"
            : null;

    /// <summary>The last part of the path: <c>ext4.vhdx</c>, or <c>git</c> for <c>C:\code\git\</c>. The path itself if it has none.</summary>
    public static string GetName(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        int slash = trimmed.LastIndexOfAny(['\\', '/']);
        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length > 0 && !name.EndsWith(':') ? name : path;
    }
}
