using System.Globalization;
using DiskTray.Core.Flyout;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Tests;

public class FlyoutLayoutTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddHours(2);

    private static FlyoutModel Build(DiskSnapshot snapshot, SurfaceTheme theme = SurfaceTheme.Dark, string? problem = null) =>
        FlyoutLayout.Build(snapshot, new AlertThresholds(), theme, Now, TimeSpan.FromMinutes(5), problem, CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    /// <summary>The real machine, with Docker measured just now and Ubuntu remembered from an earlier reading.</summary>
    private static DiskSnapshot Measured()
    {
        var snapshot = Machine.Scan();
        return snapshot with
        {
            Files = snapshot.Files.Select(f => f.Kind == DiskKind.DockerData
                ? f with { UsedBytes = 36L << 30 }
                : f.Id == Machine.UbuntuId ? f with { UsedBytes = 35L << 30, UsedAsOf = DateTimeOffset.UnixEpoch } : f).ToList(),
        };
    }

    [Fact]
    public void Describes_the_drive()
    {
        var flyout = Build(Machine.Scan());

        Assert.Equal("Drive C: · 472 GiB", flyout.DriveLine);
        Assert.Equal("50 GiB", flyout.FreeSize);
        Assert.Equal("Drive C: 27% Docker data, 13% Ubuntu, 49% Windows & other, 11% free", flyout.DonutDescription);
        Assert.Equal("Updated 00:00 · checks every 5 min", flyout.Footer);
    }

    [Fact]
    public void Warns_when_free_space_is_low()
    {
        var flyout = Build(Machine.Scan());

        Assert.Equal([new FlyoutBanner(AlertLevel.Warning, "50 GiB free (11%) – below your 15% warning level")], flyout.Banners);
    }

    [Fact]
    public void Never_shows_low_free_space_rounded_up_to_its_level()
    {
        var banner = FlyoutLayout.LowSpaceBanner(new DriveSpace(@"C:\", 1000, 146), new AlertThresholds(), CultureInfo.InvariantCulture);

        Assert.EndsWith("free (14%) – below your 15% warning level", banner!.Text);
        Assert.Contains("(14%) – below 15% warning", TrayLayout.FreeLine(new DriveSpace(@"C:\", 1000, 146), new AlertThresholds(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Has_no_banner_when_there_is_room() =>
        Assert.Null(FlyoutLayout.LowSpaceBanner(new DriveSpace(@"C:\", 1000, 500), new AlertThresholds(), CultureInfo.InvariantCulture));

    [Fact]
    public void Shows_critical_levels_and_failed_refreshes_first()
    {
        var critical = FlyoutLayout.LowSpaceBanner(new DriveSpace(@"C:\", 1000, 20), new AlertThresholds(), CultureInfo.InvariantCulture);
        var flyout = Build(Machine.Scan(), problem: "Couldn't refresh: boom");

        Assert.Equal(new FlyoutBanner(AlertLevel.Critical, "20 bytes free (2%) – below your 5% critical level"), critical);
        Assert.Equal(new FlyoutBanner(AlertLevel.Critical, "Couldn't refresh: boom"), flyout.Banners[0]);
        Assert.Equal(AlertLevel.Warning, flyout.Banners[1].Level);
    }

    [Fact]
    public void Lists_each_item_with_used_and_reclaimable_space()
    {
        var legend = Build(Measured()).Legend;

        Assert.Equal(
            [
                new LegendItem("Docker data", "129 GiB", Palette.Docker.Main, "36 GiB used · 93 GiB reclaimable"),
                new LegendItem("Ubuntu", "60 GiB", Palette.Wsl.Main, "35 GiB used · 25 GiB reclaimable · as of 00:00"),
                new LegendItem("Windows & other", "233 GiB", Palette.Other),
                new LegendItem("Free", "50 GiB", Palette.Free),
            ],
            legend);
    }

    [Fact]
    public void Splits_each_file_into_used_and_reclaimable_slices()
    {
        var flyout = Build(Measured());

        Assert.Equal(
            [Palette.Docker.Main, Palette.Docker.Reclaimable, Palette.Wsl.Main, Palette.Wsl.Reclaimable, Palette.Other, Palette.Free],
            flyout.Donut.Slices.Select(s => s.Color));
        Assert.Equal(1.0, flyout.Donut.Slices.Sum(s => s.Fraction), 9);
        Assert.True(flyout.ShowsReclaimable);
        Assert.Equal("118 GiB", flyout.ReclaimableTotal);
        Assert.Equal("Docker 93 GiB · Ubuntu 25 GiB", flyout.ReclaimableBreakdown);
    }

    [Fact]
    public void Counts_every_disk_clean_up_compacts_in_the_total_pinned_or_not()
    {
        var flyout = FlyoutLayout.Build(
            Measured(), new AlertThresholds(), SurfaceTheme.Dark, Now, TimeSpan.FromMinutes(5), provider: CultureInfo.InvariantCulture, zone: TimeZoneInfo.Utc,
            pins: [new TrayPin(PinKind.Drive)]);

        Assert.Equal("118 GiB", flyout.ReclaimableTotal);
        Assert.Equal("Docker 93 GiB · Ubuntu 25 GiB", flyout.ReclaimableBreakdown);
    }

    [Fact]
    public void Puts_the_largest_disk_file_first()
    {
        var snapshot = Measured();
        snapshot = snapshot with
        {
            Files = snapshot.Files.Select(f => f.Id == Machine.UbuntuId ? f with { SizeBytes = 200L << 30 } : f).ToList(),
        };

        var flyout = Build(snapshot);

        Assert.Equal(["Ubuntu", "Docker data", "Windows & other", "Free"], flyout.Legend.Select(i => i.Name));
        Assert.Equal([Palette.Wsl.Main, Palette.Wsl.Reclaimable, Palette.Docker.Main], flyout.Donut.Slices.Take(3).Select(s => s.Color));
        Assert.StartsWith("Ubuntu", flyout.ReclaimableBreakdown);
    }

    [Fact]
    public void Says_how_to_measure_files_that_havent_been()
    {
        var flyout = Build(Machine.Scan());

        Assert.Equal("Shows what's reclaimable once Docker Desktop runs", flyout.Legend[0].Detail);
        Assert.Equal("Shows what's reclaimable once Ubuntu runs", flyout.Legend[1].Detail);
        Assert.False(flyout.ShowsReclaimable);
        Assert.Null(flyout.ReclaimableTotal);
        Assert.Equal([Palette.Docker.Main, Palette.Wsl.Main, Palette.Other, Palette.Free], flyout.Donut.Slices.Select(s => s.Color));
    }

    [Fact]
    public void Keeps_free_space_lightest_on_a_light_background()
    {
        var flyout = Build(Machine.Scan(), SurfaceTheme.Light);

        Assert.Equal([Palette.OtherOnLight, Palette.FreeOnLight], flyout.Legend.TakeLast(2).Select(i => i.Color));
        Assert.Equal(Palette.FreeOnLight, flyout.Donut.Slices[^1].Color);
    }

    [Theory]
    [InlineData(5, "checks every 5 min")]
    [InlineData(90, "checks every 1.5 h")]
    [InlineData(120, "checks every 2 h")]
    public void Says_how_often_it_checks(int minutes, string expected) =>
        Assert.EndsWith(
            expected,
            FlyoutLayout.Footer(Now, Now, TimeSpan.FromMinutes(minutes), TimeZoneInfo.Utc, CultureInfo.InvariantCulture));
}
