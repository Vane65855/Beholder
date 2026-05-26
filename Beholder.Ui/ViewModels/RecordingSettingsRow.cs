using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Observable view-model for the Recording section of the Settings tab. Pure
/// state holder — the toggle commands live on <see cref="SettingsTabViewModel"/>
/// (they need access to <see cref="Services.IDaemonClient"/>). Splitting the
/// section state into its own row VM keeps the parent tab VM from sprawling
/// past CLAUDE.md's ~200 LOC soft threshold as Phase 13.3/13.4/13.5 add more
/// sections.
/// </summary>
internal sealed partial class RecordingSettingsRow : ObservableObject {
    /// <summary>
    /// When true, the daemon drops flow events whose process is Beholder
    /// itself. Toggled directly via optimistic-flip in
    /// <c>SettingsTabViewModel.ToggleFilterSelfTrafficCommand</c>; settles to
    /// the daemon's echoed value on RPC success, reverts on failure.
    /// </summary>
    [ObservableProperty]
    private bool _filterSelfTraffic;

    /// <summary>
    /// True while a Set RPC is in flight. The toggle pill renders disabled
    /// (greyed-out, no click) to prevent the user from queuing multiple
    /// in-flight RPCs for the same toggle.
    /// </summary>
    [ObservableProperty]
    private bool _isSaving;
}
