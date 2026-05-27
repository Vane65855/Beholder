using System.Threading;
using System.Threading.Tasks;

namespace Beholder.Ui.Services;

/// <summary>
/// Testable wrapper around an OS "pick a single file" dialog. Returns the
/// absolute path of the chosen file, or <c>null</c> if the user cancelled.
/// Introduced for Phase 13.6's Application Identity Overrides surface — the
/// user picks the offending binary and the VM derives the anchor + filename
/// from the path.
/// </summary>
internal interface IFilePicker {
    /// <summary>
    /// Opens a single-file picker. Returns the picked path on success, or
    /// <c>null</c> when the user cancels. The <paramref name="title"/> is
    /// rendered as the dialog title; on Windows the picker is restricted to
    /// <c>*.exe</c> by default, but the picker should also expose an
    /// "All files" fallback so the user can pick a binary without a <c>.exe</c>
    /// extension (rare, but possible for service hosts, MSIX-wrapped exes,
    /// etc.).
    /// </summary>
    Task<string?> PickFileAsync(string title, CancellationToken cancellationToken);
}
