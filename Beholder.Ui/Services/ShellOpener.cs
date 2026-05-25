using System;
using System.Diagnostics;

namespace Beholder.Ui.Services;

/// <summary>
/// Default <see cref="IShellOpener"/>: launches the OS's default handler
/// for the target via <see cref="Process.Start(ProcessStartInfo)"/> with
/// <c>UseShellExecute = true</c>. Cross-platform out of the box — the
/// shell-execute path dispatches to Explorer (Windows), Finder (macOS), or
/// xdg-open's chosen handler (Linux) and handles both file paths and URLs
/// via the same mechanism.
/// </summary>
internal sealed class ShellOpener : IShellOpener {
    public void Open(string target) {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Process.Start(new ProcessStartInfo {
            FileName = target,
            UseShellExecute = true,
        });
    }
}
