using DiskTray.Core.Detection;

namespace DiskTray.Core.Tests;

public class WindowsPathTests
{
    [Theory]
    [InlineData(@"\\?\C:\Users\me\wsl", @"C:\Users\me\wsl")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void Strips_the_long_path_prefix(string path, string expected) =>
        Assert.Equal(expected, WindowsPath.StripLongPathPrefix(path));

    [Fact]
    public void Joins_with_backslashes_on_any_os() =>
        Assert.Equal(@"C:\a\b\c.vhdx", WindowsPath.Join(@"C:\a\", @"\b", "c.vhdx"));

    [Theory]
    [InlineData(@"c:\x\y.vhdx", @"C:\")]
    [InlineData(@"D:\y.vhdx", @"D:\")]
    [InlineData(@"\\server\share\y.vhdx", null)]
    [InlineData("relative", null)]
    public void Finds_the_drive_root(string path, string? expected) =>
        Assert.Equal(expected, WindowsPath.GetDriveRoot(path));
}

public class WslDistrosTests
{
    [Fact]
    public void Defaults_the_disk_file_name_and_strips_the_prefix()
    {
        var distros = WslDistros.FromRegistry(new([new("{a}", "Ubuntu", @"\\?\C:\distros\ubuntu", "", 0xf)], null));

        Assert.Equal(@"C:\distros\ubuntu\ext4.vhdx", Assert.Single(distros).VhdxPath);
    }

    [Fact]
    public void Uses_a_custom_disk_file_name()
    {
        var distros = WslDistros.FromRegistry(new([new("{a}", "Arch", @"D:\wsl\arch", "arch.vhdx", 0xf)], null));

        Assert.Equal(@"D:\wsl\arch\arch.vhdx", Assert.Single(distros).VhdxPath);
    }

    [Fact]
    public void Skips_wsl1_and_incomplete_entries()
    {
        var distros = WslDistros.FromRegistry(new(
            [
                new("{a}", "Legacy", @"C:\legacy", null, 0x7),
                new("{b}", null, @"C:\nameless", null, 0xf),
                new("{c}", "NoPath", null, null, 0xf),
                new("{d}", "Kept", @"C:\kept", null, 0xf),
                // No Flags value: assumed to be WSL 2, which is what's installed now.
                new("{e}", "NoFlags", @"C:\noflags", null, null),
            ],
            null));

        Assert.Equal(["Kept", "NoFlags"], distros.Select(d => d.Name));
    }

    [Theory]
    [InlineData("docker-desktop", true)]
    [InlineData("docker-desktop-data", true)]
    [InlineData("Ubuntu", false)]
    [InlineData("my-docker-desktop", false)]
    public void Recognises_docker_desktop_distros(string name, bool dockerOwned) =>
        Assert.Equal(dockerOwned, new WslDistro("{a}", name, @"C:\x\ext4.vhdx").IsDockerOwned);
}

public class DockerDesktopTests
{
    private const string LocalAppData = @"C:\Users\me\AppData\Local";
    private const string AppData = @"C:\Users\me\AppData\Roaming";

    [Fact]
    public void Defaults_to_local_app_data() =>
        Assert.Equal(
            @"C:\Users\me\AppData\Local\Docker\wsl\disk\docker_data.vhdx",
            DockerDesktop.DataDiskPath(new FakeDiskProbe(), LocalAppData, AppData));

    [Fact]
    public void Reads_the_custom_folder_from_settings_store()
    {
        var probe = new FakeDiskProbe();
        probe.Texts[AppData + @"\Docker\settings-store.json"] = """{ "CustomWslDistroDir": "D:\\docker\\wsl" }""";
        probe.Texts[AppData + @"\Docker\settings.json"] = """{ "customWslDistroDir": "E:\\ignored" }""";

        Assert.Equal(@"D:\docker\wsl\disk\docker_data.vhdx", DockerDesktop.DataDiskPath(probe, LocalAppData, AppData));
    }

    [Fact]
    public void Reads_a_custom_folder_written_with_forward_slashes()
    {
        var probe = new FakeDiskProbe();
        probe.Texts[AppData + @"\Docker\settings-store.json"] = """{ "CustomWslDistroDir": "D:/docker/wsl" }""";

        string path = DockerDesktop.DataDiskPath(probe, LocalAppData, AppData);

        Assert.Equal(@"D:\docker\wsl\disk\docker_data.vhdx", path);
        Assert.True(DiskTray.Core.Compact.CompactorArgs.IsVhdx(path));
    }

    [Fact]
    public void Falls_back_to_the_legacy_settings_file()
    {
        var probe = new FakeDiskProbe();
        probe.Texts[AppData + @"\Docker\settings-store.json"] = """{ "CustomWslDistroDir": "" }""";
        probe.Texts[AppData + @"\Docker\settings.json"] = """{ "customWslDistroDir": "E:\\docker" }""";

        Assert.Equal(@"E:\docker\disk\docker_data.vhdx", DockerDesktop.DataDiskPath(probe, LocalAppData, AppData));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "CustomWslDistroDir": 42 }""")]
    [InlineData("""{ "Other": "x" }""")]
    public void Ignores_unusable_settings(string json) => Assert.Null(DockerDesktop.ReadDistroDir(json));
}

public class DiskScannerTests
{
    [Fact]
    public void Finds_docker_data_and_the_default_distro()
    {
        var snapshot = Machine.Scan();

        Assert.Equal(Machine.DockerData, snapshot.Docker?.Path);
        Assert.Equal(Machine.DockerSize, snapshot.Docker?.SizeBytes);
        Assert.Equal("Ubuntu", snapshot.PrimaryDistro?.Name);
        Assert.Equal(Machine.UbuntuSize, snapshot.PrimaryDistro?.SizeBytes);
        Assert.Equal(Machine.DriveFree, snapshot.SystemDrive.FreeBytes);
        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.TakenAt);
    }

    [Fact]
    public void Leaves_docker_desktops_own_distro_out()
    {
        var names = Machine.Scan().Files.Select(f => f.Name);

        Assert.Equal(["Docker data", "Ubuntu", "wsl-vpnkit"], names);
    }

    [Fact]
    public void Picks_the_largest_distro_when_the_default_is_missing()
    {
        var registry = Machine.Registry() with { DefaultDistributionId = "{gone}" };
        var probe = Machine.Probe();
        probe.Files[@"C:\Users\me\wsl-vpnkit\ext4.vhdx"] = Machine.UbuntuSize + 1;

        Assert.Equal("wsl-vpnkit", Machine.Scan(probe, registry).PrimaryDistro?.Name);
    }

    [Fact]
    public void Skips_disk_files_that_dont_exist()
    {
        var probe = Machine.Probe();
        probe.Files.Remove(Machine.DockerData);
        probe.Files.Remove(Machine.UbuntuBase + @"\ext4.vhdx");

        var snapshot = Machine.Scan(probe);

        Assert.Null(snapshot.Docker);
        Assert.Equal("wsl-vpnkit", snapshot.PrimaryDistro?.Name);
    }

    [Fact]
    public void Uses_the_legacy_docker_data_distro()
    {
        var probe = Machine.Probe();
        probe.Files.Remove(Machine.DockerData);
        probe.Files[@"C:\Users\me\AppData\Local\Docker\wsl\data\ext4.vhdx"] = 42;
        var registry = Machine.Registry();
        registry = registry with
        {
            Entries = [.. registry.Entries, new("{33333333-0000-0000-0000-000000000000}", "docker-desktop-data", @"\\?\C:\Users\me\AppData\Local\Docker\wsl\data", null, 0xf)],
        };

        var docker = Machine.Scan(probe, registry).Docker;

        Assert.Equal(@"C:\Users\me\AppData\Local\Docker\wsl\data\ext4.vhdx", docker?.Path);
        Assert.Equal(42, docker?.SizeBytes);
    }

    [Fact]
    public void Notes_docker_desktops_own_disks_without_showing_them()
    {
        var snapshot = Machine.Scan();

        Assert.Equal([@"C:\Users\me\AppData\Local\Docker\wsl\main\ext4.vhdx"], snapshot.DockerSystemDisks);
        Assert.DoesNotContain(snapshot.Files, f => f.Path.Contains(@"\main\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Measures_other_drives_that_hold_disk_files()
    {
        var probe = Machine.Probe();
        probe.Texts[Machine.AppData + @"\Docker\settings-store.json"] = """{ "CustomWslDistroDir": "D:\\docker" }""";
        probe.Files[@"D:\docker\disk\docker_data.vhdx"] = 5;
        probe.Drives[@"D:\"] = new DriveSpace(@"D:\", 1000, 400);

        var snapshot = Machine.Scan(probe);

        Assert.Equal(400, snapshot.DriveOf(snapshot.Docker!)?.FreeBytes);
        Assert.Equal(@"C:\", snapshot.SystemDrive.Root);
    }

    [Fact]
    public void Fails_when_the_system_drive_cant_be_read()
    {
        var probe = Machine.Probe();
        probe.Drives.Clear();

        Assert.Throws<InvalidOperationException>(() => Machine.Scan(probe));
    }
}
