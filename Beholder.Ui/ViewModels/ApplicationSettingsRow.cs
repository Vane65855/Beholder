using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Observable view-model for the Application section of the Settings tab — the
/// UI-local (non-daemon) window preferences. The toggle command lives on
/// <see cref="SettingsTabViewModel"/>. Unlike the daemon-backed sections there
/// is no <c>IsSaving</c> flag: the write goes to the local
/// <see cref="Services.IUiPreferencesStore"/> and is instant, not an RPC round-trip.
/// </summary>
internal sealed partial class ApplicationSettingsRow : ObservableObject {
    /// <summary>
    /// When true, the close (X) button hides the window to the system tray
    /// instead of exiting. Persisted locally; read live by <c>TrayController</c>.
    /// </summary>
    [ObservableProperty]
    private bool _closeToTray;
}
