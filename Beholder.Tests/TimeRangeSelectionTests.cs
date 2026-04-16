using Beholder.Ui.Models;

namespace Beholder.Tests;

public class TimeRangeSelectionTests {
    // ---- FromLocalCalendarDates ----

    [Fact]
    public void FromLocalCalendarDates_SameDate_CoversFullLocalDay() {
        var date = new DateTime(2026, 4, 14);

        var range = TimeRangeSelection.FromLocalCalendarDates(date, date);

        // Start of local day → end of same local day at 23:59:59.9999999
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Local)),
            range.From);
        var expectedEnd = new DateTimeOffset(
            new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Local)
                .AddDays(1).AddTicks(-1));
        Assert.Equal(expectedEnd, range.To);
    }

    [Fact]
    public void FromLocalCalendarDates_MultiDay_IncludesEntireEndDate() {
        var from = new DateTime(2026, 4, 10);
        var to = new DateTime(2026, 4, 14);

        var range = TimeRangeSelection.FromLocalCalendarDates(from, to);

        // Range spans >4 days (last instant of Apr 14 is > 4 full days after
        // start of Apr 10). Exact: 5 days minus 1 tick.
        var span = range.To - range.From;
        Assert.Equal(TimeSpan.FromDays(5) - TimeSpan.FromTicks(1), span);
    }

    [Fact]
    public void FromLocalCalendarDates_InvertedDates_Throws() {
        var from = new DateTime(2026, 4, 14);
        var to = new DateTime(2026, 4, 10);

        var ex = Assert.Throws<ArgumentException>(
            () => TimeRangeSelection.FromLocalCalendarDates(from, to));
        Assert.Equal("toDate", ex.ParamName);
    }

    [Fact]
    public void FromLocalCalendarDates_UsesLocalOffsetPerInstant() {
        // The factory must convert local dates to DateTimeOffset using
        // TimeZoneInfo.Local.GetUtcOffset at each date, not a single shared
        // offset. Verify both endpoints' UtcDateTime equals the picked local
        // midnight (or end-of-day) translated by each date's own local offset.
        var from = new DateTime(2026, 4, 10);
        var to = new DateTime(2026, 4, 14);

        var range = TimeRangeSelection.FromLocalCalendarDates(from, to);

        var expectedFromUtc = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var expectedToUtc = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Local)
            .AddDays(1).AddTicks(-1).ToUniversalTime();

        Assert.Equal(expectedFromUtc, range.From.UtcDateTime);
        Assert.Equal(expectedToUtc, range.To.UtcDateTime);
    }

    [Fact]
    public void FromLocalCalendarDates_PresetIsCustom() {
        var range = TimeRangeSelection.FromLocalCalendarDates(
            new DateTime(2026, 4, 10), new DateTime(2026, 4, 14));

        Assert.Equal(TimeRangePreset.Custom, range.Preset);
        Assert.False(range.IsLive);
    }

    [Fact]
    public void FromLocalCalendarDates_IgnoresTimeComponent() {
        // DateTime.Date stripping means an input with a time-of-day still
        // produces a full-day range starting at local midnight.
        var fromWithTime = new DateTime(2026, 4, 10, 15, 30, 45);
        var toWithTime = new DateTime(2026, 4, 10, 9, 0, 0);

        var range = TimeRangeSelection.FromLocalCalendarDates(fromWithTime, toWithTime);

        Assert.Equal(0, range.From.Hour);
        Assert.Equal(0, range.From.Minute);
        Assert.Equal(0, range.From.Second);
        Assert.Equal(23, range.To.Hour);
        Assert.Equal(59, range.To.Minute);
    }

    // ---- FromPreset ----

    [Fact]
    public void FromPreset_Last5Minutes_IsLive() {
        var range = TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes);

        Assert.Equal(TimeRangePreset.Last5Minutes, range.Preset);
        Assert.True(range.IsLive);
        Assert.Equal("5 Minutes", range.Label);
        Assert.InRange((range.To - range.From).TotalSeconds, 299, 301);
    }

    [Fact]
    public void FromPreset_Last1Hour_SpansOneHour() {
        var range = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);

        Assert.False(range.IsLive);
        Assert.Equal("1 Hour", range.Label);
        Assert.InRange((range.To - range.From).TotalMinutes, 59.99, 60.01);
    }

    [Fact]
    public void FromPreset_Last24Hours_Spans24Hours() {
        var range = TimeRangeSelection.FromPreset(TimeRangePreset.Last24Hours);

        Assert.Equal("24 Hours", range.Label);
        Assert.InRange((range.To - range.From).TotalHours, 23.99, 24.01);
    }

    [Fact]
    public void FromPreset_Last7Days_Spans7Days() {
        var range = TimeRangeSelection.FromPreset(TimeRangePreset.Last7Days);

        Assert.Equal("Last 7 Days", range.Label);
        Assert.InRange((range.To - range.From).TotalDays, 6.99, 7.01);
    }

    [Fact]
    public void FromPreset_Last30Days_Spans30Days() {
        var range = TimeRangeSelection.FromPreset(TimeRangePreset.Last30Days);

        Assert.Equal("Last 30 Days", range.Label);
        Assert.InRange((range.To - range.From).TotalDays, 29.99, 30.01);
    }

    [Fact]
    public void FromPreset_AllTime_StartsAtUnixEpoch() {
        var range = TimeRangeSelection.FromPreset(TimeRangePreset.AllTime);

        Assert.Equal("All Time", range.Label);
        Assert.Equal(DateTimeOffset.UnixEpoch, range.From);
    }

    // ---- FromCustom ----

    [Fact]
    public void FromCustom_PresetIsCustom() {
        var from = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 4, 14, 23, 59, 59, TimeSpan.Zero);

        var range = TimeRangeSelection.FromCustom(from, to);

        Assert.Equal(TimeRangePreset.Custom, range.Preset);
        Assert.False(range.IsLive);
        Assert.Equal(from, range.From);
        Assert.Equal(to, range.To);
    }

    [Fact]
    public void FromCustom_LabelShowsShortDates() {
        var from = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 4, 14, 23, 59, 59, TimeSpan.Zero);

        var range = TimeRangeSelection.FromCustom(from, to);

        Assert.Contains("Apr 10", range.Label);
        Assert.Contains("Apr 14", range.Label);
    }
}
