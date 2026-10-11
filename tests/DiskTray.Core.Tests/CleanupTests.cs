using System.Globalization;
using DiskTray.Core.Cleanup;

namespace DiskTray.Core.Tests;

/// <summary>Docker output in the shape Docker Desktop 4.43 prints it, with made-up projects.</summary>
internal static class DockerSamples
{
    public const string DiskUsage = """
        {"Images":[
          {"Containers":"1","ID":"sha256:aaa","Repository":"vsc-billing-api-1234-uid","Tag":"latest","Size":"2.53GB","SharedSize":"2.18GB","UniqueSize":"350.8MB"},
          {"Containers":"0","ID":"sha256:bbb","Repository":"<none>","Tag":"<none>","Size":"1.2GB","SharedSize":"200MB","UniqueSize":"1GB"},
          {"Containers":"1","ID":"sha256:ccc","Repository":"<none>","Tag":"<none>","Size":"500MB","SharedSize":"0B","UniqueSize":"500MB"}
        ],
        "Containers":[],
        "Volumes":[
          {"Driver":"local","Labels":"com.docker.compose.config-hash=f443,com.docker.compose.project=acmeweb-app_devcontainer,com.docker.compose.version=5.5.1,com.docker.compose.volume=sqlserver","Links":"0","Name":"acmeweb-app_devcontainer_sqlserver","Size":"2.8GB"},
          {"Driver":"local","Labels":"com.docker.compose.project=acmeweb-app_devcontainer,com.docker.compose.volume=vscode-extensions","Links":"0","Name":"acmeweb-app_devcontainer_vscode-extensions","Size":"2GB"},
          {"Driver":"local","Labels":"com.docker.compose.project=billing-api_devcontainer,com.docker.compose.volume=sqlserver","Links":"1","Name":"billing-api_devcontainer_sqlserver","Size":"3.111GB"},
          {"Driver":"local","Labels":"com.docker.compose.project=notes_devcontainer,com.docker.compose.volume=conda","Links":"0","Name":"notes_devcontainer_conda","Size":"449MB"},
          {"Driver":"local","Labels":"com.docker.compose.project=old-thing_devcontainer,com.docker.compose.volume=redis","Links":"0","Name":"old-thing_devcontainer_redis","Size":"21.13kB"},
          {"Driver":"local","Labels":"","Links":"0","Name":"f24829f44cff0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293","Size":"361MB"},
          {"Driver":"local","Labels":"","Links":"3","Name":"vscode","Size":"2.415GB"},
          {"Driver":"local","Labels":"","Links":"N/A","Name":"mystery","Size":"N/A"}
        ],
        "BuildCache":[
          {"CacheType":"regular","ID":"j4rs","InUse":"false","Shared":"true","Size":"28.2MB"},
          {"CacheType":"regular","ID":"k5st","InUse":"false","Shared":"false","Size":"12.8GB"},
          {"CacheType":"source.local","ID":"m6uv","InUse":"true","Shared":"false","Size":"1GB"}
        ]}
        """;

    public const string Inspect = """
        [
          {"Name":"/billing-api_devcontainer-app-1","State":{"Status":"running"},
           "Config":{"Labels":{
             "com.docker.compose.project":"billing-api_devcontainer",
             "com.docker.compose.project.working_dir":"/home/me/git/billing-api/.devcontainer",
             "devcontainer.local_folder":"\\\\wsl.localhost\\Ubuntu\\home\\me\\git\\billing-api"}},
           "Mounts":[{"Type":"volume","Name":"vscode","Source":"/var/lib/docker/volumes/vscode/_data"},
                     {"Type":"bind","Source":"/run/desktop/mnt/host/wsl/docker-desktop-bind-mounts/Ubuntu/1a2b"}]},
          {"Name":"/billing-api_devcontainer-sqlserver-1","State":{"Status":"exited"},
           "Config":{"Labels":{
             "com.docker.compose.project":"billing-api_devcontainer",
             "com.docker.compose.project.working_dir":"/home/me/git/billing-api/.devcontainer"}},
           "Mounts":[]},
          {"Name":"/scratch-redis-1","State":{"Status":"exited"},
           "Config":{"Labels":{
             "com.docker.compose.project":"scratch",
             "com.docker.compose.project.working_dir":"/home/me/scratch"}},
           "Mounts":[{"Type":"bind","Source":"/run/desktop/mnt/host/wsl/docker-desktop-bind-mounts/Debian/9f8e"}]},
          {"Name":"/otel","State":{"Status":"exited"},"Config":{"Labels":null},"Mounts":null}
        ]
        """;
}

