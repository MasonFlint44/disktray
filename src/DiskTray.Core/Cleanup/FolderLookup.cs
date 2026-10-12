namespace DiskTray.Core.Cleanup;

/// <summary>
/// Looks at folders on Windows directly, and at folders in WSL through
/// <c>\\wsl.localhost\&lt;distro&gt;</c>, but only in distros that are already running, since
/// opening that path starts the distro.
/// </summary>
/// <param name="wslPath">
/// Where a distro's Linux path is seen from here; <c>\\wsl.localhost\&lt;distro&gt;\…</c>
/// unless a test says otherwise.
/// </param>
public sealed class FolderLookup(IReadOnlySet<string> runningDistros, Func<string, string, string>? wslPath = null) : IFolderLookup
{
    private readonly Func<string, string, string> _wslPath = wslPath ?? ((distro, path) => $@"\\wsl.localhost\{distro}{path.Replace('/', '\\')}");

    /// <summary>
    /// Directory.Exists says false for any error, and a false here can pre-select volumes to
    /// remove, so a folder only counts as gone when its parent can be read and has nothing
    /// by that name: not a folder, and not a file or link either, since Windows can show a
    /// Linux symlink to a folder as one of those.
    /// </summary>
    public bool? Exists(ProjectFolder folder)
    {
        if (LocalPath(folder) is not { } path)
        {
            return null;
        }

        if (Safely(() => Directory.Exists(path), false))
        {
            return true;
        }

        if (folder.Parent is not { } parent || LocalPath(parent) is not { } parentPath)
        {
            return null;
        }

        var names = Safely<IReadOnlyList<string>?>(() => Entries(parentPath), null);
        return names is null ? null : names.Contains(folder.Name, StringComparer.OrdinalIgnoreCase) ? null : false;
    }

    /// <summary>The names in the folder, links included, or null when it can't be listed.</summary>
    public IReadOnlyList<string>? FolderNames(ProjectFolder folder) =>
        LocalPath(folder) is { } path ? Safely<IReadOnlyList<string>?>(() => Entries(path), null) : null;

    private static List<string> Entries(string path) =>
        Directory.EnumerateFileSystemEntries(path).Select(e => Path.GetFileName(e.TrimEnd('/', '\\'))).ToList();

    private string? LocalPath(ProjectFolder folder)
    {
        if (folder.Distro is null)
        {
            return folder.Path;
        }

        return runningDistros.Contains(folder.Distro) ? _wslPath(folder.Distro, folder.Path) : null;
    }

    private static T Safely<T>(Func<T> look, T fallback)
    {
        try
        {
            return look();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return fallback;
        }
    }
}
