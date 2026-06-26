using Microsoft.Extensions.Hosting.WindowsServices;

namespace Beholder.Daemon;

/// <summary>
/// Resolves the daemon's on-disk locations (Phase 12.1). Mutable data — the
/// SQLite database and the Ed25519 checkpoint keys — lives under
/// <c>%ProgramData%\Beholder</c> when the process is hosted by the Windows
/// Service Control Manager, and beside the executable otherwise. A developer
/// <c>dotnet run</c> therefore keeps its data local and needs no elevation,
/// while an installed service writes to a stable location the installer ACLs to
/// SYSTEM + Administrators (the data-dir decision in ADR 012 / ADR 013).
/// Read-only bundled assets (the GeoIP MMDB and the OUI registry) always sit
/// beside the executable, since they ship with the binary and are never written.
/// </summary>
public static class DaemonPaths {
    private const string AppFolderName = "Beholder";

    // Evaluated once: a process cannot change its SCM-hosted status at runtime.
    private static readonly bool HostedAsWindowsService = WindowsServiceHelpers.IsWindowsService();

    /// <summary>
    /// Directory that directly holds <c>beholder.db</c> and the <c>keys</c>
    /// folder: <see cref="ServiceDataRoot"/> under the SCM, otherwise
    /// <c>&lt;exe&gt;\data</c>.
    /// </summary>
    public static string WritableDataRoot => ResolveWritableDataRoot(HostedAsWindowsService);

    /// <summary>
    /// Directory holding the read-only bundled assets
    /// (<c>dbip-country-lite.mmdb</c>, <c>oui.csv</c>) — always
    /// <c>&lt;exe&gt;\data</c>, where the build copies them beside the binary.
    /// </summary>
    public static string ReadOnlyAssetRoot { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// The mutable-data directory an installed Windows service uses
    /// (<c>%ProgramData%\Beholder</c>), regardless of the current host. The
    /// installer needs this while running as a console process — where
    /// <see cref="WindowsServiceHelpers.IsWindowsService"/> is false — to create
    /// and ACL the directory the future service will write to.
    /// </summary>
    public static string ServiceDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName);

    internal static string ResolveWritableDataRoot(bool hostedAsWindowsService) =>
        hostedAsWindowsService ? ServiceDataRoot : Path.Combine(AppContext.BaseDirectory, "data");
}
