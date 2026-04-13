namespace Beholder.Ui.Helpers;

internal static class ByteFormatter {
    public static string FormatBytes(long bytes) {
        if (bytes < 1024L) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static string FormatRate(long bytesPerSecond) =>
        $"{FormatBytes(bytesPerSecond)}/s";
}
