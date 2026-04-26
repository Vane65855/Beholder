# 006: SNI capture from `Microsoft-Windows-PktMon` ETW for direct-IP destinations

## Context

By the time of ADR 005 (reverse-DNS fallback) the hostname-resolution ladder had three layers:

1. Windows DNS resolver cache preload at startup (`DnsApiInterop.TryEnumerateResolverCache` via `DnsGetCacheDataTableEx`, ADR 004)
2. Live `Microsoft-Windows-DNS-Client` ETW capture (`EtwDnsCache.Ingest`)
3. Reverse-DNS PTR fallback for direct-IP destinations (`ReverseDnsFallbackCache`, ADR 005)

Real-world testing surfaced a residual class no layer above could reach: long-lived TCP connections (HTTP/2 keep-alive, WebSockets) where the original DNS lookup has aged out of every cache we observe — Windows DNS, our own preload, even the browser's own internal cache (Firefox `network.dnsCacheExpiration = 60s`) — but the underlying socket is still being reused for new requests via HTTP/2 multiplexing. The user demonstrated this with `about:networking#sockets` showing `146.75.1.91` (Fastly anycast) with 30+ KB on an active TCP socket, no current DNS entry anywhere, no PTR record (Fastly publishes none), raw IP forever in the COLS view.

`docs/ARCHITECTURE.md` named the right tool for that residual:

> "True TLS-level hostname visibility (SNI extraction from the ClientHello) is a future phase and would close both the DoH and the in-process-cache bypasses — it requires a packet-capture layer that Beholder doesn't have today."

SNI (Server Name Indication, RFC 6066) is the hostname the client sends in plaintext as part of the TLS `ClientHello` extension list — every modern HTTPS connection carries it, and it survives DoH, in-process DNS caches, and connection reuse because it's emitted *on every fresh TLS handshake*, not just on DNS lookups.

## Decision

Add SNI capture as a fourth hostname source, sitting between live ETW DNS observation and reverse-DNS fallback in the priority ladder. Build it on Windows' built-in `Microsoft-Windows-PktMon` ETW provider — no third-party kernel drivers, no custom WFP-callout driver, no install footprint beyond what the daemon already requires (admin elevation).

### Capture mechanism

`Microsoft-Windows-PktMon` provider GUID `{4D4F80D9-C8BD-4D73-BB5B-19C90402C5AC}`. Pktmon ships with Windows 10+ and emits per-packet ETW events when a capture session is active (`pktmon start --capture`). The daemon shells out to `pktmon stop` (best effort) then `pktmon start --capture --pkt-size 0 --flags 0x010` at startup, subscribes to the provider via `TraceEventSession`, and runs `pktmon stop` on shutdown.

Three other capture mechanisms were considered and rejected:

- **WinDivert / Npcap** — userspace bridges to WFP. Mature, full-fidelity, but ship a third-party kernel driver (`WinDivert64.sys`) auto-installed at runtime. The user's "no drivers" decision rules them out.
- **Custom WFP-callout kernel driver** — gold standard for production NMTs but requires writing, signing, and shipping our own `.sys` file. Multi-month effort, out of scope.
- **Pktmon CLI `--log-mode real-time`** — outputs text-only packet summaries with no payload bytes. Empirically verified during Phase 0 of this work. Eliminated.

### Empirical viability gate (Phase 0)

A throwaway probe (`Beholder.Tools.PktmonProbe`, committed at `e7e5a3f4` and removed at the next probe-revert commit per `PRINCIPLES.md §No Dead Weight`; resurrectable via `git checkout <hash> -- Beholder.Tools.PktmonProbe/`) gated the rest of the work on empirical evidence. With `Invoke-WebRequest` driving 10 fresh HTTPS handshakes during a 30s capture, the probe extracted SNI from 44/44 ClientHellos (100% — multiple appearances per handshake at different NDIS components). All 10 hostnames recovered correctly: `example.com`, `www.iana.org`, `www.rfc-editor.org`, `nginx.org`, `curl.se`, `duckduckgo.com`, `www.kernel.org`, `www.gnu.org`, `datatracker.ietf.org`, `www.eff.org`. **Decision: green** — proceed.

