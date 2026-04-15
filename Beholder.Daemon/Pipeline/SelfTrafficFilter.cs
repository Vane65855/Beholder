namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Identifies Beholder's own processes by executable filename. Used by
/// <see cref="FlowEventPipeline"/> to drop self-traffic before it enters the
/// ingestion channel. Exact filename match (not substring) prevents false
/// positives from unrelated binaries whose paths happen to contain the word
/// "Beholder".
/// </summary>
internal static class SelfTrafficFilter {
    // Both .exe (Windows) and no-extension (Linux/macOS) forms are listed so
    // the filter works unchanged across current and future deployment targets.
    private static readonly HashSet<string> SelfExecutables =
        new(StringComparer.OrdinalIgnoreCase) {
            "Beholder.Daemon.exe",
            "Beholder.Daemon",
            "Beholder.Ui.exe",
            "Beholder.Ui",
        };

    /// <summary>
    /// Returns true when the filename of <paramref name="processPath"/> is
    /// one of the known Beholder executables. Case-insensitive; exact
    /// filename match, never a substring.
    /// </summary>
    public static bool IsSelfProcess(string processPath) {
        if (string.IsNullOrEmpty(processPath)) return false;
        var fileName = Path.GetFileName(processPath);
        return SelfExecutables.Contains(fileName);
    }
}
