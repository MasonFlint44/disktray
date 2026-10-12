using System.Globalization;
using DiskTray.Core.Alerts;
using DiskTray.Core.Tray;

namespace DiskTray.Core.Tests;

public class AlertRulesTests
{
    private const long GiB = 1L << 30;
    private static readonly AlertOptions Options = new(NotifyOnDrop: true, DailyReminder: true, new TimeOnly(9, 0));

    // 8 AM on Oct 11, before the reminder time.
    private static readonly DateTimeOffset Morning = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);

    /// <summary>A 1000 GiB drive with <paramref name="freeGiB"/> free: warning below 150, critical below 50.</summary>
    private static DiskSnapshot Drive(double freeGiB) =>
        new(DateTimeOffset.UnixEpoch, new DriveSpace(@"C:\", 1000 * GiB, (long)(freeGiB * GiB)), [new DriveSpace(@"C:\", 1000 * GiB, (long)(freeGiB * GiB))], [], null);

    private static (AlertState State, AlertToast? Toast) Check(
        AlertState state, double freeGiB, DateTimeOffset? now = null, bool userActive = true, AlertOptions? options = null) =>
        AlertRules.Check(state, Drive(freeGiB), new AlertThresholds(), options ?? Options, now ?? Morning, userActive, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

    [Fact]
    public void Stays_quiet_while_there_is_room()
    {
        var (state, toast) = Check(new AlertState(), 400);

        Assert.Null(toast);
        Assert.Equal(new AlertState(), state);
    }

    [Fact]
    public void Notifies_when_free_space_drops_below_the_warning_level()
    {
        var (state, toast) = Check(new AlertState(), 120);

        Assert.Equal(new AlertToast(AlertKind.Drop, AlertLevel.Warning, "Drive C: is getting full", "120 GiB free (12%) – below your 15% warning level"), toast);
        Assert.Equal(new AlertState(AlertLevel.Warning, Today), state);
    }

    [Fact]
    public void Notifies_once_per_level()
    {
        var (state, _) = Check(new AlertState(), 120);
        var (again, quiet) = Check(state, 110);
        var (critical, toast) = Check(again, 40);

        Assert.Null(quiet);
        Assert.Equal(AlertKind.Drop, toast?.Kind);
        Assert.Equal("Drive C: is almost full", toast?.Title);
        Assert.Equal(AlertLevel.Critical, critical.Fired);
    }

    [Fact]
    public void Doesnt_flap_around_a_level()
    {
        var (state, _) = Check(new AlertState(), 149);
        var (justAbove, _) = Check(state, 151);
        var (below, toast) = Check(justAbove, 149);

        Assert.Equal(AlertLevel.Warning, justAbove.Fired);
        Assert.Null(toast);
        Assert.Equal(AlertLevel.Warning, below.Fired);
    }

    [Fact]
    public void Rearms_once_free_space_climbs_a_few_gib_above_the_level()
    {
        var (state, _) = Check(new AlertState(), 40);
        var (aboveCritical, _) = Check(state, 54);
        var (aboveWarning, _) = Check(aboveCritical, 154);
        var (_, toast) = Check(aboveWarning, 140, Morning.AddDays(1));

        Assert.Equal(AlertLevel.Warning, aboveCritical.Fired);
        Assert.Equal(AlertLevel.Normal, aboveWarning.Fired);
        Assert.Equal(AlertKind.Drop, toast?.Kind);
    }

    [Fact]
    public void Reminds_once_a_day_after_the_reminder_time()
    {
        var yesterday = new AlertState(AlertLevel.Warning, Today.AddDays(-1));

        var (_, early) = Check(yesterday, 120);
        var (state, toast) = Check(yesterday, 120, Morning.AddHours(1));
        var (_, again) = Check(state, 120, Morning.AddHours(5));

        Assert.Null(early);
        Assert.Equal(new AlertToast(AlertKind.Reminder, AlertLevel.Warning, "Drive C: is still low on space", "120 GiB free (12%) – below your 15% warning level"), toast);
        Assert.Equal(Today, state.LastNotified);
        Assert.Null(again);
    }

    [Fact]
    public void Waits_for_the_user_to_be_at_the_pc()
    {
        var yesterday = new AlertState(AlertLevel.Critical, Today.AddDays(-1));

        var (away, none) = Check(yesterday, 40, Morning.AddHours(2), userActive: false);
        var (_, toast) = Check(away, 40, Morning.AddHours(3));

        Assert.Null(none);
        Assert.Equal("Drive C: is still almost full", toast?.Title);
    }

    [Fact]
    public void Doesnt_remind_on_the_day_it_dropped()
    {
        var (state, _) = Check(new AlertState(), 120, Morning.AddHours(2));
        var (_, toast) = Check(state, 120, Morning.AddHours(3));

        Assert.Null(toast);
    }

    [Fact]
    public void Doesnt_remind_inside_the_margin_once_there_is_room_again()
    {
        var (_, toast) = Check(new AlertState(AlertLevel.Warning, Today.AddDays(-1)), 151, Morning.AddHours(2));

        Assert.Null(toast);
    }

    [Fact]
    public void Follows_the_notification_options()
    {
        var off = new AlertOptions(NotifyOnDrop: false, DailyReminder: false, new TimeOnly(9, 0));

        var (state, drop) = Check(new AlertState(), 120, Morning.AddHours(2), options: off);
        var (_, reminder) = Check(state, 120, Morning.AddHours(3), options: off);
        var (_, remindersOnly) = Check(state, 120, Morning.AddHours(3), options: off with { DailyReminder = true });

        Assert.Null(drop);
        Assert.Equal(AlertLevel.Warning, state.Fired);
        Assert.Null(reminder);
        Assert.Equal(AlertKind.Reminder, remindersOnly?.Kind);
    }

    [Fact]
    public void Rearms_quietly_when_drop_notifications_are_off()
    {
        var off = Options with { NotifyOnDrop = false, DailyReminder = false };

        var (state, quiet) = Check(new AlertState(), 120, options: off);
        var (rearmed, _) = Check(state, 160, options: off);
        var (_, toast) = Check(rearmed, 120, Morning.AddDays(1));

        Assert.Null(quiet);
        Assert.Equal(AlertLevel.Normal, rearmed.Fired);
        Assert.Equal(AlertKind.Drop, toast?.Kind);
    }

    [Fact]
    public void Mentions_what_compacting_would_free()
    {
        var file = new DiskFile("docker", "Docker data", @"C:\d.vhdx", DiskKind.DockerData, 130 * GiB) { UsedBytes = 36 * GiB };
        var snapshot = Drive(120) with { Files = [file] };

        var (_, toast) = AlertRules.Check(new AlertState(), snapshot, new AlertThresholds(), Options, Morning, true, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        Assert.EndsWith("\n94 GiB is reclaimable by compacting.", toast?.Body);
    }

    [Fact]
    public void Round_trips_state_through_json()
    {
        var state = new AlertState(AlertLevel.Critical, Today);

        Assert.Equal(state, AlertState.FromJson(state.ToJson()));
        Assert.Contains("\"fired\": \"critical\"", state.ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    [InlineData("""{ "fired": "purple" }""")]
    [InlineData("""{ "fired": 7 }""")]
    public void Starts_over_when_state_is_unusable(string? json) => Assert.Equal(new AlertState(), AlertState.FromJson(json));
}
