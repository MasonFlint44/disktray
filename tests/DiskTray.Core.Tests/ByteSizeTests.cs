using System.Globalization;

namespace DiskTray.Core.Tests;

public class ByteSizeTests
{
    private const long MB = 1024L * 1024;
    private const long GB = MB * 1024;

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(550 * MB, "550 MiB")]
    [InlineData(3_006_477_107, "2.8 GiB")]
    [InlineData(138_174_005_248, "129 GiB")]
    [InlineData(54_042_759_168, "50 GiB")]
    [InlineData(506_332_180_480, "472 GiB")]
    [InlineData(1_649_267_441_664, "1.5 TiB")]
    [InlineData(-2048, "-2.0 KiB")]
    public void Formats_like_explorer(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(9.94, "9.9 GiB")]
    [InlineData(9.96, "10 GiB")]
    [InlineData(10.4, "10 GiB")]
    public void Switches_to_whole_numbers_at_ten(double gigabytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format((long)(gigabytes * GB), CultureInfo.InvariantCulture));

    [Fact]
    public void Rolls_over_to_the_next_unit_instead_of_printing_1024() =>
        Assert.Equal("1.0 GiB", ByteSize.Format((long)(1023.7 * MB), CultureInfo.InvariantCulture));

    [Fact]
    public void Uses_the_given_culture() =>
        Assert.Equal("2,8 GiB", ByteSize.Format(3_006_477_107, CultureInfo.GetCultureInfo("de-DE")));

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.003, "<1%")]
    [InlineData(0.10673, "11%")]
    [InlineData(0.15, "15%")]
    [InlineData(1.0, "100%")]
    public void Formats_percentages(double fraction, string expected) =>
        Assert.Equal(expected, ByteSize.FormatPercent(fraction, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(0.146, 0.15, "14%")]
    [InlineData(0.10673, 0.15, "11%")]
    [InlineData(0.0499, 0.05, "4%")]
    [InlineData(0.007, 0.01, "<1%")]
    public void Never_rounds_up_to_the_level_its_below(double fraction, double level, string expected) =>
        Assert.Equal(expected, ByteSize.FormatPercent(fraction, CultureInfo.InvariantCulture, level));
}
