using System;
using System.Diagnostics;
using System.IO;

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

    public void RevealInFolder(string filePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (OperatingSystem.IsWindows()) {
            // Explorer's /select verb opens the containing folder with the
            // file highlighted. Windows paths cannot contain quotes, so the
            // literal quoting is safe.
            Process.Start(new ProcessStartInfo {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true,
            });
        } else {
            // No portable select-in-explorer verb off Windows — opening the
            // containing folder is the graceful degradation.
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException($"'{filePath}' has no containing folder.", nameof(filePath));
            Open(directory);
        }
    }
}
