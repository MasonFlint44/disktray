namespace DiskTray.Core.Tray;

/// <summary>A slice of a pie, as a fraction of the whole (0–1).</summary>
public sealed record PieSlice(double Fraction, Rgb Color);

/// <summary>
/// What a tray icon draws: slices clockwise from 12 o'clock, with a warning or critical
/// ring when free space is low.
/// </summary>
public sealed record PieChart(IReadOnlyList<PieSlice> Slices, AlertLevel Alert)
{
    /// <summary>Builds slices from byte amounts, dropping empty ones. Negative amounts count as zero.</summary>
    public static PieChart FromAmounts(IEnumerable<(long Bytes, Rgb Color)> amounts, AlertLevel alert)
    {
        var parts = amounts.Where(a => a.Bytes > 0).ToList();
        double total = parts.Sum(a => (double)a.Bytes);
        var slices = total > 0
            ? parts.Select(a => new PieSlice(a.Bytes / total, a.Color)).ToList()
            : [new PieSlice(1, Palette.Free)];
        return new PieChart(slices, alert);
    }
}
