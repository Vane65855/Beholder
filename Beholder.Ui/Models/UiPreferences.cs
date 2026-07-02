namespace Beholder.Ui.Models;

/// <summary>
/// Persisted UI-local (per-user) preferences for the desktop app — window
/// behaviors that are deliberately NOT daemon settings, so they never reach the
/// daemon's chain-audited settings (ADR 010). Serialised as JSON by
/// <see cref="Services.IUiPreferencesStore"/>.
/// </summary>
internal sealed record UiPreferences {
    /// <summary>
    /// When true, the close (X) button hides the window to the system tray
    /// instead of exiting; the daemon keeps monitoring regardless. Default on.
    /// </summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>
    /// Set once the first-time "still running in the tray" hint has been shown,
    /// so it isn't repeated.
    /// </summary>
    public bool TrayHintShown { get; init; }

    /// <summary>
    /// When true, processes on the daemon's "Exclude from totals" list stay
    /// visible in the Traffic tab's process list with a marker glyph; when
    /// false (default) their rows are hidden. Display-only — excluded
    /// processes are skipped from the totals either way, which is why this
    /// preference is UI-local rather than a daemon setting.
    /// </summary>
    public bool ShowExcludedProcesses { get; init; }
}
