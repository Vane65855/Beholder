using System;

namespace Beholder.Ui.Services;

/// <summary>
/// Testable wrapper around the OS "open this folder in the file explorer"
/// affordance, used by the Settings tab's Maintenance section. A non-trivial
/// interface (rather than calling <c>Process.Start</c> inline) so the
/// command's argument can be asserted in unit tests without launching a real
/// Explorer window.
/// </summary>
internal interface IFolderOpener {
    /// <summary>
    /// Opens the file explorer at <paramref name="path"/>. <paramref name="path"/>
    /// is typically the daemon's data directory (the parent of
    /// <c>beholder.db</c>); pointing it at a file rather than a directory is
    /// implementation-defined. Throws if the path doesn't exist.
    /// </summary>
    void OpenFolder(string path);
}
