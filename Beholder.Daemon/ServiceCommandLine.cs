namespace Beholder.Daemon;

/// <summary>
/// The action the daemon executable was asked to perform, parsed from its
/// command-line arguments (Phase 12.1). Everything except <see cref="Run"/> is a
/// one-shot Windows service-control operation that runs and exits before the
/// host is built.
/// </summary>
public enum ServiceCommand {
    /// <summary>Run the daemon host — the default, whether under the SCM or a console.</summary>
    Run,

    /// <summary>Register the daemon as a Windows service (<c>--install</c>).</summary>
    Install,

    /// <summary>Remove the Windows service registration (<c>--uninstall</c>).</summary>
    Uninstall,

    /// <summary>Print the Windows service's current state (<c>--status</c>).</summary>
    Status,
}

/// <summary>
/// Maps the daemon's command-line arguments to a <see cref="ServiceCommand"/>.
/// Pure and platform-agnostic so the mapping is unit-testable; the Windows-only
/// execution of the non-<see cref="ServiceCommand.Run"/> commands lives in
/// <c>Beholder.Daemon.Windows.WindowsServiceInstaller</c>.
/// </summary>
public static class ServiceCommandLine {
    /// <summary>
    /// Returns the command named by the first recognized verb in
    /// <paramref name="args"/>. Both <c>--install</c> and <c>install</c> forms
    /// are accepted, case-insensitively. Anything unrecognized — or no args —
    /// maps to <see cref="ServiceCommand.Run"/>: an unknown flag must never
    /// silently skip starting the daemon.
    /// </summary>
    public static ServiceCommand Parse(IReadOnlyList<string> args) {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args) {
            switch (arg.Trim().TrimStart('-').ToLowerInvariant()) {
                case "install": return ServiceCommand.Install;
                case "uninstall": return ServiceCommand.Uninstall;
                case "status": return ServiceCommand.Status;
                case "console":
                case "run": return ServiceCommand.Run;
            }
        }
        return ServiceCommand.Run;
    }
}
