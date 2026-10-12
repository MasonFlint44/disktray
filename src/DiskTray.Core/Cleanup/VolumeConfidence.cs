using System.Text;

namespace DiskTray.Core.Cleanup;

/// <summary>What's known about the folder an unused volume's project came from.</summary>
public enum FolderCheck
{
    /// <summary>The project's folder is known and is gone. The only case that's pre-selected.</summary>
    Deleted,

    /// <summary>
    /// Checking the folder, or searching for one, would mean starting a distro; or the folder
    /// isn't where it was assumed to be, in a distro that wasn't recorded.
    /// </summary>
    NotChecked,

    /// <summary>The folder isn't known and nothing in the code folders matches the project's name.</summary>
    NoMatch,

    /// <summary>The folder is there, or one with a matching name is.</summary>
    Exists,

    /// <summary>The volume isn't part of a Compose project.</summary>
    NoProject,
}

/// <summary>Unused volumes from one Compose project, or the ones that have no project.</summary>
/// <param name="Guessed">The folder was found by matching the project's name, not recorded.</param>
/// <param name="Searched">The code folders that were searched by name.</param>
public sealed record VolumeGroup(
    string? Project,
    FolderCheck Check,
    ProjectFolder? Folder,
    bool Guessed,
    IReadOnlyList<DockerVolume> Volumes,
    IReadOnlyList<ProjectFolder> Searched)
{
    public bool PreSelected => Check == FolderCheck.Deleted;

    public long SizeBytes => Volumes.Sum(v => v.SizeBytes);
}

/// <summary>Looks at folders on Windows and in WSL. Implemented in the app.</summary>
public interface IFolderLookup
{
    /// <summary>Whether the folder is there, or null when checking would mean starting its distro.</summary>
    bool? Exists(ProjectFolder folder);

    /// <summary>The names of the folders inside it, or null when they can't be listed.</summary>
    IReadOnlyList<string>? FolderNames(ProjectFolder folder);
}