If the probe had failed, this ADR would document deferral instead and the hostname ladder would remain three-layer until WFP-callout-driver work was justified.

### Architecture

Two new files plus one DI registration:

- **`Beholder.Core/Tls/TlsClientHelloParser.cs`** — pure library code, fully unit-tested. `static bool TryExtractSni(ReadOnlySpan<byte> tlsRecord, out string? hostname)`. Walks the TLS record + handshake header + ClientHello body + extensions list, finds extension type 0 (`server_name`), returns the first `host_name` entry. Defensive at every length field; returns `false` (no exception) on every malformed-or-unsupported case. Lives in Core with no platform dependencies — the future Linux SNI source will reuse it unchanged.
- **`Beholder.Daemon.Windows/PktmonSniSource.cs`** — `IHostedService` that owns the ETW session, runs a hot-path callback that pushes packet snapshots onto a bounded `Channel<PacketSnapshot>`, and runs a worker thread that dedupes by `PktGroupId`, locates the Ethernet frame within Pktmon's variable-length metadata via a scan, parses the IPv4+TCP headers, calls the parser, and ingests successful (hostname, dest IP) pairs via `IDnsCacheIngest.IngestResolved` + retroactive `IDnsHostnameBackfill.BackfillHostnameAsync`. Mirrors `EtwDnsCache`'s shape exactly.
- **`Beholder.Daemon.Windows/SniOptions.cs`** — config class with `EnableSniCapture: bool = true` (default-on, opt-out), `QueueCapacity: 50000`, `SessionBufferSizeMB: 16`, `DedupCapacity: 16384`.

The decorator (`ReverseDnsFallbackCache` from ADR 005) needs no change. SNI hits land in the inner `EtwDnsCache._cache` via the existing `IngestResolved` seam, and the decorator's `Resolve` correctly short-circuits on inner-cache hits before attempting reverse DNS. SNI hostnames are at least as authoritative as PTR records (the *client* explicitly requested the hostname, so it's what the user actually intended), so the priority order is correct.

### Adaptive Ethernet-frame finder

A subtle finding from Phase 0 + production smoke testing: Pktmon's per-event metadata is *variable-length* across event subtypes. The probe's first batch of sample events had the Ethernet frame at event-data offset 32; production events have a 2-byte-or-more prefix shifting it further. A fixed-offset parser quietly drops every packet.

`FindIpv4EthernetStart` scans a 48-byte window from the start of the post-metadata buffer for the IPv4 ethertype signature (`0x08 0x00`) followed by a plausible IPv4 version+IHL byte (`0x45..0x4F`). The first match wins. False-positive risk is essentially zero in practice (the signature is 3 bytes with strong structural validation downstream).

### Hot-path discipline

Per `PRINCIPLES.md §Daemon Hot Path`:
- ETW callback extracts the `PktGroupId` (8 bytes from event-data offset 0, little-endian) and a managed-array copy of the post-metadata bytes (from offset 32 onward — safe because it never under-cuts a real Ethernet frame), then pushes onto the bounded channel and returns.
- Worker thread dedupes, scans, parses, ingests, backfills.
- Channel is `BoundedChannelOptions` with `FullMode = DropOldest` and capacity 50 000 (~75 MB worst-case for packet copies). Drop count is metered.
- Periodic stats log at Information level every 30 s: `observed`, `deduped`, `tcp443`, `sni`, `backfillFailures`, `dropped`.

### Stats log example (smoke test)

After 5 fresh `Invoke-WebRequest` calls during a 30 s window:

```
SNI capture stats (running): observed=1348, deduped=1348, tcp443=356, sni=20, backfillFailures=0, dropped=0
```

5 hostnames × ~4 NDIS-layer appearances per ClientHello = 20 SNI extractions. Backfills succeed. No queue pressure.

## Consequences

