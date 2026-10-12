using System.Globalization;
using DiskTray.Core.Detection;
using DiskTray.Core.Flyout;
using DiskTray.Core.Settings;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Tests;

public class TrayPinTests
{
    [Fact]
    public void Pins_docker_the_distro_and_the_drive_by_default() =>
        Assert.Equal([PinKind.Docker, PinKind.Distro, PinKind.Drive], TrayPin.Defaults.Select(p => p.Kind));

    [Fact]
    public void Keeps_at_most_three_and_no_repeats()
    {
        var pins = TrayPin.Clean(
        [
            new(PinKind.Path, @"C:\a.vhdx"),
            new(PinKind.Path, @"c:\A.VHDX"),
            new(PinKind.Docker),
            null,
            new(PinKind.Drive),
            new(PinKind.Distro),
        ]);

        Assert.Equal([PinKind.Path, PinKind.Docker, PinKind.Drive], pins.Select(p => p.Kind));
    }

    [Fact]
    public void Drops_bad_values()
    {
        var pins = TrayPin.Clean(
        [
            new(PinKind.Path, " "),
            new(PinKind.Drive, null, "  ", "orange", IconShows.ShareOfDrive),
            new(PinKind.Docker, null, " Mine ", "#a1b2c3", IconShows.UsedVsFree),
        ]);

        Assert.Equal([new TrayPin(PinKind.Drive), new TrayPin(PinKind.Docker, null, "Mine", "#A1B2C3")], pins);
    }

    [Fact]
    public void Falls_back_to_the_defaults_when_nothing_is_left() =>
        Assert.Equal(TrayPin.Defaults, TrayPin.Clean([new TrayPin(PinKind.Path)]));

