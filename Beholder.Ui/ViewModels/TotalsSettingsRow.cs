using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Observable view-model for the Traffic Totals section of the Settings tab —
/// the "Exclude from totals" list plus the UI-local show-excluded display
/// preference. Pure state holder; the add / remove / toggle commands live on
/// <see cref="SettingsTabViewModel"/> (they need <see cref="Services.IDaemonClient"/>,
/// the file picker, and the preferences store).
/// </summary>
internal sealed partial class TotalsSettingsRow : ObservableObject {
    /// <summary>Excluded process paths, in the daemon's list order.</summary>
    public ObservableCollection<string> ExcludedPaths { get; } = [];

    /// <summary>
    /// UI-local display preference: when true, excluded processes stay in the
    /// Traffic tab's list with a ⊘ marker instead of being hidden. Never
    /// affects the totals math.
    /// </summary>
    [ObservableProperty]
    private bool _showExcluded;

    /// <summary>Saving flag while a Set RPC round-trip is in flight.</summary>
    [ObservableProperty]
    private bool _isSaving;
}
