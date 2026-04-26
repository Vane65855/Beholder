# 004: Preload the Windows DNS resolver cache via an undocumented dnsapi.dll export

## Context

Beholder observes DNS name-to-IP mappings passively, by subscribing to the `Microsoft-Windows-DNS-Client` ETW provider (see `EtwDnsCache`). That catches every DNS event that fires after the daemon attaches to the provider, but it says nothing about resolutions that happened *before* the daemon started. On a typical developer workflow the daemon is launched by hand long after Firefox, Defender, Windows Update, and countless other apps have already resolved and connected to hosts. Every flow older than the daemon permanently shows as a raw IP in the UI even though Windows still remembers the hostname in its in-process resolver cache.

The quality bar for this tool treats per-flow hostname capture as mission-critical — "every connection that passed through Windows DNS must resolve to a hostname." The only systems that don't have to reach that bar are direct-IP connections (apps that never queried DNS at all). Reverse DNS is reserved for *that* class, and — critically — the daemon must never issue its own outbound DNS queries to close the hostname gap. (This rule has since been scoped — see ADR 005 for the explicit, gated reverse-DNS exception that covers the direct-IP residual class only.)

That combination (must-have coverage + no outbound DNS) forces us to read Windows' own cache. Windows exposes this cache via `DnsGetCacheDataTable` — an undocumented-but-stable export of `dnsapi.dll`. It has shipped on every Windows version since XP and is used in production by osquery, `muhdnscache`, and `malcomvetter/DnsCache`. There is no documented equivalent; `Get-DnsClientCache` in PowerShell wraps the same call internally.

## Decision

Call `DnsGetCacheDataTable` from a one-shot preload inside `EtwDnsCache.StartAsync`, immediately after the channel consumer is created. For each A / AAAA entry the table enumerates, follow up with a documented `DnsQuery_W` call passing `DNS_QUERY_NO_WIRE_QUERY` — which guarantees the resolution comes from cache + HOSTS only, with zero outbound network traffic. Fold each (name, IP) pair into the in-memory `_cache` via a new `IngestResolved` seam that bypasses the Windows-formatted-string parser used by the live ETW path.

P/Invoke lives in a new `DnsApiInterop.cs` using `LibraryImport` source-generated marshalling (net7+ idiom, AOT-compatible, allocation-free stubs). The project tightens `<SupportedOSPlatform>` from `windows` to `windows10.0.22000` so the Roslyn analyzer and assembly metadata accurately document the runtime requirement.

Every path that could fail — `OperatingSystem.IsWindowsVersionAtLeast` false, export missing, marshalling exception, non-zero status — logs a warning and degrades to an empty enumeration. The daemon always starts; at worst it starts with an empty preload and relies on live ETW events from that point forward.

## Consequences

- **Positive: closes the cold-start gap for every TTL-valid entry in Windows' cache.** The first launch after the daemon is started, small-byte flows from already-open sessions (keepalives to GitHub, Fastly, Google, AWS, etc.) resolve to hostnames immediately rather than showing as raw IPs for the full lifetime of those flows.
- **Positive: zero outbound DNS.** `DNS_QUERY_NO_WIRE_QUERY` is a first-class Windows API flag, verified by packet capture during smoke testing. The daemon honours its "we never talk to DNS servers" contract.
- **Positive: no legacy-branch code.** The Windows-11-only scope lets the interop use a single `DNS_CACHE_ENTRY` layout without version-dependent marshalling.
- **Positive: sets a clean P/Invoke precedent.** The project had no native interop before this; `DnsApiInterop` is the reference implementation for any future Win11-native additions.
- **Negative: depends on an undocumented export.** Microsoft could remove `DnsGetCacheDataTable` in a future Windows update. The degrade path is designed for exactly this: `NativeLibrary.TryGetExport` at runtime + a single warning log and the daemon continues with the live ETW path alone. No crash, no retry storm, no escalation. Users would lose the preload benefit but retain all post-daemon-start captures.
- **Negative: undocumented API => no SLA.** There is no support contract; behaviour is validated by production use in osquery and peers, not by Microsoft docs. If the struct layout drifts in a future Win11 update, we'd see garbage data through our marshalling — the `try/catch` around `Marshal.PtrToStructure` means we log and skip individual bad entries, and the `try/catch` around the outer enumeration means a wholly-broken schema degrades to empty preload.
- **Unresolved gap (out of scope).** Flows older than Windows' own cache TTL (typically 5-15 min) have hostnames that are *not recoverable at our layer* — Windows has already forgotten them. The full fix is Phase 11's Windows Service autostart, which makes the daemon a first-class bootstrap-time participant so nothing resolves a DNS before we do.

