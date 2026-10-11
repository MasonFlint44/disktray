using System.IO;
using DiskTray.Core.Cleanup;

namespace DiskTray.App;

/// <summary>
/// Looks at folders on Windows directly, and at folders in WSL through
/// <c>\\wsl.localhost\&lt;distro&gt;</c>, but only in distros that are already running, since
/// opening that path starts the distro.
/// </summary>
internal sealed class FolderLookup(IReadOnlySet<string> runningDistros) : IFolderLookup
{
    /// <summary>
    /// Directory.Exists says false for any error, and a false here can pre-select volumes to
    /// remove, so a folder only counts as gone when its parent can be read and lacks it.
    /// </summary>
    public bool? Exists(ProjectFolder folder)
    {
        if (WindowsPath(folder) is not { } path)
        {
            return null;
        }

        if (Safely(() => Directory.Exists(path), false))
        {
            return true;
        }

        if (folder.Parent is not { } parent || WindowsPath(parent) is not { } parentPath)
        {
            return null;
        }

        var names = Safely<IReadOnlyList<string>?>(() => Directory.EnumerateDirectories(parentPath).Select(d => Path.GetFileName(d)).ToList(), null);
        return names is null ? null : names.Contains(folder.Name, StringComparer.OrdinalIgnoreCase) ? null : false;
    }

    public IReadOnlyList<string>? FolderNames(ProjectFolder folder) =>
        WindowsPath(folder) is { } path
            ? Safely<IReadOnlyList<string>?>(() => Directory.EnumerateDirectories(path).Select(d => Path.GetFileName(d)).ToList(), null)
            : null;

    private string? WindowsPath(ProjectFolder folder)
    {
        if (folder.Distro is null)
        {
            return folder.Path;
        }

        return runningDistros.Contains(folder.Distro)
            ? $@"\\wsl.localhost\{folder.Distro}{folder.Path.Replace('/', '\\')}"
            : null;
    }

    private static T Safely<T>(Func<T> look, T fallback)
    {
        try
        {
            return look();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}