    [Fact]
    public void Round_trips_through_settings()
    {
        var settings = new AppSettings
        {
            IconShape = IconShape.Bar,
            Pins = [new(PinKind.Path, @"C:\vm\disk.vhdx", "VM", "#3FB950", IconShows.ShareOfDrive), new(PinKind.Drive, @"D:\")],
        };

        string json = settings.ToJson();

        Assert.Equal(settings, AppSettings.FromJson(json));
        Assert.Contains("\"kind\": \"path\"", json);
        Assert.Contains("\"shows\": \"shareOfDrive\"", json);
        Assert.Contains("\"iconShape\": \"bar\"", json);
    }

    [Fact]
    public void Reads_settings_without_pins_as_the_defaults() =>
        Assert.Equal(TrayPin.Defaults, AppSettings.FromJson("""{ "pins": [] }""").Pins);
}

public class PinnedItemTests
{
    private const string VmDisk = @"C:\vm\disk.vhdx";
    private const string Projects = @"C:\Users\me\projects";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1).AddHours(12);

    private static DiskSnapshot Scan(IReadOnlyList<TrayPin> pins, Action<FakeDiskProbe, FakeFolderSizes>? setUp = null)
    {
        var probe = Machine.Probe();
        var folders = new FakeFolderSizes();
        probe.Files[VmDisk] = 10L << 30;
        probe.Folders.Add(Projects);
        probe.Drives[@"D:\"] = new DriveSpace(@"D:\", 1000L << 30, 900L << 30);
        setUp?.Invoke(probe, folders);
        return new DiskScanner(probe, new FakeWslRegistry(Machine.Registry()), Machine.Paths, new FixedClock(Now), folders).Scan(pins);
    }

    private static IReadOnlyList<TrayIconModel> Build(DiskSnapshot snapshot, IReadOnlyList<TrayPin> pins) =>
        TrayLayout.Build(snapshot, new AlertThresholds(), CultureInfo.InvariantCulture, TimeZoneInfo.Utc, pins);

    [Fact]
    public void Keeps_the_usual_icon_keys_so_windows_remembers_them()
    {
        var icons = Build(Machine.Scan(), TrayPin.Defaults);

        Assert.Equal(["docker", Machine.UbuntuId, TrayLayout.DriveKey], icons.Select(i => i.Key));
    }

    [Fact]
    public void Shows_a_pinned_file_with_its_own_color_and_name()
    {
        TrayPin[] pins = [new(PinKind.Path, VmDisk, "Test VM"), new(PinKind.Drive)];
        var snapshot = Scan(pins);

        var icons = Build(snapshot, pins);

        Assert.Equal(DiskScanner.PathId(VmDisk), icons[0].Key);
        Assert.StartsWith("Test VM · 10 GiB\nC: 50 GiB free", icons[0].ToolTip);
        Assert.Equal([Palette.Extras[0].Main, Palette.Free], icons[0].Chart.Slices.Select(s => s.Color));
        Assert.Equal("Drive C: · 472 GiB\nTest VM 10 GiB · Other 411 GiB\nC: 50 GiB free (11%) – below 15% warning", icons[1].ToolTip);
    }

    [Fact]
    public void Shows_a_folder_once_its_size_is_added_up()
    {
        TrayPin[] pins = [new(PinKind.Path, Projects)];

        var waiting = Build(Scan(pins), pins)[0];
        var measured = Build(Scan(pins, (_, folders) => folders.Sizes[Projects] = new FolderSize(3L << 30, Now.AddHours(-1))), pins)[0];

        Assert.Equal("projects\nAdding up the folder's size…", waiting.ToolTip);
        Assert.Equal([Palette.Free], waiting.Chart.Slices.Select(s => s.Color));
        Assert.StartsWith("projects · 3.0 GiB as of 11:00\n", measured.ToolTip);
    }

    [Fact]
    public void Says_when_a_pinned_item_is_missing()
    {
        TrayPin[] pins = [new(PinKind.Path, @"C:\gone.vhdx"), new(PinKind.Distro, "{nope}"), new(PinKind.Drive, @"Q:\")];

        var icons = Build(Scan(pins), pins);

        Assert.Equal(@"gone.vhdx" + "\n" + @"C:\gone.vhdx wasn't found", icons[0].ToolTip);
        Assert.Equal("WSL distro\nThe WSL distro wasn't found", icons[1].ToolTip);
        Assert.Equal("Drive Q:\nThe drive isn't available", icons[2].ToolTip);
        Assert.Equal(["path:C:\\GONE.VHDX", "distro:{nope}", "drive:Q:\\"], icons.Select(i => i.Key));
    }

    [Fact]
    public void Shows_another_drive()
    {
        TrayPin[] pins = [new(PinKind.Drive, @"D:\")];

        var icon = Build(Scan(pins), pins)[0];

        Assert.Equal("Drive D: · 1000 GiB\nD: 900 GiB free (90%)", icon.ToolTip);
        Assert.Equal(AlertLevel.Normal, icon.Chart.Alert);
        Assert.Equal("drive:D:\\", icon.Key);
    }

    [Fact]
    public void Shows_a_disk_as_a_share_of_its_drive()
    {
        TrayPin[] pins = [new(PinKind.Docker, Shows: IconShows.ShareOfDrive)];

        var slices = Build(Machine.Scan(), pins)[0].Chart.Slices;

        Assert.Equal([Palette.Docker.Main, Palette.Other, Palette.Free], slices.Select(s => s.Color));
        Assert.Equal((double)Machine.DockerSize / Machine.DriveTotal, slices[0].Fraction, 9);
    }

    [Fact]
    public void Shows_a_drive_as_used_and_free()
    {
        TrayPin[] pins = [new(PinKind.Docker), new(PinKind.Drive, Shows: IconShows.UsedVsFree, Color: "#112233")];

        var icon = Build(Machine.Scan(), pins)[1];

        Assert.Equal([Rgb.FromHex("#112233"), Palette.Free], icon.Chart.Slices.Select(s => s.Color));
        Assert.Equal("Drive C: · 472 GiB\nC: 50 GiB free (11%) – below 15% warning", icon.ToolTip);
    }

    [Fact]
    public void Leaves_unpinned_disks_in_windows_and_other()
    {
        TrayPin[] pins = [new(PinKind.Docker), new(PinKind.Drive)];

        var drive = Build(Machine.Scan(), pins)[1];

        Assert.Equal([Palette.Docker.Main, Palette.Other, Palette.Free], drive.Chart.Slices.Select(s => s.Color));
        Assert.Contains("Docker 129 GiB · Other 293 GiB", drive.ToolTip);
    }

    [Fact]
    public void Uses_a_picked_color_and_its_tint()
    {
        TrayPin[] pins = [new(PinKind.Distro, Color: "#3FB950")];
        var snapshot = Machine.Scan() with
        {
            Files = Machine.Scan().Files.Select(f => f.Id == Machine.UbuntuId ? f with { UsedBytes = 35L << 30 } : f).ToList(),
        };

        var slices = Build(snapshot, pins)[0].Chart.Slices;

        Assert.Equal([Rgb.FromHex("#3FB950"), Rgb.FromHex("#3FB950").Mix(Rgb.White, 0.5), Palette.Free], slices.Select(s => s.Color));
    }

    [Fact]
    public void Flyout_lists_the_pinned_items_on_the_system_drive()
    {
        TrayPin[] pins = [new(PinKind.Path, VmDisk, "Test VM"), new(PinKind.Docker, Name: "Containers"), new(PinKind.Drive, Color: "#123456")];

        var flyout = FlyoutLayout.Build(Scan(pins), new AlertThresholds(), SurfaceTheme.Light, Now, TimeSpan.FromMinutes(5), null, CultureInfo.InvariantCulture, TimeZoneInfo.Utc, pins);

        Assert.Equal(["Containers", "Test VM", "Windows & other", "Free"], flyout.Legend.Select(l => l.Name));
        Assert.Equal(Rgb.FromHex("#123456"), flyout.Legend[2].Color);
        Assert.Null(flyout.Legend[1].Detail);
    }

    [Fact]
    public void Clean_up_still_compacts_docker_and_the_main_distro_whatever_is_pinned() =>
        Assert.Equal(["docker", Machine.UbuntuId], TrayLayout.CompactableFiles(Machine.Scan()).Select(f => f.Id));

    [Fact]
    public void Clean_up_also_compacts_another_distro_once_its_shown_as_reclaimable()
    {
        var snapshot = Machine.Scan();
        snapshot = snapshot with { Files = snapshot.Files.Select(f => f.Name == "wsl-vpnkit" ? f with { UsedBytes = f.SizeBytes / 2 } : f).ToList() };

        Assert.Equal(["docker", Machine.UbuntuId, "{11111111-0000-0000-0000-000000000000}"], TrayLayout.CompactableFiles(snapshot).Select(f => f.Id));
    }

    [Fact]
    public void Gives_two_pins_for_the_same_distro_one_icon()
    {
        var items = TrayLayout.Resolve(Machine.Scan(), [new TrayPin(PinKind.Distro), new TrayPin(PinKind.Distro, Machine.UbuntuId, "Again")]);

        Assert.Equal(Machine.UbuntuId, Assert.Single(items).Key);
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me")]
    [InlineData(@"\\server\share\big.iso")]
    [InlineData(@"\\?\C:\VMs")]
    [InlineData("relative\\path")]
    public void Drops_pins_outside_this_pcs_drives(string path) =>
        Assert.Equal(TrayPin.Defaults, TrayPin.Clean([new TrayPin(PinKind.Path, path)]));

    [Fact]
    public void Keeps_pins_on_a_drive_letter() =>
        Assert.Equal(@"D:\VMs", Assert.Single(TrayPin.Clean([new TrayPin(PinKind.Path, @"D:\VMs")])).Target);
}
