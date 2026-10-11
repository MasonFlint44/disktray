using System.Globalization;

namespace DiskTray.Core;

/// <summary>
/// Formats sizes in 1024-based units labelled KiB/MiB/GiB/TiB. The numbers match Explorer,
/// which counts the same way but labels them KB/MB/GB/TB.
/// One decimal below 10, whole numbers from 10 up ("2.8 GiB", "129 GiB").
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["bytes", "KiB", "MiB", "GiB", "TiB"];

    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        if (Math.Abs(bytes) < 1024)
        {
            return string.Format(provider, "{0} bytes", bytes);
        }

        double value = bytes;
        int unit = 0;
        while (unit < Units.Length - 1 && Math.Abs(value) >= 1024)
        {
            value /= 1024;
            unit++;
        }

        double rounded = Round(value);
        // 1023.7 MiB would print as "1024 MiB"; show it as "1.0 GiB" instead.
        if (Math.Abs(rounded) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
            rounded = Round(value);
        }

        string number = rounded.ToString(Math.Abs(rounded) < 10 ? "0.0" : "0", provider);
        return $"{number} {Units[unit]}";
    }

    /// <summary>Formats a 0–1 fraction as a whole percentage, with "&lt;1%" for small non-zero values.</summary>
    public static string FormatPercent(double fraction, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        double percent = fraction * 100;
        if (percent > 0 && percent < 0.5)
        {
            return "<1%";
        }

        return Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", provider) + "%";
    }

    private static double Round(double value) =>
        Math.Abs(value) < 9.95
            ? Math.Round(value, 1, MidpointRounding.AwayFromZero)
            : Math.Round(value, 0, MidpointRounding.AwayFromZero);
}
