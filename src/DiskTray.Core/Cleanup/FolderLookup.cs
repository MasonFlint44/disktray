namespace DiskTray.Core.Cleanup;

/// <summary>
/// Looks at folders on Windows directly, and at folders in WSL through
/// <c>\\wsl.localhost\&lt;distro&gt;</c>, but only in distros that are already running, since
/// opening that path starts the distro. Each look has a time limit, since a stuck WSL share
/// (or network drive) can hang one for good; once one runs out, nothing more is asked of
/// that distro (or of Windows), and what isn't asked counts as can't tell.
/// </summary>
/// <param name="wslPath">
/// Where a distro's Linux path is seen from here; <c>\\wsl.localhost\&lt;distro&gt;\…</c>
/// unless a test says otherwise.
/// </param>
/// <param name="folderExists">Directory.Exists, unless a test says otherwise.</param>
/// <param name="entries">The names in a folder, links included, unless a test says otherwise.</param>
/// <param name="timeout">How long one look may take; <see cref="DefaultTimeout"/> unless a test says otherwise.</param>
public sealed class FolderLookup(
    IReadOnlySet<string> runningDistros,
    Func<string, string, string>? wslPath = null,
    Func<string, bool>? folderExists = null,
    Func<string, IReadOnlyList<string>>? entries = null,
    TimeSpan? timeout = null) : IFolderLookup
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, string, string> _wslPath = wslPath ?? ((distro, path) => $@"\\wsl.localhost\{distro}{path.Replace('/', '\\')}");
    private readonly Func<string, bool> _folderExists = folderExists ?? Directory.Exists;
    private readonly Func<string, IReadOnlyList<string>> _entries = entries ?? Entries;
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    // Distros (and "" for Windows) where a look ran out of time.
    private readonly HashSet<string> _stuck = new(StringComparer.OrdinalIgnoreCase);

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

        switch (Safely<bool?>(folder, () => _folderExists(path), false))
        {
            case true:
                return true;
            case null:
                return null;
        }

        if (folder.Parent is not { } parent || LocalPath(parent) is not { } parentPath)
        {
            return null;
        }

        var names = Safely<IReadOnlyList<string>?>(folder, () => _entries(parentPath), null);
        return names is null ? null : names.Contains(folder.Name, StringComparer.OrdinalIgnoreCase) ? null : false;
    }

    /// <summary>The names in the folder, links included, or null when it can't be listed.</summary>
    public IReadOnlyList<string>? FolderNames(ProjectFolder folder) =>
        LocalPath(folder) is { } path ? Safely<IReadOnlyList<string>?>(folder, () => _entries(path), null) : null;

    private static IReadOnlyList<string> Entries(string path) =>
        Directory.EnumerateFileSystemEntries(path).Select(e => Path.GetFileName(e.TrimEnd('/', '\\'))).ToList();

    private string? LocalPath(ProjectFolder folder)
    {
        if (folder.Distro is null)
        {
            return folder.Path;
        }

        return runningDistros.Contains(folder.Distro) ? _wslPath(folder.Distro, folder.Path) : null;
    }

    /// <summary>
    /// The look's answer; <paramref name="fallback"/> when it fails; default (can't tell) when
    /// it runs out of time, or another look where <paramref name="folder"/> is did.
    /// </summary>
    private T? Safely<T>(ProjectFolder folder, Func<T> look, T fallback)
    {
        string where = folder.Distro ?? "";
        lock (_stuck)
        {
            if (_stuck.Contains(where))
            {
                return default;
            }
        }

        var task = Task.Run(look);
        try
        {
            if (task.Wait(_timeout))
            {
                return task.Result;
            }
        }
        catch (AggregateException e) when (e.InnerException is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return fallback;
        }

        lock (_stuck)
        {
            _stuck.Add(where);
        }

        return default;
    }
}
