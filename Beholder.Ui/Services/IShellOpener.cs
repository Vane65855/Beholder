using System;

namespace Beholder.Ui.Services;

/// <summary>
/// Testable wrapper around the OS "open this thing in its default handler"
/// affordance. <see cref="Open(string)"/> works for filesystem paths
/// (launches the default file explorer at that location) and for URLs
/// (launches the default browser at that location), because the underlying
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// call with <c>UseShellExecute = true</c> dispatches both cases through
/// the shell's URI/file association tables.
/// </summary>
/// <remarks>
/// Collapsing what was previously a separate <c>IFolderOpener</c> into one
/// abstraction keeps the UI's "open something in the OS" surface area
/// minimal (one interface, one method, one test double) at the cost of a
/// slightly fuzzier type name. The fuzziness is justified by the DRY win:
/// <c>Process.Start { UseShellExecute = true }</c> has identical semantics
/// for both targets, so two interfaces would be near-duplicate code.
/// </remarks>
internal interface IShellOpener {
    /// <summary>
    /// Opens <paramref name="target"/> in the OS's default handler. For a
    /// filesystem path that names a directory, this opens the directory in
    /// the file explorer. For a URL (http/https/etc.), this opens it in the
    /// default browser. Throws if the target is not openable.
    /// </summary>
    void Open(string target);

    /// <summary>
    /// Opens the folder containing <paramref name="filePath"/> in the file
    /// explorer with that file pre-selected (Task Manager's "Open file
    /// location" behavior). On platforms without a select-in-explorer verb
    /// the containing folder opens without a selection. Throws if the
    /// location is not openable.
    /// </summary>
    void RevealInFolder(string filePath);
}
