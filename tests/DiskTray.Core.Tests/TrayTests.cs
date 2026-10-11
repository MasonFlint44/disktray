using System.Buffers.Binary;
using System.Globalization;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Tests;

public class AlertThresholdsTests
{
    [Theory]
    [InlineData(160, AlertLevel.Normal)]
    [InlineData(150, AlertLevel.Normal)]
    [InlineData(107, AlertLevel.Warning)]
    [InlineData(50, AlertLevel.Warning)]
    [InlineData(49, AlertLevel.Critical)]
    public void Classifies_free_space(long freeOfThousand, AlertLevel expected) =>
        Assert.Equal(expected, new AlertThresholds().Classify(new DriveSpace(@"C:\", 1000, freeOfThousand)));
}

public class PieChartTests
{
    private static readonly Rgb Red = Rgb.FromHex("#FF0000");
    private static readonly Rgb Blue = Rgb.FromHex("0000FF");

    [Fact]
    public void Turns_amounts_into_fractions_and_drops_empty_ones()
    {
        var chart = PieChart.FromAmounts([(30, Red), (0, Blue), (-5, Blue), (70, Blue)], AlertLevel.Normal);

        Assert.Equal([new PieSlice(0.3, Red), new PieSlice(0.7, Blue)], chart.Slices);
    }

    [Fact]
    public void Shows_an_empty_pie_when_there_is_nothing() =>
        Assert.Equal([new PieSlice(1, Palette.Free)], PieChart.FromAmounts([], AlertLevel.Normal).Slices);

    [Fact]
    public void Mixes_colors() => Assert.Equal(Rgb.FromHex("#F4AA90"), Palette.Wsl.Main.Mix(Rgb.White, 0.5));

    [Fact]
    public void Rejects_bad_colors() => Assert.Throws<FormatException>(() => Rgb.FromHex("#12345"));
}

public class TrayLayoutTests
{
    private static IReadOnlyList<TrayIconModel> Build(DiskSnapshot snapshot) =>
        TrayLayout.Build(snapshot, new AlertThresholds(), CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    [Fact]
    public void Shows_docker_then_the_distro_then_the_drive() =>
        Assert.Equal(["docker", Machine.UbuntuId, TrayLayout.DriveKey], Build(Machine.Scan()).Select(i => i.Key));

    [Fact]
    public void Writes_tooltips_for_the_real_machine()
    {
        var icons = Build(Machine.Scan());

        Assert.Equal("Docker data · 129 GiB\nC: 50 GiB free (11%) – below 15% warning", icons[0].ToolTip);
        Assert.Equal("Ubuntu · 60 GiB\nC: 50 GiB free (11%) – below 15% warning", icons[1].ToolTip);
        Assert.Equal(
            "Drive C: · 472 GiB\nDocker 129 GiB · Ubuntu 60 GiB · Other 233 GiB\nC: 50 GiB free (11%) – below 15% warning",
            icons[2].ToolTip);
        Assert.All(icons, i => Assert.Equal(AlertLevel.Warning, i.Chart.Alert));
    }

    [Fact]
    public void Disk_icon_is_the_file_plus_free_space()
    {
        var docker = Build(Machine.Scan())[0].Chart.Slices;
        double whole = Machine.DockerSize + Machine.DriveFree;

        Assert.Equal(2, docker.Count);
        Assert.Equal(Palette.Docker.Main, docker[0].Color);
        Assert.Equal(Machine.DockerSize / whole, docker[0].Fraction, 9);
        Assert.Equal(Palette.Free, docker[1].Color);
    }

    [Fact]
    public void Disk_icon_shades_reclaimable_space_when_usage_is_known()
    {
        var file = new DiskFile("docker", "Docker data", @"C:\d.vhdx", DiskKind.DockerData, 138) { UsedBytes = 34 };
        var drive = new DriveSpace(@"C:\", 472, 51);

        var icon = TrayLayout.DiskIcon(file, drive, new AlertThresholds(), CultureInfo.InvariantCulture, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc);

        Assert.Equal([Palette.Docker.Main, Rgb.FromHex("#92CAF6"), Palette.Free], icon.Chart.Slices.Select(s => s.Color));
        Assert.Equal(104 / 189.0, icon.Chart.Slices[1].Fraction, 9);
        Assert.StartsWith("Docker data · 138 bytes (104 bytes reclaimable)", icon.ToolTip);
    }

    [Fact]
    public void Says_when_reclaimable_space_was_last_measured()
    {
        var now = new DateTimeOffset(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);
        var file = new DiskFile("docker", "Docker data", @"C:\d.vhdx", DiskKind.DockerData, 138)
        {
            UsedBytes = 34,
            UsedAsOf = now.AddHours(-3).AddMinutes(-46),
        };

        var icon = TrayLayout.DiskIcon(file, new DriveSpace(@"C:\", 472, 51), new AlertThresholds(), CultureInfo.InvariantCulture, now, TimeZoneInfo.Utc);

        Assert.StartsWith("Docker data · 138 bytes (104 bytes reclaimable as of 14:14)\n", icon.ToolTip);
    }

    [Theory]
    [InlineData("2026-10-11T12:05:00Z", "08:05")]
    [InlineData("2026-10-11T02:00:00Z", "Oct 10")]
    [InlineData("2026-10-09T15:00:00Z", "Oct 9")]
    [InlineData("2025-12-30T15:00:00Z", "12/30/2025")]
    public void Writes_as_of_times_in_the_local_zone(string measuredAt, string expected)
    {
        // 6 PM in New York (UTC−4 in October).
        var now = new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal(expected, TrayLayout.AsOf(DateTimeOffset.Parse(measuredAt, CultureInfo.InvariantCulture), now, zone, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Drive_icon_breaks_the_drive_down()
    {
        var slices = Build(Machine.Scan())[2].Chart.Slices;

        Assert.Equal([Palette.Docker.Main, Palette.Wsl.Main, Palette.Other, Palette.Free], slices.Select(s => s.Color));
        Assert.Equal(1.0, slices.Sum(s => s.Fraction), 9);
        Assert.Equal((double)Machine.DriveFree / Machine.DriveTotal, slices[3].Fraction, 9);
    }

    [Fact]
    public void Drive_icon_leaves_out_files_on_other_drives()
    {
        var probe = Machine.Probe();
        probe.Texts[Machine.AppData + @"\Docker\settings-store.json"] = """{ "CustomWslDistroDir": "D:\\docker" }""";
        probe.Files[@"D:\docker\disk\docker_data.vhdx"] = 5;
        probe.Drives[@"D:\"] = new DriveSpace(@"D:\", 1000, 400);

        var icons = Build(Machine.Scan(probe));

        Assert.StartsWith("Docker data · 5 bytes\nD: 400 bytes free (40%)", icons[0].ToolTip);
        Assert.DoesNotContain("Docker", icons[2].ToolTip);
        Assert.Equal(3, icons[2].Chart.Slices.Count);
    }

    [Fact]
    public void Says_when_free_space_is_critical()
    {
        string line = TrayLayout.FreeLine(new DriveSpace(@"C:\", 1000, 40), new AlertThresholds(), CultureInfo.InvariantCulture);

        Assert.Equal("C: 40 bytes free (4%) – below 5% critical", line);
    }

    [Fact]
    public void Keeps_tooltips_within_the_windows_limit()
    {
        var probe = Machine.Probe();
        string longName = new('x', 120);
        var registry = Machine.Registry();
        registry = registry with { Entries = [registry.Entries[0] with { DistributionName = longName }, .. registry.Entries.Skip(1)] };

        var icons = Build(Machine.Scan(probe, registry));

        Assert.All(icons, i => Assert.True(i.ToolTip.Length <= TrayLayout.MaxToolTipLength, i.ToolTip));
        Assert.EndsWith("…", icons[1].ToolTip);
        // The drive icon drops its breakdown line rather than cutting off the free-space line.
        Assert.Equal("Drive C: · 472 GiB\nC: 50 GiB free (11%) – below 15% warning", icons[2].ToolTip);
    }
}

public class IcoWriterTests
{
    [Fact]
    public void Writes_a_32_bit_icon_with_rows_bottom_up()
    {
        // 2×2: top row red, green; bottom row blue, white. BGRA order.
        byte[] pixels =
        [
            0, 0, 255, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 255, 255, 255,
        ];

        byte[] ico = IcoWriter.Write(2, 2, pixels);

        Assert.Equal(6 + 16 + 40 + 16 + 8, ico.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));
        Assert.Equal(2, ico[6]);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(12)));
        Assert.Equal(ico.Length - 22, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(14)));
        Assert.Equal(22, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(18)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(22 + 8)));
        // The first stored row is the bottom one (blue, white).
        Assert.Equal(pixels[8..], ico[62..70]);
        Assert.Equal(pixels[..8], ico[70..78]);
    }

    [Fact]
    public void Stores_256_as_zero_in_the_directory()
    {
        byte[] ico = IcoWriter.Write(256, 256, new byte[256 * 256 * 4]);

        Assert.Equal(0, ico[6]);
        Assert.Equal(0, ico[7]);
    }

    [Fact]
    public void Rejects_the_wrong_number_of_pixels() =>
        Assert.Throws<ArgumentException>(() => IcoWriter.Write(2, 2, new byte[15]));

    [Fact]
    public void Packs_several_pngs_into_one_icon()
    {
        byte[] small = [1, 2, 3];
        byte[] large = [4, 5, 6, 7];

        byte[] ico = IcoWriter.WritePngs([(16, small), (256, large)]);

        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));
        Assert.Equal(16, ico[6]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 8)));
        Assert.Equal(38, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 12)));
        Assert.Equal(0, ico[22]);
        Assert.Equal(41, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(22 + 12)));
        Assert.Equal(small, ico[38..41]);
        Assert.Equal(large, ico[41..]);
    }
}
