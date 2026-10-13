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
    [Fact]
    public void Inspects_containers_in_batches_short_enough_for_one_command_line()
    {
        var ids = Enumerable.Range(0, 250).Select(i => i.ToString("x64")).ToList();

        var commands = DockerOutput.InspectCommands(ids);

        Assert.Equal(3, commands.Count);
        Assert.All(commands, c => Assert.Equal("inspect", c[0]));
        Assert.Equal(ids, commands.SelectMany(c => c.Skip(1)));
        Assert.All(commands, c => Assert.True(string.Join(' ', c).Length < 8000));
    }

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
    [InlineData("""{"Status":"starting"}""", true)]
    [InlineData("""{"Status":"paused"}""", true)]
    [InlineData("""{"Status":"stopped"}""", false)]
    [InlineData("""{"Status":""}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("error during connect", null)]
    public void Reads_docker_desktop_status(string output, bool? running) =>
        Assert.Equal(running, DockerOutput.ParseDesktopStatus(output));

    [Fact]
    public void Docker_desktop_isnt_running_when_it_isnt_installed() =>
        Assert.False(DockerOutput.DesktopRunning(installed: false, null, null));

    [Theory]
    [InlineData("""{"Status":"running"}""", null, true)]
    [InlineData("""{"Status":"stopped"}""", "docker-desktop", false)]
    // The status can't be read: Docker's own distros running says it is.
    [InlineData(null, "docker-desktop,Ubuntu", true)]
    [InlineData(null, "Ubuntu", false)]
    [InlineData("not json", "", false)]
    public void Falls_back_to_dockers_distros_when_its_status_cant_be_read(string? status, string? distros, bool running) =>
        Assert.Equal(running, DockerOutput.DesktopRunning(true, status, distros?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet()));

    [Fact]
    public void Doesnt_know_whether_docker_desktop_runs_when_nothing_answers() =>
        Assert.Null(DockerOutput.DesktopRunning(true, null, null));

    [Theory]
    [InlineData("Deleted Images:\ndeleted: sha256:abc\n\nTotal reclaimed space: 1.5GB\n", 1_500_000_000L)]
    [InlineData("ID\tRECLAIMABLE\tSIZE\nabc\ttrue\t2MB\nTotal:\t3.2GB\n", 3_200_000_000L)]
    [InlineData("Total reclaimed space: 0B", 0L)]
    [InlineData("acme_sqlserver\n", null)]
    public void Reads_what_a_prune_freed(string output, long? bytes) =>
        Assert.Equal(bytes, DockerOutput.ParseReclaimed(output));

    [Fact]
    public void Reads_links_and_flags_that_arent_quoted()
    {
        var usage = DockerOutput.ParseDiskUsage("""
            {"BuildCache":[{"InUse":false,"Shared":false,"Size":"1GB"}],
             "Images":[],
             "Volumes":[{"Name":"a","Links":0,"Size":"2GB","Labels":""},{"Name":"b","Links":1,"Size":"3GB","Labels":""}]}
            """);

        Assert.Equal(1_000_000_000L, usage!.BuildCacheBytes);
        Assert.Equal(["a"], usage.UnusedVolumes.Select(v => v.Name));
    }

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
    public void Checks_a_project_in_a_windows_folder_the_same_way()
    {
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["acmeweb-app_devcontainer"] = new(null, @"C:\code\acme.web-app") }, DateTimeOffset.UnixEpoch);
        var withFolder = Folders();
        withFolder.Existing.Add(@"C:\code\acme.web-app");

        var gone = Group(Folders(), cache).Single(g => g.Project == "acmeweb-app_devcontainer");
        var there = Group(withFolder, cache).Single(g => g.Project == "acmeweb-app_devcontainer");

        Assert.Equal((FolderCheck.Deleted, true), (gone.Check, gone.PreSelected));
        Assert.Equal((FolderCheck.Exists, false), (there.Check, there.PreSelected));
        Assert.Equal(new ProjectFolder(null, @"C:\code\acme.web-app"), there.Folder);
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

    /// <summary>A Compose project run in some distro with only named volumes, so nothing says which distro.</summary>
    private static readonly IReadOnlyList<DockerContainer> Postgres = DockerOutput.ParseContainers("""
        [{"Name":"/pg-db-1","State":{"Status":"exited"},
          "Config":{"Labels":{"com.docker.compose.project":"pg","com.docker.compose.project.working_dir":"/home/me/pg"}},
          "Mounts":[{"Type":"volume","Name":"pg_data","Source":"/var/lib/docker/volumes/pg_data/_data"}]}]
        """);

    [Fact]
    public void Marks_a_folder_whose_distro_was_assumed()
    {
        var folders = VolumeConfidence.FoldersFromContainers([.. Containers, .. Postgres], "Ubuntu");

        Assert.True(folders["pg"].DistroGuessed);
        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/pg"), folders["pg"]);
        Assert.False(folders["scratch"].DistroGuessed);
        Assert.False(folders["billing-api_devcontainer"].DistroGuessed);
    }

    [Fact]
    public void Never_pre_selects_a_project_whose_distro_was_assumed()
    {
        // The project lives in Debian; Ubuntu, the default, is running and has no such folder.
        var unused = new List<DockerVolume> { new("pg_data", 5L << 30, "pg", "data") };
        var groups = VolumeConfidence.Group(unused, VolumeConfidence.FoldersFromContainers(Postgres, "Ubuntu"), new ProjectFolderCache(), [], Folders());

        Assert.Equal(FolderCheck.NotChecked, Assert.Single(groups).Check);
        Assert.False(groups[0].PreSelected);
    }

    [Fact]
    public void Still_finds_an_assumed_folder_that_is_there()
    {
        var unused = new List<DockerVolume> { new("pg_data", 5L << 30, "pg", "data") };
        var folders = Folders();
        folders.Existing.Add("Ubuntu:/home/me/pg");

        var groups = VolumeConfidence.Group(unused, VolumeConfidence.FoldersFromContainers(Postgres, "Ubuntu"), new ProjectFolderCache(), [], folders);

        Assert.Equal(FolderCheck.Exists, Assert.Single(groups).Check);
    }

    [Fact]
    public void Prefers_a_remembered_recorded_folder_over_an_assumed_one_from_now()
    {
        var unused = new List<DockerVolume> { new("pg_data", 5L << 30, "pg", "data") };
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new("Ubuntu", "/home/me/old/pg") }, DateTimeOffset.UnixEpoch);
        var folders = Folders();
        folders.Existing.Add("Ubuntu:/home/me/old/pg");

        var group = Assert.Single(VolumeConfidence.Group(unused, VolumeConfidence.FoldersFromContainers(Postgres, "Ubuntu"), cache, [], folders));

        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/old/pg"), group.Folder);
        Assert.Equal(FolderCheck.Exists, group.Check);
    }

    [Fact]
    public void Doesnt_call_a_project_deleted_while_its_containers_point_somewhere_it_may_be()
    {
        // Remembered at old/pg, which is gone; today's containers say /home/me/pg, in a distro
        // that wasn't recorded and where it isn't, so it may be in another one.
        var unused = new List<DockerVolume> { new("pg_data", 5L << 30, "pg", "data") };
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new("Ubuntu", "/home/me/old/pg") }, DateTimeOffset.UnixEpoch);

        var group = Assert.Single(VolumeConfidence.Group(unused, VolumeConfidence.FoldersFromContainers(Postgres, "Ubuntu"), cache, [], Folders()));

        Assert.Equal(FolderCheck.NotChecked, group.Check);
        Assert.True(group.FolderGone);
        Assert.False(group.PreSelected);
        Assert.Equal("pg · ~/old/pg is gone, but the project may be somewhere else", CleanupLayout.GroupLine(group).Text);
    }

    [Fact]
    public void Finds_a_project_at_the_folder_its_containers_point_to_now()
    {
        var unused = new List<DockerVolume> { new("pg_data", 5L << 30, "pg", "data") };
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new("Ubuntu", "/home/me/old/pg") }, DateTimeOffset.UnixEpoch);
        var folders = Folders();
        folders.Existing.Add("Ubuntu:/home/me/pg");

        var group = Assert.Single(VolumeConfidence.Group(unused, VolumeConfidence.FoldersFromContainers(Postgres, "Ubuntu"), cache, [], folders));

        Assert.Equal(FolderCheck.Exists, group.Check);
        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/pg"), group.Folder);
    }

    [Fact]
    public void Doesnt_call_a_project_deleted_once_its_cloned_again_somewhere_else()
    {
        // Remembered at ~/git/acme.web-app, since deleted, and cloned again into ~/work,
        // a code folder, with no containers in between.
        var folders = Folders();
        folders.Existing.Add("Ubuntu:/home/me/work/acme.web-app");
        var groups = VolumeConfidence.Group(Unused, VolumeConfidence.FoldersFromContainers(Containers, "Ubuntu"), Cache(), [new ProjectFolder("Ubuntu", "/home/me/work")], folders);

        var acme = groups.Single(g => g.Project == "acmeweb-app_devcontainer");
        Assert.Equal(FolderCheck.Exists, acme.Check);
        Assert.True(acme.Guessed);
        Assert.Equal(new ProjectFolder("Ubuntu", "/home/me/work/acme.web-app"), acme.Folder);
        Assert.DoesNotContain(groups, g => g.PreSelected);
    }

    [Fact]
    public void Doesnt_call_a_project_deleted_when_its_other_folder_cant_be_checked()
    {
        // Recorded in Ubuntu (gone); the containers now say Debian, which isn't running.
        var unused = new List<DockerVolume> { new("pg_data", 5L << 30, "pg", "data") };
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new("Ubuntu", "/home/me/old/pg") }, DateTimeOffset.UnixEpoch);
        var live = new Dictionary<string, ProjectFolder> { ["pg"] = new("Debian", "/home/me/pg") };

        var group = Assert.Single(VolumeConfidence.Group(unused, live, cache, [], Folders()));

        Assert.Equal(FolderCheck.NotChecked, group.Check);
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

    [Fact]
    public void Never_lets_an_assumed_distro_replace_a_recorded_one()
    {
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new("Debian", "/home/me/pg") }, DateTimeOffset.UnixEpoch);

        Assert.False(cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new ProjectFolder("Ubuntu", "/home/me/pg") { DistroGuessed = true } }, DateTimeOffset.UnixEpoch));
        Assert.Equal(new ProjectFolder("Debian", "/home/me/pg"), cache.Get("pg"));
    }

    [Fact]
    public void Lets_a_recorded_distro_replace_an_assumed_one_and_remembers_which_is_which()
    {
        var cache = new ProjectFolderCache();
        cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new ProjectFolder("Ubuntu", "/home/me/pg") { DistroGuessed = true } }, DateTimeOffset.UnixEpoch);
        Assert.True(ProjectFolderCache.FromJson(cache.ToJson()).Get("pg")!.DistroGuessed);

        // Same folder, but now a bind mount says the distro.
        Assert.True(cache.Record(new Dictionary<string, ProjectFolder> { ["pg"] = new("Ubuntu", "/home/me/pg") }, DateTimeOffset.UnixEpoch));
        Assert.False(ProjectFolderCache.FromJson(cache.ToJson()).Get("pg")!.DistroGuessed);
    }

    [Fact]
    public void Treats_distros_saved_before_guesses_were_marked_as_assumed()
    {
        var cache = ProjectFolderCache.FromJson("""
            {"pg":{"distro":"Ubuntu","path":"/home/me/pg","seenAt":"2026-10-01T00:00:00+00:00"},
             "win":{"distro":null,"path":"C:\\code\\win","seenAt":"2026-10-01T00:00:00+00:00"}}
            """);

        Assert.True(cache.Get("pg")!.DistroGuessed);
        Assert.False(cache.Get("win")!.DistroGuessed);
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
        // Compacting when idle: the user quits Docker, so it isn't started again unasked.
        Assert.False(choices.StartDockerAfter);
    }

    [Theory]
    [InlineData(CompactWhen.RightAway, true, true, true)]
    [InlineData(CompactWhen.WhenIdle, true, true, false)]
    [InlineData(CompactWhen.RightAway, false, true, false)]
    [InlineData(CompactWhen.RightAway, true, false, false)]
    public void Starts_docker_again_by_default_only_after_stopping_it_itself(CompactWhen when, bool setting, bool dockerRunning, bool expected) =>
        Assert.Equal(expected, CleanupChoices.StartDockerDefault(State(dockerRunning), new CleanupDefaults(StartDockerAfter: setting), when));

    [Fact]
    public void Has_the_wait_for_idle_watch_docker_desktops_own_disk_too()
    {
        var state = State();

        var request = CleanupRequest.From(state, CleanupChoices.Defaults(state), Build(state), dryRun: false);

        Assert.Equivalent(
            new[] { Machine.DockerData, Machine.UbuntuBase + @"\ext4.vhdx", @"C:\Users\me\wsl-vpnkit\ext4.vhdx", @"C:\Users\me\AppData\Local\Docker\wsl\main\ext4.vhdx" },
            request.WslDisks,
            strict: true);
    }

    [Fact]
    public void Starts_with_what_settings_say()
    {
        var choices = CleanupChoices.Defaults(
            State(),
            new CleanupDefaults(BuildCache: false, PickDeletedProjectVolumes: false, Compact: false, When: CompactWhen.RightAway, StartDockerAfter: false));

        Assert.True(choices.Prune);
        Assert.False(choices.BuildCache);
        Assert.True(choices.DanglingImages);
        Assert.Empty(choices.Volumes);
        Assert.False(choices.Compact);
        Assert.Equal(CompactWhen.RightAway, choices.When);
        Assert.False(choices.StartDockerAfter);
    }

    [Fact]
    public void Leaves_prune_off_when_settings_pick_nothing_to_prune() =>
        Assert.False(CleanupChoices.Defaults(State(), new CleanupDefaults(false, false, false)).Prune);

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
        Assert.Equal("1.0 GiB", model.PruneTotal);
    }

    [Fact]
    public void Never_expects_a_disk_to_end_up_below_nothing()
    {
        var docker = State().Snapshot.Docker!;

        // Pruning more than the disk says it uses (the estimates are rough) leaves it at zero.
        var target = CleanupLayout.Targets(State().Snapshot, docker.UsedBytes!.Value + (10L << 30)).Single(t => t.File.Kind == DiskKind.DockerData);

        Assert.Equal(0, target.AfterBytes);
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
    public void Says_when_it_couldnt_tell_whats_running()
    {
        var model = Build(State(false) with { RunningKnown = false }, c => c with { When = CompactWhen.RightAway });

        Assert.Contains("couldn't tell what's running", Assert.Single(model.Warning));
    }

    [Fact]
    public void Knows_docker_desktop_runs_even_when_it_didnt_say_what_to_prune()
    {
        var state = State(false) with { DesktopRunning = true };
        var model = Build(state, c => c with { When = CompactWhen.RightAway });

        Assert.Contains("didn't say what can be pruned", model.PruneUnavailable);
        Assert.Contains(model.Warning, w => w.StartsWith("Docker Desktop", StringComparison.Ordinal));
        Assert.True(CleanupChoices.Defaults(state, new CleanupDefaults(When: CompactWhen.RightAway)).StartDockerAfter);
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

public class RunProgressTests
{
    [Fact]
    public void Drops_reports_still_queued_when_the_run_finishes()
    {
        var queued = new Queue<Action>();
        var seen = new List<string>();
        var progress = new RunProgress<string>(queued.Enqueue, seen.Add);

        progress.Report("pruning");
        queued.Dequeue()();
        // The run reports one last step and returns before the UI thread gets to it.
        progress.Report("waiting for admin");
        progress.Done();
        while (queued.Count > 0)
        {
            queued.Dequeue()();
        }

        Assert.Equal(["pruning"], seen);
    }
}

public sealed class FolderLookupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("disktray-folders-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Looks at folders in the running distro "Ubuntu" under a temp folder standing in for \\wsl.localhost.</summary>
    private FolderLookup Lookup(params string[] running) =>
        new(running.ToHashSet(StringComparer.OrdinalIgnoreCase), (distro, path) => Path.Combine(_root, distro) + path);

    private void Make(string path) => Directory.CreateDirectory(Path.Combine(_root, "Ubuntu") + path);

    [Fact]
    public void Finds_a_folder_that_is_there()
    {
        Make("/home/me/app");

        Assert.True(Lookup("Ubuntu").Exists(new ProjectFolder("Ubuntu", "/home/me/app")));
    }

    [Fact]
    public void Calls_a_folder_gone_only_when_its_parent_can_be_read_and_lacks_it()
    {
        Make("/home/me");

        Assert.False(Lookup("Ubuntu").Exists(new ProjectFolder("Ubuntu", "/home/me/app")));
        // No parent to read either: unknown, not gone.
        Assert.Null(Lookup("Ubuntu").Exists(new ProjectFolder("Ubuntu", "/srv/nothing/app")));
    }

    [Fact]
    public void Doesnt_look_in_a_distro_that_isnt_running()
    {
        Make("/home/me");

        Assert.Null(Lookup().Exists(new ProjectFolder("Ubuntu", "/home/me/app")));
        Assert.Null(Lookup().FolderNames(new ProjectFolder("Ubuntu", "/home/me")));
    }

    [Fact]
    public void Gives_up_on_a_look_that_hangs_and_on_that_distro_after_it()
    {
        using var stuck = new ManualResetEventSlim();
        int looks = 0;
        var lookup = new FolderLookup(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ubuntu", "Debian" },
            (distro, path) => distro + ":" + path,
            path =>
            {
                Interlocked.Increment(ref looks);
                if (path.StartsWith("Ubuntu:", StringComparison.Ordinal))
                {
                    stuck.Wait(TestContext.Current.CancellationToken);
                }

                return true;
            },
            _ => [],
            TimeSpan.FromMilliseconds(100));
        try
        {
            Assert.Null(lookup.Exists(new ProjectFolder("Ubuntu", "/home/me/app")));
            // Ubuntu isn't asked again, but other distros still are.
            Assert.Null(lookup.Exists(new ProjectFolder("Ubuntu", "/home/me/other")));
            Assert.Null(lookup.FolderNames(new ProjectFolder("Ubuntu", "/home/me")));
            Assert.True(lookup.Exists(new ProjectFolder("Debian", "/home/me/app")));
            Assert.Equal(2, looks);
        }
        finally
        {
            stuck.Set();
        }
    }

    [Fact]
    public void Looks_at_a_windows_folder_directly()
    {
        string app = Directory.CreateDirectory(Path.Combine(_root, "code", "app")).FullName;

        Assert.True(Lookup().Exists(new ProjectFolder(null, app)));
        Assert.False(Lookup().Exists(new ProjectFolder(null, Path.Combine(_root, "code", "gone"))));
        Assert.Equal(["app"], Lookup().FolderNames(new ProjectFolder(null, Path.Combine(_root, "code"))));
    }

    [Fact]
    public void Doesnt_call_a_folder_gone_when_a_link_or_file_has_its_name()
    {
        Make("/home/me");
        string home = Path.Combine(_root, "Ubuntu") + "/home/me";
        // A link to a folder that isn't reachable from here, and a file, as \\wsl.localhost can show Linux links.
        Directory.CreateSymbolicLink(Path.Combine(home, "linked"), "/nowhere/at/all");
        File.WriteAllText(Path.Combine(home, "plain"), "");

        // Never gone. Windows counts a folder link as a folder even when what it points to
        // isn't there, so there it's "exists"; elsewhere it can't be told.
        Assert.NotEqual(false, Lookup("Ubuntu").Exists(new ProjectFolder("Ubuntu", "/home/me/linked")));
        Assert.Null(Lookup("Ubuntu").Exists(new ProjectFolder("Ubuntu", "/home/me/plain")));
        Assert.Equal(["linked", "plain"], Lookup("Ubuntu").FolderNames(new ProjectFolder("Ubuntu", "/home/me"))!.Order(StringComparer.Ordinal));
    }
}
