namespace DiskTray.Core.Tray;

public enum AlertLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>Free-space levels, as fractions of the drive (0.15 = 15%).</summary>
public sealed record AlertThresholds(double WarningFreeFraction = 0.15, double CriticalFreeFraction = 0.05)
{
    public AlertLevel Classify(DriveSpace drive) =>
        drive.FreeFraction < CriticalFreeFraction ? AlertLevel.Critical
        : drive.FreeFraction < WarningFreeFraction ? AlertLevel.Warning
        : AlertLevel.Normal;

    /// <summary>The free fraction below which the drive is at <paramref name="level"/>, or null for <see cref="AlertLevel.Normal"/>.</summary>
    public double? FractionFor(AlertLevel level) => level switch
    {
        AlertLevel.Critical => CriticalFreeFraction,
        AlertLevel.Warning => WarningFreeFraction,
        _ => null,
    };
}
