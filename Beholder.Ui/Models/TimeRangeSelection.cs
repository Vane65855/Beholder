using System;

namespace Beholder.Ui.Models;

/// <summary>
/// Named time-range presets for the Traffic tab's range selector dropdown.
/// <see cref="Last5Minutes"/> is the only live-streaming preset; all others
/// trigger a historical query against the daemon's tiered storage.
/// </summary>
public enum TimeRangePreset {
    Last5Minutes,
    Last1Hour,
    Last24Hours,
    Last7Days,
    Last30Days,
    AllTime,
    Custom,
}

/// <summary>
/// Represents the user's selected time range in the Traffic tab. Either a
/// named <see cref="TimeRangePreset"/> (with <see cref="From"/>/<see cref="To"/>
/// computed relative to now) or a custom user-picked date range.
/// </summary>
public sealed class TimeRangeSelection {
    public TimeRangePreset Preset { get; }
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public string Label { get; }

    /// <summary>
    /// True only for <see cref="TimeRangePreset.Last5Minutes"/> — the live
    /// streaming mode where the chart and process list update in real time
    /// from the circular buffers. All other presets (including Custom) use
    /// historical queries and produce a static snapshot.
    /// </summary>
    public bool IsLive => Preset == TimeRangePreset.Last5Minutes;

    private TimeRangeSelection(TimeRangePreset preset, DateTimeOffset from, DateTimeOffset to, string label) {
        Preset = preset;
        From = from;
        To = to;
        Label = label;
    }

    public static TimeRangeSelection FromPreset(TimeRangePreset preset) {
        var now = DateTimeOffset.UtcNow;
        return preset switch {
            TimeRangePreset.Last5Minutes => new(preset, now.AddMinutes(-5), now, "5 Minutes"),
            TimeRangePreset.Last1Hour => new(preset, now.AddHours(-1), now, "1 Hour"),
            TimeRangePreset.Last24Hours => new(preset, now.AddHours(-24), now, "24 Hours"),
            TimeRangePreset.Last7Days => new(preset, now.AddDays(-7), now, "Last 7 Days"),
            TimeRangePreset.Last30Days => new(preset, now.AddDays(-30), now, "Last 30 Days"),
            TimeRangePreset.AllTime => new(preset, DateTimeOffset.UnixEpoch, now, "All Time"),
            _ => new(TimeRangePreset.Last5Minutes, now.AddMinutes(-5), now, "5 Minutes"),
        };
    }

    public static TimeRangeSelection FromCustom(DateTimeOffset from, DateTimeOffset to) {
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        var label = $"{from:MMM d} – {to:MMM d}";
        return new(TimeRangePreset.Custom, from, to, label);
    }

    /// <summary>
    /// Build a Custom <see cref="TimeRangeSelection"/> from two local calendar
    /// dates the user picked (e.g., from <c>CalendarDatePicker.SelectedDate</c>,
    /// which returns an <see cref="DateTimeKind.Unspecified"/> <see cref="DateTime"/>
    /// representing a wall-clock date in local time). The returned range covers
    /// the full local days <c>[fromDate 00:00, toDate 23:59:59.9999999]</c> and is
    /// converted to <see cref="DateTimeOffset"/> using each instant's local UTC
    /// offset — correct across DST transitions between the two dates, unlike a
    /// single "now-offset" wrap.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="toDate"/> is earlier (by calendar day) than
    /// <paramref name="fromDate"/>.
    /// </exception>
    public static TimeRangeSelection FromLocalCalendarDates(DateTime fromDate, DateTime toDate) {
        if (toDate.Date < fromDate.Date)
            throw new ArgumentException(
                $"toDate ({toDate:yyyy-MM-dd}) must be on or after fromDate ({fromDate:yyyy-MM-dd}).",
                nameof(toDate));

        // SpecifyKind to Local so DateTimeOffset(DateTime) picks up the offset
        // at each specific instant (handles DST) rather than "now-offset".
        var localFrom = DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Local);
        var localTo = DateTime.SpecifyKind(
            toDate.Date.AddDays(1).AddTicks(-1),  // 23:59:59.9999999 of toDate
            DateTimeKind.Local);

        return FromCustom(new DateTimeOffset(localFrom), new DateTimeOffset(localTo));
    }

    /// <summary>
    /// Display labels for each preset, used by the dropdown's item list.
    /// </summary>
    public static string GetPresetLabel(TimeRangePreset preset) => preset switch {
        TimeRangePreset.Last5Minutes => "5 Minutes",
        TimeRangePreset.Last1Hour => "1 Hour",
        TimeRangePreset.Last24Hours => "24 Hours",
        TimeRangePreset.Last7Days => "Last 7 Days",
        TimeRangePreset.Last30Days => "Last 30 Days",
        TimeRangePreset.AllTime => "All Time",
        TimeRangePreset.Custom => "Custom...",
        _ => preset.ToString(),
    };

    /// <summary>
    /// Which group a preset belongs to in the dropdown (for visual separators).
    /// Group 0 = quick recent (5m/1h/24h), Group 1 = historical (7d/30d/all),
    /// Group 2 = custom.
    /// </summary>
    public static int GetPresetGroup(TimeRangePreset preset) => preset switch {
        TimeRangePreset.Last5Minutes or
        TimeRangePreset.Last1Hour or
        TimeRangePreset.Last24Hours => 0,
        TimeRangePreset.Last7Days or
        TimeRangePreset.Last30Days or
        TimeRangePreset.AllTime => 1,
        TimeRangePreset.Custom => 2,
        _ => 0,
    };
}
