using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows.Scanner;

/// <summary>
/// P/Invoke surface for the slice of <c>iphlpapi.dll</c> the LAN scanner needs.
/// Two distinct surfaces:
///
/// <list type="bullet">
/// <item><b>Active probe:</b> <see cref="TrySendArp"/> wraps the documented
/// <c>SendARP</c> Win32 export (shipped since Windows NT4) for per-IP ARP
/// requests. Slow on unresponsive IPs (Windows holds ~1 s per call), so the
/// scheduler caller (<see cref="ArpScanProbe.ProbeIpsAsync"/>) drives many
/// in parallel via <see cref="System.Threading.Tasks.Parallel.ForEachAsync"/>.</item>
/// <item><b>Passive cache walk:</b> <see cref="TryEnumerateIpv4ArpCache"/>
/// wraps <c>GetIpNetTable2</c> + <c>FreeMibTable</c> to read Windows'
/// existing IPv4 ARP / neighbor cache. Instant; zero packets sent. Catches
/// most devices on a typical LAN where everything talks to the gateway
/// periodically. Mirrors <see cref="DnsApiInterop.TryEnumerateResolverCache"/>'s
/// pattern from ADR 004 line-for-line.</item>
/// </list>
///
/// All entry points use source-generated <see cref="LibraryImportAttribute"/>
/// marshalling. Defensive: every expected failure mode collapses to a null /
/// empty return so callers can treat OS-API absence the same as "no data."
/// </summary>
internal static partial class IphlpapiInterop {
    private const uint NoError = 0;
    private const uint ExpectedMacLength = 6;

    // SOCKADDR_INET family codes (winsock2.h).
    private const ushort AfInet = 2;

    // NL_NEIGHBOR_STATE (nldef.h) — the OS-tracked reachability of an ARP entry.
    // We accept only states that mean "this MAC is currently associated with this
    // IP." Skip Unreachable (0), Incomplete (1), Delay (3), Probe (5) — those are
    // in-flight or failed and would give us stale or wrong MACs.
    private const uint NlnsReachable = 2;
    private const uint NlnsStale = 4;
    private const uint NlnsPermanent = 6;

    // IF_MAX_PHYS_ADDRESS_SIZE (ifdef.h) — the inline buffer size for hardware
    // addresses. Ethernet MACs occupy the first 6 bytes; other media (InfiniBand
    // etc.) can use more.
    private const int IfMaxPhysAddressSize = 32;

    // MIB_IPNET_TABLE2 header layout: ULONG NumEntries + 4 bytes alignment
    // padding before the 8-byte-aligned MIB_IPNET_ROW2 array. The padding is
    // implicit on x64 because the row struct contains a ulong (NET_LUID) which
    // forces 8-byte alignment.
    private const int RowsBaseOffset = 8;

    [LibraryImport("iphlpapi.dll", EntryPoint = "SendARP")]
    private static partial uint SendArp(
        uint destIp,
        uint srcIp,
        [Out] byte[] macAddr,
        ref uint physAddrLen);

    [LibraryImport("iphlpapi.dll", EntryPoint = "GetIpNetTable2")]
    private static partial uint GetIpNetTable2(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll", EntryPoint = "FreeMibTable")]
    private static partial void FreeMibTable(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow2 {
        // SOCKADDR_INET union (28 bytes). Parsed manually in TryReadIpv4 —
        // first 2 bytes are family; if AF_INET, bytes 4..8 are the IPv4 address.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 28)]
        public byte[] AddressBytes;

        public uint InterfaceIndex;     // NET_IFINDEX
        public ulong InterfaceLuid;     // NET_LUID union (8 bytes)

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IfMaxPhysAddressSize)]
        public byte[] PhysicalAddress;

        public uint PhysicalAddressLength;
        public uint State;              // NL_NEIGHBOR_STATE enum
        public uint Flags;              // IsRouter / IsUnreachable bitfield (1 byte) + padding
        public uint ReachabilityTime;   // ULONG union (LastReachable | LastUnreachable)
    }

    /// <summary>
    /// Issues a single ARP request for <paramref name="dest"/> and returns the
    /// responder's MAC formatted as lowercase hex with colons (e.g.
    /// <c>"aa:bb:cc:dd:ee:ff"</c>), or <see langword="null"/> if the IP did
    /// not respond, the underlying export is missing, or the response was
    /// malformed. <paramref name="timeoutMs"/> is documented but Windows
    /// internally caps SendARP at ~1 s regardless; the parameter is kept on
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

    /// <summary>
    /// Enumerates the Windows IPv4 ARP / neighbor cache, returning
    /// <c>(IP, MAC)</c> tuples for entries in reachability states the caller
    /// can trust (<c>Reachable</c> / <c>Stale</c> / <c>Permanent</c>).
    /// Returns an empty sequence on every expected failure (export missing,
    /// non-zero status, empty cache, marshalling exception). The unmanaged
    /// table is freed via <c>FreeMibTable</c> when iteration completes.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="DnsApiInterop.TryEnumerateResolverCache"/>'s shape
    /// exactly per ADR 004's P/Invoke precedent. Pre-Win10 systems are not
    /// supported by the broader daemon, so the version gate is consistent
    /// with <see cref="TrySendArp"/>.
    /// </remarks>
    public static IEnumerable<(IPAddress Ip, string Mac)> TryEnumerateIpv4ArpCache(ILogger logger) {
        ArgumentNullException.ThrowIfNull(logger);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) yield break;

