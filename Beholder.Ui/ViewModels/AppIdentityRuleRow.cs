using System;
using System.IO;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Immutable view-model for one row in the Settings tab's Application
/// Identity Overrides list. Pure data holder; the REMOVE command lives on
/// <see cref="SettingsTabViewModel"/> (it needs <see cref="Services.IDaemonClient"/>).
/// </summary>
internal sealed class AppIdentityRuleRow {
    public int Id { get; }
    public string AnchorPath { get; }
    public string Filename { get; }
    public string? DisplayName { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Human-readable label: the user-supplied <see cref="DisplayName"/> when
    /// present, otherwise the filename minus its extension (e.g.,
    /// <c>Discord.exe</c> → <c>Discord</c>). Drives the bold header line in
    /// each rule's list-entry card.
    /// </summary>
    public string Label =>
        !string.IsNullOrEmpty(DisplayName)
            ? DisplayName
            : Path.GetFileNameWithoutExtension(Filename);

    public AppIdentityRuleRow(
        int id, string anchorPath, string filename, string? displayName,
        DateTimeOffset createdAt
    ) {
        Id = id;
        AnchorPath = anchorPath;
        Filename = filename;
        DisplayName = displayName;
        CreatedAt = createdAt;
    }
}