internal sealed class FakeFolders : IFolderLookup
{
    /// <summary>Folders that exist, as "distro:path" (or just the path on Windows).</summary>
    public HashSet<string> Existing { get; } = [];

    /// <summary>Distros whose folders can be looked at, because they're running.</summary>
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase) { "Ubuntu" };

    public bool? Exists(ProjectFolder folder) =>
        folder.Distro is { } distro && !Running.Contains(distro) ? null : Existing.Contains(Key(folder));

    public IReadOnlyList<string>? FolderNames(ProjectFolder folder)
    {
        if (folder.Distro is { } distro && !Running.Contains(distro))
        {
            return null;
        }

        string prefix = Key(folder).TrimEnd('/') + "/";
        return Existing.Where(e => e.StartsWith(prefix, StringComparison.Ordinal) && !e[prefix.Length..].Contains('/'))
            .Select(e => e[prefix.Length..])
            .ToList();
    }

    private static string Key(ProjectFolder folder) => folder.Distro is null ? folder.Path : $"{folder.Distro}:{folder.Path}";
}

public class DockerOutputTests
{
    [Theory]
    [InlineData("3.111GB", 3_111_000_000L)]
    [InlineData("21.13kB", 21_130L)]
    [InlineData("0B", 0L)]
    [InlineData("19B", 19L)]
    [InlineData("1.5TB", 1_500_000_000_000L)]
    [InlineData("N/A", null)]
    [InlineData("", null)]
    [InlineData("12 parsecs", null)]
    public void Reads_sizes_as_docker_prints_them(string text, long? expected) =>
        Assert.Equal(expected, DockerOutput.ParseSize(text));

    [Fact]
    public void Reads_labels_whose_values_hold_commas()
    {
        var labels = DockerOutput.ParseLabels("a=1,com.docker.compose.project.config_files=/x/one.yml,/x/two.yml,b=2");

        Assert.Equal("/x/one.yml,/x/two.yml", labels["com.docker.compose.project.config_files"]);
        Assert.Equal("2", labels["b"]);
    }

    [Fact]
    public void Counts_unshared_build_cache_and_unused_dangling_images()
    {
        var usage = DockerOutput.ParseDiskUsage(DockerSamples.DiskUsage)!;

        Assert.Equal(12_800_000_000L, usage.BuildCacheBytes);
        Assert.Equal(1, usage.DanglingImages);
        Assert.Equal(1_000_000_000L, usage.DanglingImageBytes);
    }

