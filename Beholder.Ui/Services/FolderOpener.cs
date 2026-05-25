using System;
using System.Diagnostics;

namespace Beholder.Ui.Services;

/// <summary>
/// Default <see cref="IFolderOpener"/>: launches the shell's default
/// directory handler (Explorer on Windows, Finder on macOS, xdg-open's
/// chosen file manager on Linux) via <see cref="Process.Start(ProcessStartInfo)"/>
/// with <c>UseShellExecute = true</c>. Cross-platform out of the box; no
/// PLATFORM_WINDOWS guard required.
/// </summary>
internal sealed class FolderOpener : IFolderOpener {
    public void OpenFolder(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