/// <summary>
/// Groups unused volumes by Compose project and works out how sure it is that each
/// project's folder is gone. In order, a project's folder comes from a container's labels,
/// then from <see cref="ProjectFolderCache"/>, then from a name match in the code folders.
/// Only a known folder that's missing counts as deleted.
/// </summary>
public static class VolumeConfidence
{
    public static IReadOnlyList<VolumeGroup> Group(
        IReadOnlyList<DockerVolume> unused,
        IReadOnlyDictionary<string, ProjectFolder> fromContainers,
        ProjectFolderCache cache,
        IEnumerable<ProjectFolder> codeFolders,
        IFolderLookup lookup)
    {
        // Folders that held projects before are likely where others live too.
        var known = fromContainers.Values
            .Concat(unused.Select(v => v.Project).OfType<string>().Distinct().Select(cache.Get).OfType<ProjectFolder>());
        var listings = codeFolders.Concat(known.Select(f => f.Parent).OfType<ProjectFolder>())
            .Distinct()
            .Select(f => (Folder: f, Names: lookup.FolderNames(f)))
            .ToList();
        var searched = listings.Where(l => l.Names is not null).Select(l => l.Folder).ToList();

        var groups = new List<VolumeGroup>();
        foreach (var byProject in unused.GroupBy(v => v.Project, StringComparer.Ordinal))
        {
            var volumes = byProject.OrderByDescending(v => v.SizeBytes).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();
            if (byProject.Key is not { } project)
            {
                groups.Add(new VolumeGroup(null, FolderCheck.NoProject, null, false, volumes, []));
                continue;
            }

            if (FolderOf(project, fromContainers, cache) is { } folder)
            {
                var check = lookup.Exists(folder) switch
                {
                    true => FolderCheck.Exists,
                    // Only a folder whose distro is known for sure can be called gone.
                    false when !folder.DistroGuessed => FolderCheck.Deleted,
                    _ => FolderCheck.NotChecked,
                };
                groups.Add(new VolumeGroup(project, check, folder, false, volumes, []));
            }
            else if (MatchByName(project, listings) is { } match)
            {
                groups.Add(new VolumeGroup(project, FolderCheck.Exists, match, true, volumes, searched));
            }
            else
            {
                // Code folders in distros that aren't running couldn't be searched.
                var check = searched.Count > 0 || listings.Count == 0 ? FolderCheck.NoMatch : FolderCheck.NotChecked;
                groups.Add(new VolumeGroup(project, check, null, false, volumes, searched));
            }
        }

        return groups
            .OrderBy(g => g.Check == FolderCheck.NoProject)
            .ThenByDescending(g => g.PreSelected)
            .ThenByDescending(g => g.SizeBytes)
            .ThenBy(g => g.Project, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A recorded folder beats one whose distro was guessed, whether it's from a container
    /// now or remembered; then a container's beats the remembered one.
    /// </summary>
    private static ProjectFolder? FolderOf(string project, IReadOnlyDictionary<string, ProjectFolder> fromContainers, ProjectFolderCache cache)
    {
        var live = fromContainers.GetValueOrDefault(project);
        var remembered = cache.Get(project);
        return live is { DistroGuessed: false } ? live
            : remembered is { DistroGuessed: false } ? remembered
            : live ?? remembered;
    }

    /// <summary>
    /// Each Compose project's folder, from its containers' labels. VS Code's
    /// <c>devcontainer.local_folder</c> wins over Compose's working folder, which doesn't say
    /// which distro it's in; that comes from a bind mount, or else is assumed to be the default
    /// distro, marked <see cref="ProjectFolder.DistroGuessed"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, ProjectFolder> FoldersFromContainers(
        IReadOnlyList<DockerContainer> containers,
        string? defaultDistro)
    {
        var folders = new Dictionary<string, ProjectFolder>(StringComparer.Ordinal);
        foreach (var byProject in containers
            .Where(c => c.Labels.GetValueOrDefault(DockerOutput.ProjectLabel) is { Length: > 0 })
            .GroupBy(c => c.Labels[DockerOutput.ProjectLabel], StringComparer.Ordinal))
        {
            var local = byProject
                .Select(c => ProjectFolder.FromWindowsPath(c.Labels.GetValueOrDefault(DockerOutput.LocalFolderLabel)))
                .OfType<ProjectFolder>()
                .FirstOrDefault();
            if (local is not null)
            {
                folders[byProject.Key] = local;
                continue;
            }

            string? recorded = byProject.SelectMany(c => c.BindSources)
                .Select(ProjectFolder.DistroOfBindMount)
                .OfType<string>()
                .FirstOrDefault();
            var working = byProject
                .Select(c => ProjectFolder.FromWorkingDir(c.Labels.GetValueOrDefault(DockerOutput.WorkingDirLabel), recorded ?? defaultDistro))
                .OfType<ProjectFolder>()
                .FirstOrDefault();
            if (working is not null)
            {
                folders[byProject.Key] = working with { DistroGuessed = working.Distro is not null && recorded is null };
            }
        }

        return folders;
    }

    /// <summary>
    /// The name Compose gives a project in a folder: lower case, keeping only letters, digits,
    /// "-" and "_" (so <c>acme.web-app</c> becomes <c>acmeweb-app</c>).
    /// </summary>
    public static string ProjectName(string folderName)
    {
        var name = new StringBuilder(folderName.Length);
        foreach (char c in folderName.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')
            {
                name.Append(c);
            }
        }

        return name.ToString();
    }

    /// <summary>A folder whose Compose name is the project's, allowing for VS Code's "_devcontainer" ending.</summary>
    private static ProjectFolder? MatchByName(string project, IEnumerable<(ProjectFolder Folder, IReadOnlyList<string>? Names)> listings)
    {
        string bare = project;
        foreach (string suffix in new[] { "_devcontainer", "-devcontainer" })
        {
            if (project.EndsWith(suffix, StringComparison.Ordinal))
            {
                bare = project[..^suffix.Length];
                break;
            }
        }

        foreach (var (parent, names) in listings)
        {
            foreach (string name in names ?? [])
            {
                string candidate = ProjectName(name);
                if (candidate.Length > 0 && (candidate == bare || candidate == project))
                {
                    return parent with { Path = parent.Path.TrimEnd('/', '\\') + (parent.Distro is null ? "\\" : "/") + name };
                }
            }
        }

        return null;
    }
}
