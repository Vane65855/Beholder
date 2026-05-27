using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Section-row VM for Phase 13.6's Application Identity Overrides settings
/// section. Holds the list of persisted rules + the add-mode state (picked
/// file, derived filename/variable/anchor, validation). The commands
/// (BeginAddRule / CancelAddRule / PickFile / SaveRule / RemoveRule) live on
/// <see cref="SettingsTabViewModel"/> because they need
/// <see cref="Services.IDaemonClient"/> and <see cref="Services.IFilePicker"/>.
/// </summary>
internal sealed partial class AppIdentityRulesRow : ObservableObject {
    /// <summary>Rules fetched from the daemon, in insertion (id) order.</summary>
    public ObservableCollection<AppIdentityRuleRow> Rules { get; } = new();

    /// <summary>
    /// True when the section is in ADD mode (input form visible). False when
    /// just showing the list + ADD RULE button. Mirrors the Phase 9.5
    /// IsEditingLabel pattern from the Scanner tab.
    /// </summary>
    [ObservableProperty]
    private bool _isAdding;

    /// <summary>True while the rules list is being loaded from the daemon.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>True while the SAVE RPC is in flight (locks the SAVE button).</summary>
    [ObservableProperty]
    private bool _isSaving;

    /// <summary>The full path the user picked via the file picker. Null until picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPickedFile))]
    private string? _pickedFilePath;

    public bool HasPickedFile => !string.IsNullOrEmpty(PickedFilePath);

    /// <summary>
    /// Auto-derived from <see cref="PickedFilePath"/>: <c>Path.GetFileName</c>.
    /// Read-only in the UI; recomputed when the picked path changes.
    /// </summary>
    [ObservableProperty]
    private string _filename = string.Empty;

    /// <summary>
    /// Auto-detected variable segment (immediate parent of the picked file).
    /// Read-only info display so the user can sanity-check what's being
    /// treated as "the folder that changes between versions."
    /// </summary>
    [ObservableProperty]
    private string _variableSegment = string.Empty;

    /// <summary>
    /// Editable anchor full path. Auto-populated to the grandparent of the
    /// picked file (the stable parent above the variable segment). The user
    /// can adjust if the auto-detection picked the wrong level.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchPreview))]
    private string _anchorPath = string.Empty;

    /// <summary>Optional user-supplied label for the rule (UI display only).</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// Inline error message when validation fails (typically: file isn't
    /// exactly one segment below the configured anchor). Empty string when
    /// the form is valid OR when no file has been picked yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string _validationError = string.Empty;

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    /// <summary>
    /// Live preview of what the rule will match. Computed from
    /// <see cref="AnchorPath"/> + <see cref="Filename"/>. Drives the
    /// "Will match: ..." line in the ADD card so the user sees the rule's
    /// behaviour before saving.
    /// </summary>
    public string MatchPreview {
        get {
            if (string.IsNullOrEmpty(AnchorPath) || string.IsNullOrEmpty(Filename))
                return string.Empty;
            return $"Will match: {AnchorPath}{Path.DirectorySeparatorChar}<any single subfolder>{Path.DirectorySeparatorChar}{Filename}";
        }
    }

    /// <summary>
    /// True when SAVE is enabled: a file is picked, the anchor is non-empty,
    /// no validation error is active, and a save isn't currently in flight.
    /// </summary>
    public bool CanSave => HasPickedFile
        && !string.IsNullOrEmpty(AnchorPath)
        && !HasValidationError
        && !IsSaving;

    partial void OnPickedFilePathChanged(string? value) {
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnAnchorPathChanged(string value) {
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnValidationErrorChanged(string value) {
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnIsSavingChanged(bool value) {
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Resets the add-mode state. Called on CANCEL and after a successful SAVE.
    /// </summary>
    public void ResetAddState() {
        IsAdding = false;
        PickedFilePath = null;
        Filename = string.Empty;
        VariableSegment = string.Empty;
        AnchorPath = string.Empty;
        DisplayName = string.Empty;
        ValidationError = string.Empty;
    }
}
