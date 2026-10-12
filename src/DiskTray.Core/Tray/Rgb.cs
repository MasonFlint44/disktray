using System.Globalization;

namespace DiskTray.Core.Tray;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses "#RRGGBB" or "RRGGBB".</summary>
    public static Rgb FromHex(string hex)
    {
        string digits = hex.StartsWith('#') ? hex[1..] : hex;
        if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            throw new FormatException($"Expected a color like #RRGGBB, got '{hex}'.");
        }

        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public static readonly Rgb White = new(255, 255, 255);

    /// <summary>This color moved <paramref name="amount"/> (0–1) of the way toward <paramref name="other"/>.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        byte Channel(byte from, byte to) => (byte)Math.Round(from + ((to - from) * amount));
        return new Rgb(Channel(R, other.R), Channel(G, other.G), Channel(B, other.B));
    }

    /// <summary>A color from hue (0–360), saturation and value (0–1), as a color picker sets them.</summary>
    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        double m = value - chroma;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        byte Channel(double c) => (byte)Math.Round((c + m) * 255);
        return new Rgb(Channel(r), Channel(g), Channel(b));
    }

    /// <summary>Hue (0–360), saturation and value (0–1). Gray has hue 0.</summary>
    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double delta = max - Math.Min(r, Math.Min(g, b));
        double hue = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta % 6 + 6) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>The WCAG contrast ratio with another color, from 1 (the same) to 21 (black on white).</summary>
    public double ContrastWith(Rgb other)
    {
        double lighter = Math.Max(Luminance, other.Luminance);
        double darker = Math.Min(Luminance, other.Luminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private double Luminance
    {
        get
        {
            static double Linear(byte channel)
            {
                double c = channel / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));
        }
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>An item's color, and the lighter tint used for its reclaimable space.</summary>
public readonly record struct ItemColor(Rgb Main)
{
    /// <summary>
    /// The item color mixed halfway with white. The free slice next to it is dark gray, so a
    /// darker shade would blend into it on either taskbar theme.
    /// </summary>
    public Rgb Reclaimable => Main.Mix(Rgb.White, 0.5);
}

/// <summary>Colors from docs/design.md.</summary>
public static class Palette
{
    public static readonly ItemColor Docker = new(Rgb.FromHex("#2496ED"));
    public static readonly ItemColor Wsl = new(Rgb.FromHex("#E95420"));
    public static readonly Rgb Other = Rgb.FromHex("#D0D0D0");
    public static readonly Rgb Free = Rgb.FromHex("#3A3A3A");

    /// <summary>Other and free on a light background (the flyout in light theme), where free stays the lightest.</summary>
    public static readonly Rgb OtherOnLight = Rgb.FromHex("#8C8C8C");
    public static readonly Rgb FreeOnLight = Rgb.FromHex("#E0E0E0");
    public static readonly Rgb Outline = Rgb.FromHex("#5A5A5A");
    public static readonly Rgb Warning = Rgb.FromHex("#F0B040");
    public static readonly Rgb Critical = Rgb.FromHex("#E5534B");

    /// <summary>The usual colors for pinned files and folders, in turn.</summary>
    public static readonly IReadOnlyList<ItemColor> Extras =
    [
        new(Rgb.FromHex("#A371F7")),
        new(Rgb.FromHex("#3FB950")),
        new(Rgb.FromHex("#1ABC9C")),
    ];

    /// <summary>
    /// Whether a picked color is hard to tell from the free slice next to it: below 1.5 : 1,
    /// about where the old darker shade sat (1.3 : 1) and blended in.
    /// </summary>
    public static bool BlendsWithFree(Rgb color) => color.ContrastWith(Free) < 1.5;
}
