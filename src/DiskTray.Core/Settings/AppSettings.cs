using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskTray.Core.Alerts;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Settings;

/// <summary>The theme for the flyout and the app's windows.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>What the user chose in Settings. Saved as settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>The choices for <see cref="RefreshMinutes"/> in Settings.</summary>
    public static readonly IReadOnlyList<int> RefreshChoices = [1, 5, 15, 30, 60];

    public const int MaxWarningPercent = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly AppSettings Defaults = new();

    public AppTheme Theme { get; init; } = AppTheme.System;

    public int RefreshMinutes { get; init; } = 5;

    /// <summary>Free space, as a percentage of the drive, below which the warning shows.</summary>
    public int WarningPercent { get; init; } = 15;

    /// <summary>Free space, as a percentage of the drive, below which it turns critical.</summary>
    public int CriticalPercent { get; init; } = 5;

    /// <summary>Notify as soon as free space drops below a level.</summary>
    public bool NotifyOnDrop { get; init; } = true;

    /// <summary>While it stays low, remind once a day, the first time the user is at the PC after <see cref="ReminderTime"/>.</summary>
    public bool DailyReminder { get; init; } = true;

    public TimeOnly ReminderTime { get; init; } = new(9, 0);

    /// <summary>
    /// Whether the app has added itself to Startup apps yet. It does on first launch, turned
    /// on, and after that leaves the choice to Settings and Task Manager.
    /// </summary>
    public bool StartupRegistered { get; init; }

    [JsonIgnore]
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(RefreshMinutes);

    [JsonIgnore]
    public AlertOptions Alerts => new(NotifyOnDrop, DailyReminder, ReminderTime);

    [JsonIgnore]
    public AlertThresholds Thresholds => new(WarningPercent / 100.0, CriticalPercent / 100.0);

    /// <summary>What's wrong with the alert levels, worded for Settings, or null when they're fine.</summary>
    public string? LevelsProblem() =>
        WarningPercent is < 1 or > MaxWarningPercent ? $"The warning level has to be between 1% and {MaxWarningPercent}%."
        : CriticalPercent < 0 ? "The critical level can't be below 0%."
        : CriticalPercent >= WarningPercent ? "The critical level has to be below the warning level."
        : null;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads saved settings. A file that can't be read gives the defaults, and a value that
    /// makes no sense goes back to its default, so a hand-edited file can't stop the app.
    /// </summary>
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Defaults;
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, Json);
        }
        catch (JsonException)
        {
            return Defaults;
        }

        if (settings is null)
        {
            return Defaults;
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            settings = settings with { Theme = Defaults.Theme };
        }

        if (settings.RefreshMinutes is < 1 or > 24 * 60)
        {
            settings = settings with { RefreshMinutes = Defaults.RefreshMinutes };
        }

        if (settings.LevelsProblem() is not null)
        {
            settings = settings with { WarningPercent = Defaults.WarningPercent, CriticalPercent = Defaults.CriticalPercent };
        }

        return settings;
    }

    /// <summary>A level as space on the drive, for next to its box in Settings: "≈ 71 GiB".</summary>
    public static string ApproximateSpace(int percent, DriveSpace drive, IFormatProvider? provider = null) =>
        "≈ " + ByteSize.Format((long)(drive.TotalBytes * (percent / 100.0)), provider ?? CultureInfo.CurrentCulture);

    /// <summary>"1 minute", "5 minutes", "1 hour".</summary>
    public static string DescribeMinutes(int minutes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        return minutes % 60 == 0
            ? string.Format(provider, minutes == 60 ? "{0} hour" : "{0} hours", minutes / 60)
            : string.Format(provider, minutes == 1 ? "{0} minute" : "{0} minutes", minutes);
    }
}
