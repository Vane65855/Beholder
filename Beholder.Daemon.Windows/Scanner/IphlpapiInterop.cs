using System.Net;
using System.Runtime.InteropServices;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// P/Invoke surface for the minimum slice of <c>iphlpapi.dll</c> the Phase 9.2
/// ARP probe needs. Wraps the documented <c>SendARP</c> Win32 export
/// (shipped since Windows NT4) using source-generated
/// <see cref="LibraryImportAttribute"/> marshalling — same pattern as
/// <see cref="DnsApiInterop"/> per ADR 004's P/Invoke precedent.
/// </summary>
/// <remarks>
/// Unlike <see cref="DnsApiInterop"/>'s undocumented exports, <c>SendARP</c> is
/// fully documented and stable, so no <c>NativeLibrary.TryGetExport</c> probe
/// is needed. Defensive: every expected failure mode collapses to a null
/// return so the calling probe can treat "no response from this IP" the same
/// as "the call succeeded with no MAC."
/// </remarks>
internal static partial class IphlpapiInterop {
    private const uint NoError = 0;
    private const uint ExpectedMacLength = 6;

    [LibraryImport("iphlpapi.dll", EntryPoint = "SendARP")]
    private static partial uint SendArp(
        uint destIp,
        uint srcIp,
        [Out] byte[] macAddr,
        ref uint physAddrLen);

    /// <summary>
    /// Issues a single ARP request for <paramref name="dest"/> and returns the
    /// responder's MAC formatted as lowercase hex with colons (e.g.
    /// <c>"aa:bb:cc:dd:ee:ff"</c>), or <see langword="null"/> if the IP did
    /// not respond, the underlying export is missing, or the response was
    /// malformed. <paramref name="timeoutMs"/> is documented but Windows
    /// internally caps SendARP at ~3 s regardless; the parameter is kept on
    /// the surface for future symmetry with mDNS / NetBIOS probes whose
    /// timeouts ARE honored.
    /// </summary>
    public static string? TrySendArp(IPAddress dest, int timeoutMs) {
        ArgumentNullException.ThrowIfNull(dest);
        _ = timeoutMs; // reserved for future symmetry; SendARP ignores it

        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return null;
        if (dest.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

        try {
            var destBytes = dest.GetAddressBytes();
            var destInt = BitConverter.ToUInt32(destBytes, 0);
            var mac = new byte[ExpectedMacLength];
            var len = ExpectedMacLength;
            var status = SendArp(destInt, 0, mac, ref len);
            if (status != NoError || len != ExpectedMacLength) return null;
            return FormatMac(mac);
        } catch (DllNotFoundException) {
            return null;
        } catch (EntryPointNotFoundException) {
            return null;
        }
    }

    private static string FormatMac(byte[] bytes) =>
        string.Join(':', bytes.Select(b => b.ToString("x2")));
}