    [Fact]
    public void Lists_only_volumes_no_container_uses()
    {
        var usage = DockerOutput.ParseDiskUsage(DockerSamples.DiskUsage)!;

        Assert.Equal(
            ["acmeweb-app_devcontainer_sqlserver", "acmeweb-app_devcontainer_vscode-extensions", "notes_devcontainer_conda", "old-thing_devcontainer_redis", "f24829f44cff0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293"],
            usage.UnusedVolumes.Select(v => v.Name));
        Assert.Equal(
            new DockerVolume("acmeweb-app_devcontainer_sqlserver", 2_800_000_000L, "acmeweb-app_devcontainer", "sqlserver"),
            usage.UnusedVolumes[0]);
        Assert.Null(usage.UnusedVolumes[^1].Project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Gives_up_on_output_it_cant_read(string output)
    {
        Assert.Null(DockerOutput.ParseDiskUsage(output));
        Assert.Empty(DockerOutput.ParseContainers(output == "[]" ? "{}" : output));
    }

    [Fact]
    public void Reads_containers()
    {
        var containers = DockerOutput.ParseContainers(DockerSamples.Inspect);

        Assert.Equal(["billing-api_devcontainer-app-1", "billing-api_devcontainer-sqlserver-1", "scratch-redis-1", "otel"], containers.Select(c => c.Name));
        Assert.True(containers[0].Running);
        Assert.False(containers[1].Running);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\git\billing-api", containers[0].Labels[DockerOutput.LocalFolderLabel]);
        Assert.Equal(["/run/desktop/mnt/host/wsl/docker-desktop-bind-mounts/Ubuntu/1a2b"], containers[0].BindSources);
        Assert.Empty(containers[3].Labels);
    }

    [Theory]
    [InlineData("""{"SessionID":"x","Status":"running"}""", true)]
    [InlineData("""{"Status":"stopped"}""", false)]
    [InlineData("error during connect", false)]
    public void Reads_docker_desktop_status(string output, bool running) =>
        Assert.Equal(running, DockerOutput.ParseDesktopRunning(output));

    [Fact]
    public void Removes_only_the_volumes_named() =>
        Assert.Equal(["volume", "rm", "a", "b"], DockerOutput.RemoveVolumesCommand(["a", "b"]));
}

public class ProjectFolderTests
{
    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\git\app", "Ubuntu", "/home/me/git/app")]
    [InlineData(@"\\wsl$\Ubuntu-24.04\home\me\app\", "Ubuntu-24.04", "/home/me/app")]
    [InlineData(@"C:\code\app\", null, @"C:\code\app")]
    public void Reads_vs_code_folders(string label, string? distro, string path) =>
        Assert.Equal(new ProjectFolder(distro, path), ProjectFolder.FromWindowsPath(label));

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    [InlineData(@"\\wsl.localhost\Ubuntu")]
    [InlineData(@"\\server\share\app")]
    public void Ignores_paths_it_cant_place(string label) => Assert.Null(ProjectFolder.FromWindowsPath(label));

    [Fact]
    public void Drops_the_devcontainer_folder_from_composes_working_folder()
    {
        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/git/app"), ProjectFolder.FromWorkingDir("/home/me/git/app/.devcontainer", "Ubuntu"));
        Assert.Equal(new ProjectFolder(null, @"C:\code\app"), ProjectFolder.FromWorkingDir(@"C:\code\app\.devcontainer", "Ubuntu"));
        Assert.Null(ProjectFolder.FromWorkingDir("/home/me/app", null));
    }

    [Fact]
    public void Shows_the_linux_home_folder_as_a_tilde()
    {
        Assert.Equal("~/git/app", new ProjectFolder("Ubuntu", "/home/me/git/app").Display);
        Assert.Equal("/srv/app", new ProjectFolder("Ubuntu", "/srv/app").Display);
        Assert.Equal(@"C:\code\app", new ProjectFolder(null, @"C:\code\app").Display);
    }

    [Fact]
    public void Knows_its_parent_and_name()
    {
        var folder = new ProjectFolder("Ubuntu", "/home/me/git/app");

        Assert.Equal("app", folder.Name);
        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/git"), folder.Parent);
        Assert.Equal(new ProjectFolder(null, @"C:\code"), new ProjectFolder(null, @"C:\code\app").Parent);
        Assert.Null(new ProjectFolder(null, @"C:\code").Parent);
    }

    [Fact]
    public void Compares_windows_paths_ignoring_case_but_not_linux_ones()
    {
        Assert.Equal(new ProjectFolder(null, @"C:\Code"), new ProjectFolder(null, @"c:\code"));
        Assert.NotEqual(new ProjectFolder("Ubuntu", "/home/Me"), new ProjectFolder("ubuntu", "/home/me"));
        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me"), new ProjectFolder("ubuntu", "/home/me"));
    }

    [Theory]
    [InlineData("/run/desktop/mnt/host/wsl/docker-desktop-bind-mounts/Ubuntu/1a2b", "Ubuntu")]
    [InlineData("/home/me/app", null)]
    public void Finds_the_distro_a_bind_mount_came_from(string source, string? distro) =>
        Assert.Equal(distro, ProjectFolder.DistroOfBindMount(source));
}

public class VolumeConfidenceTests
{
    private static readonly IReadOnlyList<DockerVolume> Unused = DockerOutput.ParseDiskUsage(DockerSamples.DiskUsage)!.UnusedVolumes;
    private static readonly IReadOnlyList<DockerContainer> Containers = DockerOutput.ParseContainers(DockerSamples.Inspect);

    private static IReadOnlyList<VolumeGroup> Group(FakeFolders folders, ProjectFolderCache? cache = null, IReadOnlyList<DockerVolume>? unused = null) =>
        VolumeConfidence.Group(
            unused ?? Unused,
            VolumeConfidence.FoldersFromContainers(Containers, "Ubuntu"),
            cache ?? Cache(),
            [],
            folders);

    /// <summary>acme.web-app was recorded earlier and has since been deleted.</summary>
    private static ProjectFolderCache Cache()
    {
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["acmeweb-app_devcontainer"] = new("Ubuntu", "/home/me/git/acme.web-app") }, DateTimeOffset.UnixEpoch);
        return cache;
    }

    private static FakeFolders Folders() => new()
    {
        Existing = { "Ubuntu:/home/me/git/billing-api", "Ubuntu:/home/me/git/notes" },
    };

    [Fact]
    public void Prefers_vs_codes_folder_then_composes_in_the_bind_mounts_distro()
    {
        var folders = VolumeConfidence.FoldersFromContainers(Containers, "Ubuntu");

        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/git/billing-api"), folders["billing-api_devcontainer"]);
        Assert.Equal(new ProjectFolder("Debian", "/home/me/scratch"), folders["scratch"]);
        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public void Tags_each_project_by_what_is_known_about_its_folder()
    {
        var groups = Group(Folders());

        Assert.Equal(
            [
                ("acmeweb-app_devcontainer", FolderCheck.Deleted, false),
                ("notes_devcontainer", FolderCheck.Exists, true),
                ("old-thing_devcontainer", FolderCheck.NoMatch, false),
                (null, FolderCheck.NoProject, false),
            ],
            groups.Select(g => (g.Project, g.Check, g.Guessed)));
        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/git/notes"), groups[1].Folder);
        Assert.Equal([new ProjectFolder("Ubuntu", "/home/me/git")], groups[2].Searched);
    }

    [Fact]
    public void Pre_selects_only_projects_whose_folder_is_gone()
    {
        var groups = Group(Folders());

        Assert.Equal(["acmeweb-app_devcontainer"], groups.Where(g => g.PreSelected).Select(g => g.Project));
        Assert.Equal(["acmeweb-app_devcontainer_sqlserver", "acmeweb-app_devcontainer_vscode-extensions"], groups[0].Volumes.Select(v => v.Name));
    }

    [Fact]
    public void Doesnt_count_a_folder_as_deleted_when_its_distro_isnt_running()
    {
        var folders = Folders();
        folders.Running.Clear();

        var groups = Group(folders);

        Assert.Equal(FolderCheck.NotChecked, groups.Single(g => g.Project == "acmeweb-app_devcontainer").Check);
        Assert.Equal(FolderCheck.NotChecked, groups.Single(g => g.Project == "old-thing_devcontainer").Check);
        Assert.DoesNotContain(groups, g => g.PreSelected);
    }

    [Fact]
    public void Treats_a_recorded_folder_that_still_exists_as_in_use()
    {
        var folders = Folders();
        folders.Existing.Add("Ubuntu:/home/me/git/acme.web-app");

        Assert.Equal(FolderCheck.Exists, Group(folders).Single(g => g.Project == "acmeweb-app_devcontainer").Check);
    }

    [Theory]
    [InlineData("acme.web-app", "acmeweb-app")]
    [InlineData("My Project!", "myproject")]
    [InlineData("under_score-dash", "under_score-dash")]
    public void Names_projects_the_way_compose_does(string folder, string project) =>
        Assert.Equal(project, VolumeConfidence.ProjectName(folder));
}

public class ProjectFolderCacheTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var cache = new ProjectFolderCache();
        Assert.True(cache.Record(new Dictionary<string, ProjectFolder> { ["app"] = new("Ubuntu", "/home/me/app") }, DateTimeOffset.UnixEpoch));
        Assert.False(cache.Record(new Dictionary<string, ProjectFolder> { ["app"] = new("Ubuntu", "/home/me/app") }, DateTimeOffset.UnixEpoch));

        var copy = ProjectFolderCache.FromJson(cache.ToJson());

        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/app"), copy.Get("app"));
        Assert.Null(copy.Get("other"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{"app":{"distro":"Ubuntu"}}""")]
    public void Starts_empty_from_a_bad_file(string? json) => Assert.Null(ProjectFolderCache.FromJson(json).Get("app"));
}

public class CleanupLayoutTests
{
    private static readonly IFormatProvider Invariant = CultureInfo.InvariantCulture;

    /// <summary>The design machine, measured, with Docker running and two volumes left from a deleted project.</summary>
    private static CleanupState State(bool dockerRunning = true, params string[] running)
    {
        var snapshot = Machine.Scan();
        snapshot = snapshot with
        {
            Files = snapshot.Files.Select(f => f.Kind == DiskKind.DockerData
                ? f with { UsedBytes = 36L << 30 }
                : f.Id == Machine.UbuntuId ? f with { UsedBytes = 35L << 30 } : f).ToList(),
        };

        var volumes = new List<DockerVolume>
        {
            new("acme_sqlserver", 3L << 30, "acme", "sqlserver"),
            new("acme_vscode", 2L << 30, "acme", "vscode"),
            new("notes_conda", 1L << 30, "notes", "conda"),
        };
        var groups = new List<VolumeGroup>
        {
            new("acme", FolderCheck.Deleted, new ProjectFolder("Ubuntu", "/home/me/git/acme"), false, volumes[..2], []),
            new("notes", FolderCheck.Exists, new ProjectFolder("Ubuntu", "/home/me/git/notes"), false, volumes[2..], []),
        };
        var docker = dockerRunning ? new DockerDiskUsage(12L << 30, 0, 0, volumes) : null;
        return new CleanupState(
            snapshot,
            docker,
            dockerRunning ? groups : [],
            running.ToHashSet(StringComparer.OrdinalIgnoreCase),
            dockerRunning ? 4 : 0);
    }

    private static CleanupModel Build(CleanupState state, Func<CleanupChoices, CleanupChoices>? change = null)
    {
        var choices = CleanupChoices.Defaults(state);
        return CleanupLayout.Build(state, change?.Invoke(choices) ?? choices, Invariant);
    }

    [Fact]
    public void Picks_both_steps_and_the_deleted_projects_volumes_by_default()
    {
        var choices = CleanupChoices.Defaults(State());

        Assert.True(choices.Prune);
        Assert.True(choices.Compact);
        Assert.Equal(CompactWhen.WhenIdle, choices.When);
        Assert.Equal(["acme_sqlserver", "acme_vscode"], choices.Volumes.Order());
        Assert.True(choices.StartDockerAfter);
    }

    [Fact]
    public void Adds_up_the_prune_and_what_compacting_gets_back()
    {
        var model = Build(State());

        Assert.Equal("Build cache 12 GiB · 2 volumes 5.0 GiB", model.PruneDetail);
        Assert.Equal("17 GiB", model.PruneTotal);
        Assert.Equal("Docker 129 → ~19 GiB · Ubuntu 60 → ~35 GiB", model.CompactDetail);
        Assert.Equal("C: gets back ~135 GiB", model.Summary);
        Assert.Equal("Prunes now, compacts when WSL is idle", model.Plan);
        Assert.True(model.CanStart);
        Assert.Empty(model.Warning);
    }

    [Fact]
    public void Words_each_combination_of_steps()
    {
        var state = State();

        Assert.Equal("Prunes, then shuts down WSL and compacts", Build(state, c => c with { When = CompactWhen.RightAway }).Plan);
        Assert.Equal("Compacts when WSL is idle", Build(state, c => c with { Prune = false }).Plan);
        Assert.Equal("Shuts down WSL and compacts now", Build(state, c => c with { Prune = false, When = CompactWhen.RightAway }).Plan);

        var pruneOnly = Build(state, c => c with { Compact = false });
        Assert.Equal("Prunes now · C: gains space after a compact", pruneOnly.Plan);
        Assert.Equal("Frees 17 GiB inside Docker", pruneOnly.Summary);

        var nothing = Build(state, c => c with { Prune = false, Compact = false });
        Assert.Equal("Select a step", nothing.Summary);
        Assert.Equal("Nothing will run", nothing.Plan);
        Assert.False(nothing.CanStart);
    }

    [Fact]
    public void Counts_only_the_ticked_prune_items()
    {
        var model = Build(State(), c => c with { BuildCache = false, Volumes = new HashSet<string> { "notes_conda" } });

        Assert.Equal("1 volume 1.0 GiB", model.PruneDetail);
        Assert.Equal(1L << 30, model.PruneBytes);
    }

    [Fact]
    public void Can_only_compact_when_docker_isnt_running()
    {
        var state = State(dockerRunning: false);
        var model = Build(state);

        Assert.False(CleanupChoices.Defaults(state).Prune);
        Assert.NotNull(model.PruneUnavailable);
        Assert.Equal("Docker 129 → ~36 GiB · Ubuntu 60 → ~35 GiB", model.CompactDetail);
        Assert.Equal("Compacts when WSL is idle", model.Plan);
        Assert.False(CleanupChoices.Defaults(state).StartDockerAfter);
    }

    [Fact]
    public void Warns_what_shutting_down_wsl_closes()
    {
        var model = Build(State(true, "Ubuntu", "docker-desktop"), c => c with { When = CompactWhen.RightAway });

        Assert.Equal(
            [
                "This shuts down WSL and closes:",
                "Ubuntu – open terminals, VS Code and Claude Code sessions",
                "Docker Desktop – 4 containers running",
                "Unsaved work in them is lost.",
            ],
            model.Warning);
    }

    [Fact]
    public void Says_when_nothing_would_be_closed()
    {
        var model = Build(State(dockerRunning: false), c => c with { When = CompactWhen.RightAway });

        Assert.Equal(["Nothing is running in WSL right now, so nothing gets closed."], model.Warning);
    }

    [Fact]
    public void Says_when_a_disk_hasnt_been_measured()
    {
        var state = State();
        state = state with { Snapshot = Machine.Scan() };

        var model = Build(state);

        Assert.Equal("Docker 129 GiB, not measured yet · Ubuntu 60 GiB, not measured yet", model.CompactDetail);
        Assert.Equal("Compacts Docker and Ubuntu", model.Summary);
    }

    [Fact]
    public void Heads_each_volume_group()
    {
        var lines = State().VolumeGroups.Select(g => CleanupLayout.GroupLine(g, Invariant)).ToList();

        Assert.Equal("acme · folder deleted (~/git/acme)", lines[0].Text);
        Assert.True(lines[0].Warning);
        Assert.Equal([new VolumeLine("acme_sqlserver", "…_sqlserver", "3.0 GiB"), new VolumeLine("acme_vscode", "…_vscode", "2.0 GiB")], lines[0].Volumes);
        Assert.Equal("notes · folder exists (~/git/notes)", lines[1].Text);
        Assert.False(lines[1].Warning);
    }

    [Theory]
    [InlineData(FolderCheck.NoMatch, null, "old · no matching folder in ~/git")]
    [InlineData(FolderCheck.NotChecked, "/home/me/git/old", "old · ~/git/old not checked, Ubuntu isn't running")]
    [InlineData(FolderCheck.NoProject, null, "No project")]
    public void Words_the_less_certain_groups(FolderCheck check, string? path, string expected)
    {
        var group = new VolumeGroup(
            check == FolderCheck.NoProject ? null : "old",
            check,
            path is null ? null : new ProjectFolder("Ubuntu", path),
            false,
            [],
            [new ProjectFolder("Ubuntu", "/home/me/git")]);

        Assert.Equal(expected, CleanupLayout.GroupLine(group, Invariant).Text);
    }

    [Theory]
    [InlineData("acme_sqlserver", "acme", "…_sqlserver")]
    [InlineData("f24829f44cff0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293", null, "f24829f44cff…")]
    [InlineData("vscode", null, "vscode")]
    public void Shortens_volume_names(string name, string? project, string label) =>
        Assert.Equal(label, CleanupLayout.VolumeLabel(new DockerVolume(name, 0, project)));
}
