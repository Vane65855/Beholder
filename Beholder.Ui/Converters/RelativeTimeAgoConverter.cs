using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Beholder.Ui.Converters;

/// <summary>
/// Formats a <see cref="DateTimeOffset"/> as a relative-time label
/// ("5s ago", "3m ago", "2h ago", "yesterday") with a fallback to an
/// absolute date for values older than a week. Used by the Scanner
/// tab's device list and detail pane to surface "when was this device
/// last seen?" without requiring the ViewModel to maintain a backing
/// label property for every row.
/// </summary>
/// <remarks>
/// Compares the bound value against <see cref="DateTimeOffset.UtcNow"/>
/// at conversion time. This means re-binding the same row (e.g. on
/// every layout pass) refreshes the label naturally — but it also
/// means rows that don't re-bind silently go stale. The Scanner tab
/// pairs this converter with a 1-second VM-side ticker that forces
/// a property-changed notification on each visible row's
/// <c>LastSeen</c>, ensuring labels stay live.
/// </remarks>
internal sealed class RelativeTimeAgoConverter : IValueConverter {
    public static readonly RelativeTimeAgoConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is not DateTimeOffset timestamp) return string.Empty;
        return Format(timestamp, DateTimeOffset.UtcNow);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>
    /// Pure formatter exposed for ViewModel-side use (e.g. the
    /// Scanner tab's 1-second ticker writes the label directly into
    /// <c>LanDeviceRow.LastSeenLabel</c> without going through the
    /// XAML binding path).
    /// </summary>
    internal static string Format(DateTimeOffset timestamp, DateTimeOffset now) {
        var elapsed = now - timestamp;
        if (elapsed.TotalSeconds < 0) return "just now";     // clock skew
        if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 2) return "yesterday";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
        return timestamp.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
