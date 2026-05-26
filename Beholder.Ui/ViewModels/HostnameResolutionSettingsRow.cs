using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Observable view-model for the Hostname Resolution section of the Settings
/// tab. Same shape as <see cref="RecordingSettingsRow"/> but with three
/// independently-toggleable bools. Only <c>EnableReverseDnsFallback</c> takes
/// effect immediately; the other two are persisted but their daemon-side
/// consumers snapshot at startup, so the Settings view renders a
/// "(takes effect on next daemon start)" caption next to those pills.
/// </summary>
internal sealed partial class HostnameResolutionSettingsRow : ObservableObject {
    [ObservableProperty]
    private bool _enablePreload;

    [ObservableProperty]
    private bool _enableReverseDnsFallback;

    [ObservableProperty]
    private bool _enableSniCapture;

    /// <summary>Saving flag for the EnablePreload pill (snapshot-at-startup).</summary>
    [ObservableProperty]
    private bool _isSavingPreload;

    /// <summary>Saving flag for the EnableReverseDnsFallback pill (live).</summary>
    [ObservableProperty]
    private bool _isSavingReverseDnsFallback;

    /// <summary>Saving flag for the EnableSniCapture pill (snapshot-at-startup).</summary>
    [ObservableProperty]
    private bool _isSavingSniCapture;
}
