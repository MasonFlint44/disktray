using System.Globalization;
using DiskTray.Core.Settings;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var settings = new AppSettings
        {
            Theme = AppTheme.Light,
            RefreshMinutes = 15,
            WarningPercent = 20,
            CriticalPercent = 8,
            NotifyOnDrop = false,
            DailyReminder = false,
            ReminderTime = new TimeOnly(7, 30),
            StartupRegistered = true,
            CleanupBuildCache = false,
            CleanupDanglingImages = false,
            CleanupDeletedProjectVolumes = false,
            CleanupCompact = false,
            CleanupCompactWhen = DiskTray.Core.Cleanup.CompactWhen.RightAway,
            CleanupStartDocker = false,
        };

        Assert.Equal(settings, AppSettings.FromJson(settings.ToJson()));
    }

    [Fact]
    public void Writes_readable_json()
    {
        string json = new AppSettings { Theme = AppTheme.Dark }.ToJson();

        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.Contains("\"reminderTime\": \"09:00:00\"", json);
        Assert.Contains("\"cleanupCompactWhen\": \"whenIdle\"", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Uses_the_defaults_when_there_is_no_file(string? json)
    {
        var (settings, dropped) = AppSettings.Read(json);

        Assert.Equal(new AppSettings(), settings);
        Assert.Empty(dropped);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "theme": "dark", """)]
    public void Uses_the_defaults_when_the_file_is_unusable_but_leaves_startup_apps_alone(string json)
    {
        var (settings, dropped) = AppSettings.Read(json);

        Assert.Equal(new AppSettings { StartupRegistered = true }, settings);
        Assert.Equal(["the whole file"], dropped);
    }

    [Fact]
    public void Keeps_everything_else_when_one_value_cant_be_read()
    {
        var (settings, dropped) = AppSettings.Read("""
            {
              "theme": "purple",
              "warningPercent": 20,
              "cleanupCompactWhen": "someday",
              "reminderTime": "soon",
              "refreshMinutes": "often",
              "codeFolders": ["C:\\code"],
              "pins": [{ "kind": "drive", "target": "D:\\" }, { "kind": "sparkly" }],
              "startupRegistered": false
            }
            """);

        Assert.Equal(["theme", "cleanupCompactWhen", "reminderTime", "refreshMinutes", "some of pins"], dropped);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(20, settings.WarningPercent);
        Assert.Equal(5, settings.RefreshMinutes);
        Assert.Equal([@"C:\code"], settings.CodeFolders);
        Assert.Equal([new TrayPin(PinKind.Drive, @"D:\")], settings.Pins);
        Assert.False(settings.StartupRegistered);
    }

    [Fact]
    public void Counts_a_startup_apps_value_it_cant_read_as_done()
    {
        var (settings, dropped) = AppSettings.Read("""{ "startupRegistered": "maybe", "theme": "dark" }""");

        Assert.Equal(["startupRegistered"], dropped);
        Assert.Equal(new AppSettings { Theme = AppTheme.Dark, StartupRegistered = true }, settings);
    }

    [Fact]
    public void Keeps_good_values_and_resets_bad_ones()
    {
        var settings = AppSettings.FromJson("""{ "theme": "Dark", "refreshMinutes": 0, "warningPercent": 10, "criticalPercent": 12 }""");

        Assert.Equal(new AppSettings { Theme = AppTheme.Dark }, settings);
    }

    [Fact]
    public void Ignores_settings_it_doesnt_know() =>
        Assert.Equal(new AppSettings { RefreshMinutes = 30 }, AppSettings.FromJson("""{ "refreshMinutes": 30, "sparkles": "lots" }"""));

    [Theory]
    [InlineData(15, 5, null)]
    [InlineData(1, 0, null)]
    [InlineData(0, 0, "The warning level has to be between 1% and 50%.")]
    [InlineData(51, 5, "The warning level has to be between 1% and 50%.")]
    [InlineData(15, -1, "The critical level can't be below 0%.")]
    [InlineData(15, 15, "The critical level has to be below the warning level.")]
    public void Checks_the_alert_levels(int warning, int critical, string? expected) =>
        Assert.Equal(expected, new AppSettings { WarningPercent = warning, CriticalPercent = critical }.LevelsProblem());

    [Fact]
    public void Turns_levels_into_thresholds()
    {
        var thresholds = new AppSettings { WarningPercent = 20, CriticalPercent = 8 }.Thresholds;

        Assert.Equal(0.20, thresholds.WarningFreeFraction, 9);
        Assert.Equal(0.08, thresholds.CriticalFreeFraction, 9);
    }

    [Fact]
    public void Shows_levels_as_space_on_the_drive() =>
        Assert.Equal("≈ 71 GiB", AppSettings.ApproximateSpace(15, new DriveSpace(@"C:\", Machine.DriveTotal, 0), CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(5, "5 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    public void Describes_refresh_intervals(int minutes, string expected) =>
        Assert.Equal(expected, AppSettings.DescribeMinutes(minutes, CultureInfo.InvariantCulture));
}
