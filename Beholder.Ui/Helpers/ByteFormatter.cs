using System;

namespace Beholder.Ui.Helpers;

internal static class ByteFormatter {
    // Format with the invariant culture so byte/rate labels read identically on
    // every machine ("1.5 MB", not "1,5 MB" on comma-decimal locales). These are
    // canonical technical values, not localized prose.
    public static string FormatBytes(long bytes) {
        if (bytes < 1024L) return FormattableString.Invariant($"{bytes} B");
        if (bytes < 1024L * 1024) return FormattableString.Invariant($"{bytes / 1024.0:F1} KB");
        if (bytes < 1024L * 1024 * 1024) return FormattableString.Invariant($"{bytes / (1024.0 * 1024):F1} MB");
        return FormattableString.Invariant($"{bytes / (1024.0 * 1024 * 1024):F2} GB");
    }

    public static string FormatRate(long bytesPerSecond) =>
        $"{FormatBytes(bytesPerSecond)}/s";
}
