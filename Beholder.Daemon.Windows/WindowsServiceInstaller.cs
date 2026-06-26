using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Registers, removes, and queries the Beholder daemon as a Windows service
/// using the in-box <c>sc.exe</c> and <c>icacls.exe</c> tools (Phase 12.1).
/// Invoked from <c>Program.cs</c> for the <c>--install</c> / <c>--uninstall</c> /
/// <c>--status</c> commands, before the host is built. The service runs as
/// LocalSystem, which already holds the privileges ETW capture and WFP firewall
/// control require, so no application manifest is needed. See ADR 013.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceInstaller {
    internal const string ServiceName = "Beholder";
    private const string DisplayName = "Beholder NMT";
    private const string Description =
        "Beholder Network Monitoring Tool — captures per-process network telemetry, " +
        "enforces firewall rules, and maintains the tamper-evident audit chain.";

    /// <summary>Registers the auto-start service and hardens its data directory. Returns a process exit code.</summary>
    public static int Install() {
        if (!IsElevated()) return FailNotElevated("install");

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) {
            Console.Error.WriteLine("Cannot determine the daemon executable path; install aborted.");
            return 1;
        }

        var dataRoot = ServiceDataRoot();
        HardenDataDirectory(dataRoot);

        var create = RunTool("sc.exe", BuildCreateArguments(exePath));
        if (create.ExitCode != 0) {
            Console.Error.WriteLine($"sc create failed (exit {create.ExitCode}): {create.Output.Trim()}");
            return create.ExitCode;
        }
        // Description + failure-recovery are best-effort polish; a hiccup here
        // must not undo the successful create above.
        RunTool("sc.exe", BuildDescriptionArguments());
        RunTool("sc.exe", BuildFailureArguments());

        var start = RunTool("sc.exe", BuildStartArguments());
        Console.WriteLine(start.ExitCode == 0
            ? $"Installed and started service '{ServiceName}'. Data directory: {dataRoot}"
            : $"Installed service '{ServiceName}' (not yet started: {start.Output.Trim()}). Data directory: {dataRoot}");
        return 0;
    }

    /// <summary>Stops (best-effort) and deletes the service, leaving its data directory intact. Returns an exit code.</summary>
    public static int Uninstall() {
        if (!IsElevated()) return FailNotElevated("uninstall");

        RunTool("sc.exe", BuildStopArguments());   // best-effort: may already be stopped/absent
        var delete = RunTool("sc.exe", BuildDeleteArguments());
        if (delete.ExitCode != 0) {
            Console.Error.WriteLine($"sc delete failed (exit {delete.ExitCode}): {delete.Output.Trim()}");
            return delete.ExitCode;
        }
        Console.WriteLine($"Removed service '{ServiceName}'. Data directory left intact: {ServiceDataRoot()}");
        return 0;
    }

    /// <summary>Prints the service's current state via <c>sc query</c>. Returns sc's exit code.</summary>
    public static int Status() {
        var query = RunTool("sc.exe", BuildQueryArguments());
        Console.WriteLine(query.Output.Trim());
        return query.ExitCode;
    }

    // ---- pure argument builders (unit-tested without executing) ----

    internal static IReadOnlyList<string> BuildCreateArguments(string exePath) => [
        "create", ServiceName,
        "binPath=", exePath,
        "start=", "auto",
        "DisplayName=", DisplayName,
    ];

    internal static IReadOnlyList<string> BuildDescriptionArguments() =>
        ["description", ServiceName, Description];

    internal static IReadOnlyList<string> BuildFailureArguments() => [
        "failure", ServiceName,
        "reset=", "86400",
        "actions=", "restart/5000/restart/5000/restart/60000",
    ];

    internal static IReadOnlyList<string> BuildStartArguments() => ["start", ServiceName];
    internal static IReadOnlyList<string> BuildStopArguments() => ["stop", ServiceName];
    internal static IReadOnlyList<string> BuildDeleteArguments() => ["delete", ServiceName];
    internal static IReadOnlyList<string> BuildQueryArguments() => ["query", ServiceName];

    // Strip inherited ACEs (ProgramData grants Users read by default) and grant
    // only LocalSystem + Administrators, so the daemon's Ed25519 private signing
    // key is not readable by other local users. Well-known SIDs keep this
    // locale-independent. Realizes ADR 012's "SYSTEM + Administrators" claim.
    internal static IReadOnlyList<string> BuildHardenAclArguments(string dataRoot) => [
        dataRoot,
        "/inheritance:r",
        "/grant:r", "*S-1-5-18:(OI)(CI)F",      // LocalSystem
        "/grant:r", "*S-1-5-32-544:(OI)(CI)F",  // Administrators
    ];

    // ---- helpers ----

    // Mirrors Beholder.Daemon.DaemonPaths.ServiceDataRoot — kept in sync by both
    // resolving %ProgramData%\Beholder. The projects can't share a constant
    // (Beholder.Daemon.Windows must not reference Beholder.Daemon — circular).
    internal static string ServiceDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Beholder");

    private static void HardenDataDirectory(string dataRoot) {
        Directory.CreateDirectory(dataRoot);
        var acl = RunTool("icacls.exe", BuildHardenAclArguments(dataRoot));
        if (acl.ExitCode != 0) {
            Console.Error.WriteLine(
                $"Warning: could not harden ACLs on {dataRoot} (exit {acl.ExitCode}): {acl.Output.Trim()}");
        }
    }

    private static bool IsElevated() {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int FailNotElevated(string action) {
        Console.Error.WriteLine(
            $"Administrator privileges are required to {action} the Beholder service. " +
            "Re-run this command from an elevated prompt.");
        return 5;   // ERROR_ACCESS_DENIED
    }

    // Resolve sc.exe / icacls.exe from System32 explicitly — never via PATH —
    // since these run with the caller's elevated token.
    private static (int ExitCode, string Output) RunTool(string tool, IReadOnlyList<string> arguments) {
        var startInfo = new ProcessStartInfo {
            FileName = Path.Combine(Environment.SystemDirectory, tool),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null) return (-1, $"Failed to start {tool}.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