## Resolution: verified `DnsGetCacheDataTableEx` prototype, try-Ex-then-legacy in production

The Win11 22H2+ regression was closed empirically. A throwaway probe (`Beholder.Tools.DnsCacheProbe`, committed at `3a05477` and removed at `8ef32e3` per `PRINCIPLES.md §No Dead Weight`; resurrectable via `git checkout 3a05477 -- Beholder.Tools.DnsCacheProbe/`) ran seven candidate signatures in separate processes so a wrong-shape AV in one couldn't take down the others. The result matrix on the user's Win11 22H2+ machine:

| Candidate | Status | Outcome |
|---|---|---|
| legacy `DnsGetCacheDataTable(out IntPtr)` | 1 (`ERROR_INVALID_FUNCTION`) | confirms the regression |
| `DnsGetCacheDataTableEx(uint flags, out IntPtr)` (this is the answer) | 0 | 469 entries walked, sample names `rr5---sn-4g5ednrl.googlevideo.com`, `plus.l.google.com`, `bunnyfonts.b-cdn.net`, `yt3.googleusercontent.com`, `images.skyscnr.com` — all matched the user's active browsing session |
| `DnsGetCacheDataTableEx(out IntPtr, uint flags)` (wrong arg order) | 87 (`ERROR_INVALID_PARAMETER`) | rejected by Win11 parameter validation, no AV |
| `DnsGetCacheDataTableEx(uint flags=0x8000, out IntPtr)` | 0 | identical 469 entries — the FRex `0x8000` "private cache tier" bit makes no observable difference here, so production passes `flags=0` |
| `DnsGetCacheDataTableEx(uint, out uint count, out IntPtr)` | 0 with null table | false positive — extra parameter shifted the out-pointer |
| `DnsGetCacheDataTableEx(out IntPtr, out uint count)` | 0 with null table | same false positive shape |
| `DnsGetCacheDataTable(uint flags, out IntPtr)` (testing if legacy was widened) | 0 with null table | false positive — confirms legacy still has the original arity |

No candidate AV'd. The earlier `f6d7bf3` crash from a wrong-arity Ex declaration was an x64 stack-state coincidence; the same wrong-arity case in this run exited cleanly because the surrounding stack happened to differ.

### Production wiring

`Beholder.Daemon.Windows/DnsApiInterop.cs` declares both exports and tries Ex first via `AcquireCacheTable`. If Ex returns status 0 (Win11 22H2+ happy path), we use its table. If Ex returns non-zero (older Win11 builds where the regression hasn't shipped) we fall through to legacy. If both return non-zero or throw, we log a warning citing this decision record and skip the preload — daemon still starts, live ETW continues to populate the cache from then on.

The verified prototype is:

```c
DWORD WINAPI DnsGetCacheDataTableEx(
    DWORD              dwFlags,        // pass 0
    PDNS_CACHE_ENTRY  *ppCacheTable    // out: head of singly-linked list, free with DnsFree(...DnsFreeFlat)
);
```

`DNSCACHEENTRY` layout is unchanged from the legacy export: `pNext`, `pszName`, `wType`, `wDataLength`, `dwFlags`. Both exports populate the same kernel-side state, so `EnumerateTable` doesn't need to know which path served.

### Kill-switch

`DnsOptions.EnablePreload` (default `true`) stays in place. Setting it to `false` in `appsettings.json` under `"Dns"` (or env var `Dns__EnablePreload=false`) makes `EtwDnsCache.PreloadFromWindowsDnsCache` early-return without entering the interop at all. Defence against any future Windows update that breaks both Ex and legacy.

### What's next if both ever fail

`MSFT_DNSClientCache` CIM class in `ROOT\StandardCimv2` is the documented, crash-safe escape hatch — heavier infra (WMI session + CIM marshalling), but it works on every Windows version that supports `Get-DnsClientCache` in PowerShell. Plan B if a future Windows update breaks both undocumented `dnsapi.dll` exports.
