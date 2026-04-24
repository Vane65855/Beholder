using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows;

/// <summary>
/// P/Invoke surface for the minimum slice of <c>dnsapi.dll</c> we need to
/// preload our hostname cache from the Windows DNS resolver at daemon startup.
/// Read-only enumeration: no query ever leaves the machine because every
/// <see cref="DnsQueryW"/> call is issued with <see cref="DNS_QUERY_NO_WIRE_QUERY"/>
/// which restricts resolution to the local cache + HOSTS file.
/// </summary>
/// <remarks>
/// <para>
/// <c>DnsGetCacheDataTable</c> is an undocumented export that has shipped in
/// <c>dnsapi.dll</c> since Windows XP and is used in production by
/// <a href="https://github.com/osquery/osquery/pull/6505">osquery</a>,
/// <a href="https://github.com/FRex/muhdnscache">muhdnscache</a>, and
/// <a href="https://github.com/malcomvetter/DnsCache">malcomvetter/DnsCache</a>.
/// The decision record at <c>docs/decisions/004-dns-cache-preload-undocumented-api.md</c>
/// justifies its use and lays out the graceful-degrade contract: if the
/// export is ever removed or the marshalling surprises us, the daemon logs a
/// warning and proceeds with an empty preload — it does not crash.
/// </para>
/// <para>
/// Windows 11 only. The <c>DNS_CACHE_ENTRY</c> and <c>DNS_RECORD</c> layouts
/// have drifted across Windows versions; narrowing to Win11 lets us use a
/// single struct layout without legacy-branch churn.
/// </para>
/// </remarks>
internal static partial class DnsApiInterop {
    private const ushort DNS_TYPE_A = 0x0001;
    private const ushort DNS_TYPE_AAAA = 0x001C;

    /// <summary>
    /// The critical flag: tells <see cref="DnsQueryW"/> to satisfy the query
    /// from cache + HOSTS only, never issuing a network DNS request. This is
    /// the guarantee that our preload generates zero outbound traffic.
    /// </summary>
    private const uint DNS_QUERY_NO_WIRE_QUERY = 0x10;

    private const uint ERROR_SUCCESS = 0;

    private enum DnsFreeType : uint {
        DnsFreeFlat = 0,
        DnsFreeRecordList = 1,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DnsCacheEntry {
        public IntPtr Next;
        public IntPtr Name;  // LPWSTR
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
    }

    /// <summary>
    /// Fixed-size prefix of the <c>DNS_RECORD</c> union Windows emits. We
    /// only read the header + the address payload; we don't parse the
    /// tail-union for non-A/AAAA types.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DnsRecordHeader {
        public IntPtr Next;
        public IntPtr Name;  // LPWSTR
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
        // Data union follows immediately after; layout is type-dependent.
    }

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable")]
    private static partial uint DnsGetCacheDataTable(out IntPtr ppCacheTable);

