using System.Globalization;
using DiskTray.Core.Detection;
using DiskTray.Core.Flyout;
using DiskTray.Core.Settings;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Tests;

public class LargeFileScanTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10).AddHours(12);

    private static LargeFileScan Scan() => new(
        Now.AddHours(-3),
        2L << 30,
        [
            new(Machine.DockerData, Machine.DockerSize),
            new(@"C:\Users\me\VMs\win11.vhdx", 40L << 30),
            new(@"C:\Users\me\Downloads\ubuntu.iso", 6L << 30),
            new(@"C:\Users\me\projects\data\dump.sql", 3L << 30),
            new(@"C:\Users\me\Videos\talk.mp4", 2L << 30),
        ]);

    [Fact]
    public void Lists_files_largest_first_leaving_out_known_disks_and_pins()
    {
        TrayPin[] pins = [new(PinKind.Docker), new(PinKind.Path, @"C:\Users\me\projects\")];

        var others = Scan().Others(Machine.Scan(), pins, 2L << 30);

        Assert.Equal([@"C:\Users\me\VMs\win11.vhdx", @"C:\Users\me\Downloads\ubuntu.iso", @"C:\Users\me\Videos\talk.mp4"], others.Select(f => f.Path));
    }

    [Fact]
    public void Hides_files_under_a_raised_threshold() =>
        Assert.Equal(2, Scan().Others(Machine.Scan(), TrayPin.Defaults, 5L << 30).Count);

    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(25, 2, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 4, false)]
    public void Scans_again_daily_or_when_the_threshold_drops(int hoursLater, int minGiB, bool due) =>
        Assert.Equal(due, Scan().IsDue(Now.AddHours(-3).AddHours(hoursLater), (long)minGiB << 30));

    [Fact]
    public void Round_trips_through_json() =>
        Assert.Equal(Scan().Files, LargeFileScan.FromJson(Scan().ToJson())!.Files);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ nope")]
    [InlineData("""{ "scannedAt": "2026-10-11T00:00:00Z" }""")]
    public void Scans_again_when_the_saved_scan_is_unusable(string? json) => Assert.Null(LargeFileScan.FromJson(json));

    [Fact]
    public void Flyout_lists_them_with_where_they_are()
    {
        var flyout = FlyoutLayout.Build(
            Machine.Scan(), new AlertThresholds(), SurfaceTheme.Dark, Now, TimeSpan.FromMinutes(5), null, CultureInfo.InvariantCulture, TimeZoneInfo.Utc,
            largeFiles: new LargeFileListing(Scan(), 2L << 30));

        Assert.True(flyout.ShowsLargeFiles);
        Assert.Equal(new LargeFileLine(@"C:\Users\me\VMs\win11.vhdx", "win11.vhdx", "40 GiB", @"C:\Users\me\VMs", CanPin: false), flyout.LargeFiles[0]);
        Assert.Equal(4, flyout.LargeFiles.Count);
        Assert.Equal("Files over 2.0 GiB in your user folder · scanned 09:00", flyout.LargeFilesNote);
    }

    [Fact]
    public void Flyout_offers_pinning_while_there_is_room()
    {
        var flyout = FlyoutLayout.Build(
            Machine.Scan(), new AlertThresholds(), SurfaceTheme.Dark, Now, TimeSpan.FromMinutes(5), null, CultureInfo.InvariantCulture, TimeZoneInfo.Utc,
            [new TrayPin(PinKind.Drive)], new LargeFileListing(Scan(), 2L << 30));

        Assert.All(flyout.LargeFiles, f => Assert.True(f.CanPin));
    }

    [Theory]
    [InlineData(false, "Files over 2.0 GiB in your user folder show here after the daily scan")]
    [InlineData(true, "Looking for files over 2.0 GiB in your user folder…")]
    public void Flyout_says_when_nothing_has_been_scanned_yet(bool scanning, string note)
    {
        var flyout = FlyoutLayout.Build(
            Machine.Scan(), new AlertThresholds(), SurfaceTheme.Dark, Now, TimeSpan.FromMinutes(5), null, CultureInfo.InvariantCulture, TimeZoneInfo.Utc,
            largeFiles: new LargeFileListing(null, 2L << 30, scanning));

        Assert.Empty(flyout.LargeFiles);
        Assert.Equal(note, flyout.LargeFilesNote);
    }

    [Fact]
    public void Flyout_leaves_the_section_out_when_its_turned_off() =>
        Assert.False(FlyoutLayout.Build(Machine.Scan(), new AlertThresholds(), SurfaceTheme.Dark, Now, TimeSpan.FromMinutes(5)).ShowsLargeFiles);

    [Fact]
    public void Says_how_many_more_there_are()
    {
        var many = Scan() with { Files = Enumerable.Range(1, 8).Select(i => new LargeFile($@"C:\Users\me\f{i}.bin", (long)(10 + i) << 30)).ToList() };

        var flyout = FlyoutLayout.Build(
            Machine.Scan(), new AlertThresholds(), SurfaceTheme.Dark, Now, TimeSpan.FromMinutes(5), null, CultureInfo.InvariantCulture, TimeZoneInfo.Utc,
            largeFiles: new LargeFileListing(many, 2L << 30));

        Assert.Equal(5, flyout.LargeFiles.Count);
        Assert.EndsWith("· 3 more not shown", flyout.LargeFilesNote);
    }
}

public class DetectionOptionsTests
{
    private static DiskSnapshot Scan(DetectionOptions detect) =>
        new DiskScanner(Machine.Probe(), new FakeWslRegistry(Machine.Registry()), Machine.Paths, new FixedClock(DateTimeOffset.UnixEpoch)).Scan(null, detect);

    [Fact]
    public void Skips_what_detection_turns_off()
    {
        Assert.Null(Scan(new DetectionOptions(Docker: false)).Docker);
        Assert.NotNull(Scan(new DetectionOptions(Docker: false)).PrimaryDistro);
        Assert.Null(Scan(new DetectionOptions(Distros: false)).PrimaryDistro);
        Assert.NotNull(Scan(new DetectionOptions(Distros: false)).Docker);
    }

    [Fact]
    public void Says_a_pinned_item_is_turned_off_rather_than_missing()
    {
        var icons = TrayLayout.Build(Scan(new DetectionOptions(false, false)), new AlertThresholds(), CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

        Assert.Equal("Docker data\nTurned off in Settings › Detection", icons[0].ToolTip);
        Assert.EndsWith("Turned off in Settings › Detection", icons[1].ToolTip);
    }

    [Fact]
    public void Keeps_detection_settings_through_json()
    {
        var settings = new AppSettings
        {
            DetectDistros = false,
            DetectDocker = false,
            LargeFileScan = false,
            LargeFileMinGiB = 10,
            ShowLargeFiles = false,
            CodeFolders = [@"\\wsl.localhost\Ubuntu\home\me\git", @"C:\code"],
        };

        Assert.Equal(settings, AppSettings.FromJson(settings.ToJson()));
    }

    [Fact]
    public void Resets_a_threshold_that_makes_no_sense() =>
        Assert.Equal(2, AppSettings.FromJson("""{ "largeFileMinGiB": 0 }""").LargeFileMinGiB);

    [Fact]
    public void Drops_blank_and_repeated_code_folders() =>
        Assert.Equal([@"C:\code"], AppSettings.FromJson("""{ "codeFolders": ["C:\\code", " ", "c:\\CODE"] }""").CodeFolders);
}