        if (!NativeLibrary.TryLoad("iphlpapi.dll", out var handle)) {
            logger.LogWarning("ARP cache walk skipped: could not load iphlpapi.dll");
            yield break;
        }
        bool hasGetTable;
        bool hasFree;
        try {
            hasGetTable = NativeLibrary.TryGetExport(handle, "GetIpNetTable2", out _);
            hasFree = NativeLibrary.TryGetExport(handle, "FreeMibTable", out _);
        } finally {
            NativeLibrary.Free(handle);
        }
        if (!hasGetTable || !hasFree) {
            logger.LogWarning(
                "ARP cache walk skipped: GetIpNetTable2 or FreeMibTable export missing");
            yield break;
        }

        IntPtr table;
        uint status;
        try {
            status = GetIpNetTable2(AfInet, out table);
        } catch (Exception ex) {
            logger.LogWarning(ex, "GetIpNetTable2 threw; ARP cache walk skipped");
            yield break;
        }
        if (status != NoError || table == IntPtr.Zero) {
            logger.LogDebug(
                "GetIpNetTable2 returned status {Status}; ARP cache empty or unavailable", status);
            yield break;
        }

        try {
            foreach (var tuple in EnumerateArpRows(table, logger))
                yield return tuple;
        } finally {
            try {
                FreeMibTable(table);
            } catch (Exception ex) {
                logger.LogWarning(ex, "FreeMibTable on ARP table failed during teardown");
            }
        }
    }

    private static IEnumerable<(IPAddress Ip, string Mac)> EnumerateArpRows(
        IntPtr table, ILogger logger
    ) {
        uint numEntries;
        try {
            numEntries = (uint)Marshal.ReadInt32(table);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Failed to read ARP table header");
            yield break;
        }

        var rowSize = Marshal.SizeOf<MibIpNetRow2>();
        var rowsBase = IntPtr.Add(table, RowsBaseOffset);

        for (uint i = 0; i < numEntries; i++) {
            MibIpNetRow2 row;
            try {
                row = Marshal.PtrToStructure<MibIpNetRow2>(
                    IntPtr.Add(rowsBase, (int)(i * rowSize)));
            } catch (Exception ex) {
                logger.LogDebug(ex, "Skipping ARP row {Index} due to marshalling exception", i);
                continue;
            }

            if (row.State is not NlnsReachable and not NlnsStale and not NlnsPermanent) continue;
            if (row.PhysicalAddressLength != ExpectedMacLength) continue;  // Ethernet MACs only

            // Skip non-unicast MACs (broadcast ff:ff:ff:ff:ff:ff, IPv4 multicast
            // 01:00:5e:*, IPv6 multicast 33:33:*, locally-administered group
            // addresses). The I/G bit — least significant bit of the first
            // octet — is set on every group address; clearing means unicast.
            // Also skip all-zeros (uninitialized / invalid). Windows' ARP cache
            // contains an entry for the directed-broadcast IP that would
            // otherwise surface as a phantom "device" (~1 spurious row per
            // active subnet on the LAN scanner output).
            if ((row.PhysicalAddress[0] & 0x01) != 0) continue;
            if (row.PhysicalAddress[0] == 0 && row.PhysicalAddress[1] == 0 &&
                row.PhysicalAddress[2] == 0 && row.PhysicalAddress[3] == 0 &&
                row.PhysicalAddress[4] == 0 && row.PhysicalAddress[5] == 0) continue;

            var ip = TryReadIpv4(row.AddressBytes);
            if (ip is null) continue;

            var macBytes = new byte[ExpectedMacLength];
            Array.Copy(row.PhysicalAddress, macBytes, ExpectedMacLength);
            yield return (ip, FormatMac(macBytes));
        }
    }

    /// <summary>
    /// Parses the leading bytes of a SOCKADDR_INET buffer as a SOCKADDR_IN
    /// when the family is <see cref="AfInet"/>; returns null otherwise.
    /// Layout: family (ushort, bytes 0-1), port (ushort, bytes 2-3),
    /// sin_addr (4 bytes, bytes 4-7), padding to 28 bytes.
    /// </summary>
    private static IPAddress? TryReadIpv4(byte[] sockaddrInetBytes) {
        if (sockaddrInetBytes is null || sockaddrInetBytes.Length < 8) return null;
        var family = (ushort)(sockaddrInetBytes[0] | (sockaddrInetBytes[1] << 8));
        if (family != AfInet) return null;
        var ipBytes = new byte[4];
        Array.Copy(sockaddrInetBytes, 4, ipBytes, 0, 4);
        return new IPAddress(ipBytes);
    }

    private static string FormatMac(byte[] bytes) =>
        string.Join(':', bytes.Select(b => b.ToString("x2")));
}