    // Newer Win11 variant of the same function. On some Win11 builds the old
    // <c>DnsGetCacheDataTable</c> export is present but returns
    // <c>ERROR_INVALID_FUNCTION</c> (status 1), forcing callers onto the Ex
    // variant. Signature is the same as the old one on current builds — a
    // single PDNS_CACHE_ENTRY out-pointer — so we can try one then the other
    // without a different struct layout.
    [LibraryImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTableEx")]
    private static partial uint DnsGetCacheDataTableEx(out IntPtr ppCacheTable);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint DnsQueryW(
        string pszName,
        ushort wType,
        uint options,
        IntPtr pExtra,
        out IntPtr ppQueryResults,
        IntPtr pReserved);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFree")]
    private static partial void DnsFree(IntPtr pData, DnsFreeType freeType);

    /// <summary>
    /// Enumerates the Windows DNS resolver cache's A / AAAA entries and
    /// returns (queryName, ipAddress) tuples for each cached answer. No
    /// outbound DNS traffic is generated: every query uses
    /// <see cref="DNS_QUERY_NO_WIRE_QUERY"/>.
    /// </summary>
    /// <remarks>
    /// Returns an empty sequence (not a throw) for any of:
    /// <list type="bullet">
    /// <item>Running on pre-Windows-11 — this code targets Win11's
    /// <c>DNS_CACHE_ENTRY</c> layout and skips cleanly on older builds.</item>
    /// <item><c>DnsGetCacheDataTable</c> export missing from
    /// <c>dnsapi.dll</c> on this Windows build.</item>
    /// <item><c>DnsGetCacheDataTable</c> returning a non-zero status.</item>
    /// <item>Any unexpected exception during marshalling or enumeration.</item>
    /// </list>
    /// All skip paths log a warning — the daemon continues to start and the
    /// live ETW path still populates the cache going forward. The caller
    /// consumes the enumerable synchronously; the cached-table pointer is
    /// released once iteration completes.
    /// </remarks>
    public static IEnumerable<(string QueryName, IPAddress Address)> TryEnumerateResolverCache(ILogger logger) {
        ArgumentNullException.ThrowIfNull(logger);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) {
            logger.LogWarning(
                "DNS cache preload skipped: requires Windows 11 or later (build 22000+)");
            yield break;
        }

        if (!NativeLibrary.TryLoad("dnsapi.dll", out var handle)) {
            logger.LogWarning(
                "DNS cache preload skipped: could not load dnsapi.dll");
            yield break;
        }
        bool exHasExport;
        bool legacyHasExport;
        try {
            exHasExport = NativeLibrary.TryGetExport(handle, "DnsGetCacheDataTableEx", out _);
            legacyHasExport = NativeLibrary.TryGetExport(handle, "DnsGetCacheDataTable", out _);
        } finally {
            NativeLibrary.Free(handle);
        }

        if (!exHasExport && !legacyHasExport) {
            logger.LogWarning(
                "DNS cache preload skipped: neither DnsGetCacheDataTableEx nor DnsGetCacheDataTable export found on this Windows build");
            yield break;
        }

        // Prefer Ex on Win11 — the legacy export survives but returns
        // ERROR_INVALID_FUNCTION (1) on recent builds. If Ex is absent or
        // itself fails, fall back to the legacy export. Either outcome is
        // acceptable; both failing means we log and continue with empty
        // preload.
        var result = AcquireCacheTable(exHasExport, legacyHasExport, logger);
        if (result.CacheTable == IntPtr.Zero) {
            if (result.SuccessfulEntryPoint is null) {
                logger.LogWarning(
                    "DNS cache preload skipped: all resolver-cache entry points returned non-zero (last status {LastStatus}, attempts {Attempts})",
                    result.LastStatus, result.AttemptsTried);
                yield break;
            }
            // Call succeeded but returned an empty table — legitimate empty cache.
            logger.LogDebug("DNS cache preload: Windows resolver cache is empty (via {EntryPoint})",
                result.SuccessfulEntryPoint);
            yield break;
        }

        var cacheTable = result.CacheTable;

        try {
            foreach (var tuple in EnumerateTable(cacheTable, logger))
                yield return tuple;
        } finally {
            try {
                DnsFree(cacheTable, DnsFreeType.DnsFreeFlat);
            } catch (Exception ex) {
                logger.LogWarning(ex, "DnsFree on cache table failed during preload teardown");
            }
        }
    }

    /// <summary>
    /// Result of a best-effort call to DnsGetCacheDataTableEx / legacy. If
    /// <c>CacheTable</c> is non-zero the caller must DnsFree it after use.
    /// <c>SuccessfulEntryPoint</c> is the string name of whichever variant
    /// returned success (even an empty cache counts as success); null means
    /// every variant returned an error.
    /// </summary>
    private readonly record struct CacheTableResult(
        IntPtr CacheTable,
        uint LastStatus,
        string? SuccessfulEntryPoint,
        string AttemptsTried);

    /// <summary>
    /// Calls DnsGetCacheDataTableEx first (preferred on Win11 — the legacy
    /// export can return <c>ERROR_INVALID_FUNCTION</c> on recent builds).
    /// Falls back to the legacy export only if Ex is missing or failed. The
    /// struct layout is identical between the two variants, so the caller
    /// enumerates whichever returned a non-null table identically.
    /// </summary>
    private static CacheTableResult AcquireCacheTable(
        bool hasEx, bool hasLegacy, ILogger logger
    ) {
        var attempts = string.Empty;
        uint lastStatus = 0;

        if (hasEx) {
            attempts = "Ex";
            try {
                var status = DnsGetCacheDataTableEx(out var table);
                if (status == ERROR_SUCCESS) {
                    // Non-zero table is "has entries"; zero table + success is
                    // "empty cache". Both cases are successful calls.
                    return new CacheTableResult(table, status, "Ex", attempts);
                }
                lastStatus = status;
            } catch (Exception ex) {
                logger.LogWarning(ex, "DnsGetCacheDataTableEx threw");
            }
        }

        if (hasLegacy) {
            attempts = attempts.Length == 0 ? "legacy" : attempts + "+legacy";
            try {
                var status = DnsGetCacheDataTable(out var table);
                if (status == ERROR_SUCCESS) {
                    return new CacheTableResult(table, status, "legacy", attempts);
                }
                lastStatus = status;
            } catch (Exception ex) {
                logger.LogWarning(ex, "DnsGetCacheDataTable threw");
            }
        }

        return new CacheTableResult(IntPtr.Zero, lastStatus, null, attempts);
    }

