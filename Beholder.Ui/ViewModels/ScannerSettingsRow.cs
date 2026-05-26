using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Observable view-model for the Scanner section of the Settings tab. One
/// toggle today (<see cref="EnableHostnameResolution"/>) — pure state holder,
/// the toggle command lives on <see cref="SettingsTabViewModel"/>. The
/// toggle takes effect on the next scan tick (Phase 13.4 lifted the
/// snapshot-at-startup gate into a per-scan read inside
/// <c>WindowsLanDeviceProbe.ScanAsync</c>).
/// </summary>
internal sealed partial class ScannerSettingsRow : ObservableObject {
    [ObservableProperty]
    private bool _enableHostnameResolution;

    /// <summary>Saving flag for the EnableHostnameResolution pill.</summary>
    [ObservableProperty]
    private bool _isSavingHostnameResolution;
}
