using System;
using System.Collections.Generic;
using System.Linq;

namespace Beholder.Ui;

/// <summary>
/// Parses the UI's command-line arguments. The only option today is the
/// start-minimized-to-tray flag the installer's login-startup shortcut passes
/// (<c>--tray</c>), so the window comes up hidden in the tray instead of
/// popping on every sign-in.
/// </summary>
internal static class StartupOptions {
    /// <summary>
    /// True when the UI was launched with <c>--tray</c> or <c>--minimized</c>
    /// (case-insensitive) and should start hidden to the system tray.
    /// </summary>
    public static bool StartMinimizedToTray(IReadOnlyList<string>? args) =>
        args is not null && args.Any(a =>
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
}