    private static IEnumerable<(string QueryName, IPAddress Address)> EnumerateTable(
        IntPtr cacheTable, ILogger logger
    ) {
        var current = cacheTable;
        while (current != IntPtr.Zero) {
            DnsCacheEntry entry;
            string? name;
            try {
                entry = Marshal.PtrToStructure<DnsCacheEntry>(current);
                name = entry.Name != IntPtr.Zero
                    ? Marshal.PtrToStringUni(entry.Name)
                    : null;
            } catch (Exception ex) {
                logger.LogWarning(ex, "DNS cache preload: failed to marshal cache entry; stopping enumeration");
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(name)
                && (entry.Type == DNS_TYPE_A || entry.Type == DNS_TYPE_AAAA)) {
                foreach (var address in QueryCachedRecord(name, entry.Type, logger))
                    yield return (name, address);
            }

            current = entry.Next;
        }
    }

    private static IEnumerable<IPAddress> QueryCachedRecord(string name, ushort type, ILogger logger) {
        IntPtr records;
        uint status;
        try {
            status = DnsQueryW(
                name, type, DNS_QUERY_NO_WIRE_QUERY,
                IntPtr.Zero, out records, IntPtr.Zero);
        } catch (Exception ex) {
            logger.LogWarning(
                ex, "DNS cache preload: DnsQuery_W threw for {Name} type {Type}", name, type);
            yield break;
        }

        // Non-zero status is normal for cache-only queries when an entry has
        // expired between the table enumeration and our follow-up query —
        // log at Debug rather than warning to avoid noise.
        if (status != ERROR_SUCCESS || records == IntPtr.Zero) {
            yield break;
        }

        try {
            foreach (var addr in WalkRecordList(records, type))
                yield return addr;
        } finally {
            try {
                DnsFree(records, DnsFreeType.DnsFreeRecordList);
            } catch (Exception ex) {
                logger.LogWarning(
                    ex, "DnsFree on record list failed for {Name}", name);
            }
        }
    }

    private static IEnumerable<IPAddress> WalkRecordList(IntPtr records, ushort filterType) {
        // The DNS_RECORD union has a variable tail; the header is a fixed
        // prefix and the address data sits at sizeof(header). For A (4 bytes)
        // and AAAA (16 bytes) we read the payload directly from the computed
        // offset. Other record types are ignored.
        var headerSize = Marshal.SizeOf<DnsRecordHeader>();
        var current = records;
        while (current != IntPtr.Zero) {
            var header = Marshal.PtrToStructure<DnsRecordHeader>(current);
            var next = header.Next;

            if (header.Type == filterType) {
                // DNS_A_DATA's IpAddress and DNS_AAAA_DATA's Ip6Address are
                // laid out in network byte order (MSB first in memory) per
                // Windows socket convention. new IPAddress(byte[]) expects
                // network byte order. Reading raw bytes in memory order and
                // constructing directly works correctly regardless of host
                // endianness — no byte-swap needed.
                IPAddress? address = null;
                if (header.Type == DNS_TYPE_A)
                    address = ReadIpFromRecord(current + headerSize, byteCount: 4);
                else if (header.Type == DNS_TYPE_AAAA)
                    address = ReadIpFromRecord(current + headerSize, byteCount: 16);

                if (address is not null)
                    yield return address;
            }

            current = next;
        }
    }

    /// <summary>
    /// Reads <paramref name="byteCount"/> raw bytes from the native pointer
    /// and wraps them in an <see cref="IPAddress"/>. Returns <c>null</c> on
    /// any marshalling failure so a single bad record can't stop enumeration
    /// of the rest of the list.
    /// </summary>
    private static IPAddress? ReadIpFromRecord(IntPtr source, int byteCount) {
        try {
            var bytes = new byte[byteCount];
            Marshal.Copy(source, bytes, 0, byteCount);
            return new IPAddress(bytes);
        } catch (Exception) {
            return null;
        }
    }
}
