using DiskTray.Core.Detection;

namespace DiskTray.Core.Tests;

internal sealed class FakeDiskProbe : IDiskProbe
{
    public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, DriveSpace> Drives { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long? GetFileSize(string path) => Files.TryGetValue(path, out long size) ? size : null;

    public DriveSpace? GetDriveSpace(string root) => Drives.GetValueOrDefault(root);

    public string? ReadAllText(string path) => Texts.GetValueOrDefault(path);
}

internal sealed class FakeWslRegistry(WslRegistryData data) : IWslRegistry
{
    public WslRegistryData Read() => data;
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>The real numbers from the machine DiskTray was designed on (October 2026).</summary>
internal static class Machine
{
    public const string LocalAppData = @"C:\Users\me\AppData\Local";
    public const string AppData = @"C:\Users\me\AppData\Roaming";
    public const string UbuntuId = "{aaaaaaaa-0000-0000-0000-000000000000}";
    public const string UbuntuBase = @"C:\Users\me\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState";
    public const string DockerData = @"C:\Users\me\AppData\Local\Docker\wsl\disk\docker_data.vhdx";

    public const long DriveTotal = 506_332_180_480;
    public const long DriveFree = 54_042_759_168;
    public const long DockerSize = 138_174_005_248;
    public const long UbuntuSize = 64_314_408_960;

    public static readonly ScanPaths Paths = new(@"C:\", LocalAppData, AppData);

    public static WslRegistryData Registry() => new(
        [
            new(UbuntuId, "Ubuntu", UbuntuBase, null, 2),
            new("{11111111-0000-0000-0000-000000000000}", "wsl-vpnkit", @"\\?\C:\Users\me\wsl-vpnkit", null, 2),
            new("{22222222-0000-0000-0000-000000000000}", "docker-desktop", @"\\?\C:\Users\me\AppData\Local\Docker\wsl\main", "ext4.vhdx", 2),
        ],
        UbuntuId);

    public static FakeDiskProbe Probe()
    {
        var probe = new FakeDiskProbe();
        probe.Drives[@"C:\"] = new DriveSpace(@"C:\", DriveTotal, DriveFree);
        probe.Files[DockerData] = DockerSize;
        probe.Files[UbuntuBase + @"\ext4.vhdx"] = UbuntuSize;
        probe.Files[@"C:\Users\me\wsl-vpnkit\ext4.vhdx"] = 103_809_024;
        probe.Files[@"C:\Users\me\AppData\Local\Docker\wsl\main\ext4.vhdx"] = 100_663_296;
        probe.Texts[AppData + @"\Docker\settings-store.json"] = """{ "CustomWslDistroDir": "C:\\Users\\me\\AppData\\Local\\Docker\\wsl", "DiskSizeMiB": 65536 }""";
        return probe;
    }

    public static DiskSnapshot Scan(FakeDiskProbe? probe = null, WslRegistryData? registry = null) =>
        new DiskScanner(probe ?? Probe(), new FakeWslRegistry(registry ?? Registry()), Paths, new FixedClock(DateTimeOffset.UnixEpoch)).Scan();
}
