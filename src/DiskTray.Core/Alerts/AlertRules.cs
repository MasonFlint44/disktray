using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskTray.Core.Flyout;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Alerts;

/// <summary>When to notify, from Settings.</summary>
public sealed record AlertOptions(bool NotifyOnDrop, bool DailyReminder, TimeOnly ReminderTime);

/// <summary>
/// What's been notified so far: the highest level that fired and hasn't re-armed, and the
/// day of the last notification. Saved as alerts.json so a restart doesn't notify again.
/// </summary>
public sealed record AlertState(AlertLevel Fired = AlertLevel.Normal, DateOnly? LastNotified = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads saved state. Anything unreadable starts over, which at worst notifies once more.</summary>
    public static AlertState FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AlertState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<AlertState>(json, Json);
            return state is not null && Enum.IsDefined(state.Fired) ? state : new AlertState();
        }
        catch (JsonException)
        {
            return new AlertState();
        }
    }
}

public enum AlertKind
{
    /// <summary>Free space just dropped below a level.</summary>
    Drop,

    /// <summary>The once-a-day reminder while it stays low.</summary>
    Reminder,
}

/// <summary>A notification to show.</summary>
public sealed record AlertToast(AlertKind Kind, AlertLevel Level, string Title, string Body);

/// <summary>
/// Decides when to notify about low free space on the system drive: once when it drops
/// below a level, and once a day while it stays low.
/// </summary>
public static class AlertRules
{
    /// <summary>How far above a level free space has to climb before that level can fire again.</summary>
    public const long RearmMarginBytes = 3L << 30;

    /// <param name="now">The current time, read in <paramref name="zone"/> for the reminder time and the day.</param>
    /// <param name="userActive">Whether the user is at the PC now. The reminder waits for that.</param>
    public static (AlertState State, AlertToast? Toast) Check(
        AlertState state,
        DiskSnapshot snapshot,
        AlertThresholds thresholds,
        AlertOptions options,
        DateTimeOffset now,
        bool userActive,
        TimeZoneInfo? zone = null,
        IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Local).DateTime;
        var today = DateOnly.FromDateTime(local);
        var drive = snapshot.SystemDrive;
        var level = thresholds.Classify(drive);

        // Re-arm the levels free space has climbed back above, with a margin so it doesn't flap.
        var fired = state.Fired;
        while (fired > AlertLevel.Normal && drive.FreeBytes >= LevelBytes(drive, thresholds, fired) + RearmMarginBytes)
        {
            fired--;
        }

        if (level > fired)
        {
            // Below a new level. This counts as the day's notification.
            return options.NotifyOnDrop
                ? (new AlertState(level, today), Toast(AlertKind.Drop, level, snapshot, thresholds, provider))
                : (state with { Fired = level }, null);
        }

        state = state with { Fired = fired };
        if (options.DailyReminder && level > AlertLevel.Normal && userActive
            && TimeOnly.FromDateTime(local) >= options.ReminderTime && state.LastNotified != today)
        {
            return (state with { LastNotified = today }, Toast(AlertKind.Reminder, level, snapshot, thresholds, provider));
        }

        return (state, null);
    }

    private static long LevelBytes(DriveSpace drive, AlertThresholds thresholds, AlertLevel level) =>
        (long)(drive.TotalBytes * (level == AlertLevel.Critical ? thresholds.CriticalFreeFraction : thresholds.WarningFreeFraction));

    internal static AlertToast Toast(AlertKind kind, AlertLevel level, DiskSnapshot snapshot, AlertThresholds thresholds, IFormatProvider provider)
    {
        var drive = snapshot.SystemDrive;
        string title = (kind, level) switch
        {
            (AlertKind.Drop, AlertLevel.Critical) => $"Drive {drive.Label} is almost full",
            (AlertKind.Drop, _) => $"Drive {drive.Label} is getting full",
            (_, AlertLevel.Critical) => $"Drive {drive.Label} is still almost full",
            _ => $"Drive {drive.Label} is still low on space",
        };

        string body = FlyoutLayout.LowSpaceBanner(drive, thresholds, provider)?.Text ?? "";
        long reclaimable = TrayLayout.CompactableFiles(snapshot).Where(f => string.Equals(f.DriveRoot, drive.Root, StringComparison.OrdinalIgnoreCase)).Sum(f => f.ReclaimableBytes ?? 0);
        if (reclaimable > 0)
        {
            body += $"\n{ByteSize.Format(reclaimable, provider)} is reclaimable by compacting.";
        }

        return new AlertToast(kind, level, title, body);
    }
}
