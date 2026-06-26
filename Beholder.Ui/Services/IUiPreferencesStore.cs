using Beholder.Ui.Models;

namespace Beholder.Ui.Services;

/// <summary>
/// Loads and saves the desktop app's UI-local <see cref="UiPreferences"/>. The
/// UI has no other client-side persistence; this is the single seam for
/// per-user window preferences (close-to-tray, etc.), kept out of the daemon so
/// they never enter its chain-audited settings (ADR 010).
/// </summary>
internal interface IUiPreferencesStore {
    /// <summary>
    /// Returns the persisted preferences, or defaults when none are saved yet or
    /// the file is unreadable.
    /// </summary>
    UiPreferences Load();

    /// <summary>
    /// Persists <paramref name="preferences"/>, overwriting any prior file.
    /// Best-effort: I/O failures are logged and swallowed, never thrown.
    /// </summary>
    void Save(UiPreferences preferences);
}