- **Positive: closes the long-lived-connection hostname gap.** Connections whose original DNS lookup has aged out of every cache (HTTP/2 keep-alive, WebSocket reuse, browser internal-cache hits) get hostnames on every fresh TLS handshake — which happens often enough on real workloads to provide effective coverage.
- **Positive: closes the DoH bypass for the residual class too.** Even with browser DoH enabled (Mode 2 or 3), every TLS handshake still emits a plaintext SNI on the wire. The capture sees it regardless of how DNS was resolved.
- **Positive: uses Windows' built-in tooling.** No third-party drivers, no install footprint, no signing logistics. Pktmon ships with Windows 10+ and is supported by Microsoft.
- **Positive: integration is minimal.** The parser lives in Core (zero platform deps). The source uses the existing `IDnsCacheIngest` + `IDnsHostnameBackfill` seams from ADRs 004 and 005. The reverse-DNS decorator is unchanged.
- **Negative: depends on Pktmon being enabled.** The daemon shells out to `pktmon` to enable capture. If Pktmon is disabled in this Windows edition (e.g. Windows Server with limited installations), or if the CLI fails for any reason, the source logs a warning and skips itself. The daemon still runs with the three-layer hostname ladder.
- **Negative: variable-length metadata in PktMon events.** Different event subtypes have different prefix lengths before the Ethernet frame. The scan-based finder handles this but it's a known fragility — a future Windows update could change the layout. Smoke testing on each Windows release is the safety net.
- **Negative: TLS 1.3 ECH defeats SNI capture by design.** When ECH is enabled (Chrome via `chrome://flags`, Firefox `network.dns.echconfig.enabled`), the real SNI is encrypted in the outer ClientHello and the parser correctly returns `false`. Those flows fall through to reverse DNS, then raw IP. Today ECH is opt-in for browsers and not in widespread production use; if it becomes the default, the gap reopens.
- **Negative: no privacy reduction.** SNI is plaintext-on-the-wire — every network operator on the path already sees it. Capturing locally adds no privacy surface that wasn't already exposed. Worth documenting explicitly in case future audit raises the question.

## Scope

Wired on Windows only (Pktmon is Windows-specific). The parser lives in `Beholder.Core` and is platform-agnostic; the future Linux SNI source (eBPF or netfilter NFLOG) would reuse it unchanged.

## Kill-switch

Set `Sni__EnableSniCapture=false` (env var) or in `appsettings.json` under section `"Sni"`:

```json
{
  "Sni": {
    "EnableSniCapture": false
  }
}
```

Snapshot at construction; not hot-reloadable. Matches the `EnablePreload` and `EnableReverseDnsFallback` contracts.

## Out of scope

- **5-tuple correlation between SNI and individual flow events.** SNI gives `(srcIp, srcPort, dstIp, dstPort, hostname)` — five fields. The DNS cache is keyed by destination IP only. We ingest as `(hostname, dstIp)` and lose source-port specificity. Last-writer-wins matches the existing DNS-cache semantics.
- **Eager SQLite persistence of SNI hits.** SNI ingestions for IPs with no active flow this tick stay in memory only and are lost on daemon restart — until the next handshake re-emits the SNI. Acceptable for v1; matches DNS-side behaviour.
- **HTTP/3 (QUIC) SNI.** UDP/443 also carries SNI in plaintext during the initial CRYPTO frame. Same parsing approach but a different packet structure. Defer to a follow-up.
- **VLAN-tagged Ethernet (802.1Q).** The daemon doesn't run on VLAN-trunked interfaces today. If it ever does, the Ethernet-frame finder would need a 4-byte tag offset.
- **IPv6.** Same Ethernet-frame finding logic but a different IP header layout. Not implemented in v1.
- **Encrypted ClientHello (ECH) recovery.** Where ECH is deployed, SNI is encrypted and the parser correctly returns `false`. Those flows fall through to reverse DNS. Recovering the encrypted SNI would require ECH key access, which clients don't share with passive observers. Out of scope.
- **WinDivert / Npcap / custom WFP driver fallback.** Explicitly ruled out per the user's "no drivers" decision. If a future Windows update breaks Pktmon's ETW emission, SNI capture is deferred — *not* attempted via fallback.
