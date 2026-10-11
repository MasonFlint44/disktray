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
    public static readonly Rgb Accent = Rgb.FromHex("#4CC2FF");

    public static ItemColor For(DiskKind kind) => kind == DiskKind.DockerData ? Docker : Wsl;
}
