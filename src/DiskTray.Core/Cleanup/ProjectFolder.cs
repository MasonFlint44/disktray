namespace DiskTray.Core.Cleanup;

/// <summary>
/// A folder that holds a project: a Windows path when <see cref="Distro"/> is null, otherwise
/// a Linux path inside that WSL distro.
/// </summary>
public sealed record ProjectFolder(string? Distro, string Path)
{
    private const string BindMountsPrefix = "/run/desktop/mnt/host/wsl/docker-desktop-bind-mounts/";

    /// <summary>The last part of the path, e.g. <c>web-app</c>.</summary>
    public string Name => Path.TrimEnd('/', '\\').Split('/', '\\')[^1];

    /// <summary>The folder it's in, or null at the top.</summary>
    public ProjectFolder? Parent
    {
        get
        {
            string trimmed = Path.TrimEnd('/', '\\');
            int slash = trimmed.LastIndexOfAny(['/', '\\']);
            if (slash <= 0 || (Distro is null && slash <= 2))
            {
                return null;
            }

            return this with { Path = trimmed[..slash] };
        }
    }

    /// <summary>The path as people would write it, with <c>~</c> for the Linux home folder.</summary>
    public string Display
    {
        get
        {
            if (Distro is null || !Path.StartsWith("/home/", StringComparison.Ordinal))
            {
                return Path;
            }

            int end = Path.IndexOf('/', "/home/".Length);
            return end < 0 ? "~" : "~" + Path[end..];
        }
    }

    public bool Equals(ProjectFolder? other) =>
        other is not null
        && string.Equals(Distro, other.Distro, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path, other.Path, Distro is null ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(
            Distro?.ToUpperInvariant(),
            Distro is null ? Path.ToUpperInvariant() : Path);

    /// <summary>
    /// Reads a Windows path as VS Code writes it in <c>devcontainer.local_folder</c>:
    /// <c>C:\code\app</c>, or <c>\\wsl.localhost\Ubuntu\home\me\app</c> (or <c>\\wsl$\…</c>)
    /// for a folder inside a distro.
    /// </summary>
    public static ProjectFolder? FromWindowsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = path[prefix.Length..];
                int slash = rest.IndexOf('\\');
                if (slash <= 0)
                {
                    return null;
                }

                return new ProjectFolder(rest[..slash], "/" + rest[(slash + 1)..].Replace('\\', '/').TrimEnd('/'));
            }
        }

        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
            ? new ProjectFolder(null, path.TrimEnd('\\'))
            : null;
    }

    /// <summary>
    /// Reads Compose's working folder: a Linux path when Compose ran inside a distro (which one
    /// isn't recorded, so it's <paramref name="distro"/>), or a Windows path. A devcontainer's
    /// Compose files sit in <c>.devcontainer</c>, so that last part is left off.
    /// </summary>
    public static ProjectFolder? FromWorkingDir(string? path, string? distro)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        ProjectFolder? folder = path.StartsWith('/')
            ? distro is null ? null : new ProjectFolder(distro, path.TrimEnd('/'))
            : FromWindowsPath(path);
        return folder?.Name == ".devcontainer" ? folder.Parent : folder;
    }

    /// <summary>
    /// The distro a bind mount came from, when Docker Desktop shared it from WSL
    /// (<c>/run/desktop/mnt/host/wsl/docker-desktop-bind-mounts/Ubuntu/…</c>).
    /// </summary>
    public static string? DistroOfBindMount(string source)
    {
        if (!source.StartsWith(BindMountsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = source[BindMountsPrefix.Length..];
        int slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : rest.Length > 0 ? rest : null;
    }
}
