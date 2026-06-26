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
}
