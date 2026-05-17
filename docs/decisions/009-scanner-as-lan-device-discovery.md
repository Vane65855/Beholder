# 009: Scanner Scope — LAN Device Discovery (Phase 9)

## Context

Phase 9's `SCANNER` tab is wired into the top navigation but `ScannerTabView.axaml` is a "Scanner tab content (deferred)" placeholder and `ScannerTabViewModel` is a stub with no fields and no behavior. `docs/phases.md` §6 (Phase 9) lists four plausible feature directions and explicitly defers the choice to a scoping ADR:

> Likely candidate features: port scan of locally-known destinations, vulnerability lookup against a CVE feed, anomaly detection (deviation-from-baseline alerts on existing flows), network discovery (LAN sweep).

The four candidates compared against Beholder's "see what your machine is doing on the network" thesis:

| Candidate | Fit | Why / Why not |
|---|---|---|
| **LAN device discovery** | ★★★★★ | Direct extension of the visibility thesis from "this machine" to "this machine + its peers." Reuses existing infrastructure: the cross-link from a discovered LAN host to its traffic in the Traffic tab is a one-RPC reuse, not a new pipeline. Mirrors GlassWire's "Things" tab — the project's most-cited reference comparison per UI_QUALITY_STANDARDS §7. |
| Port scan of locally-known destinations | ★★ | Active reconnaissance of WAN destinations crosses from "monitoring" into "scanning a third party." Legal/ethical surface (CFAA in the US, equivalents elsewhere) for a feature whose value to a desktop user is unclear. |
| CVE feed lookup | ★ | Requires an outbound CVE feed connection (violates Beholder's "no outbound network on the daemon" posture per `PRINCIPLES.md`). Even with a manual fetcher pattern (like `Beholder.Tools.GeoIpFetcher`), the matching surface — process → CPE → CVE — is enormous and out of scope for a desktop tool. |
| Anomaly detection on existing flows | ★★ | Genuinely interesting but conceptually distinct from "scanning." Belongs in its own phase if it ships at all; doesn't fit "Scanner" as a label. |

LAN discovery also resolves the longest-standing roadmap ambiguity: Phase 13.6 (Settings → Scanner section) was blocked on "*content depends on Phase 9 outcome*" and can now be designed concretely.

## Decision

**Scanner = LAN device discovery.** The other three candidates are rejected (see Out of Scope). The remainder of this ADR defines the feature surface concretely enough that Phase 9 can be split into sub-phases without further design discussion.

### Discovery method

**Active probing.** Three protocols, all link-local, all standard practice:

1. **ARP probes** across the local /24 (or whatever prefix the primary NIC reports). One ARP request per IP, listen for responses. Cheap (~256 layer-2 broadcasts on a /24, completes in seconds). Devices that respond are present; devices that don't respond are absent or filtering ARP (rare).
2. **mDNS service-discovery queries** (`_services._dns-sd._udp.local`) for hostnames. Most modern devices (Apple, Linux with Avahi, recent Windows, IoT firmwares) respond.
3. **NetBIOS name queries** for the Windows-flavored hostname fallback when mDNS is silent. Legacy but still common on small business / home networks with older Windows machines.

Passive-only (ARP cache walk + listening) was considered and rejected as the default: it produces a sparse picture (devices invisible until they speak), defeats the "what's on my LAN right now" use case, and provides no hostname resolution. Active probing within the local subnet is what every comparable tool does (Fing, GlassWire, Angry IP Scanner, nmap's `-sn`) and matches the ambient ARP/mDNS traffic any normal device generates during DHCP/connectivity checks.

The probes never leave the local subnet. No ICMP echo to gateway/internet. No TCP SYN probes. No UDP service probes beyond the three discovery protocols above.

### Per-device metadata

The Scanner records and surfaces:

| Field | Source | Notes |
|---|---|---|
| `mac` | ARP response, also `GetIpNetTable2` for cache | **The identity key.** See "Identity model" below. |
| `ip` | ARP response | Mutable (DHCP lease renewal). Tracked over time per `mac`. |
| `vendor` | OUI lookup on `mac[0:3]` | From embedded IEEE OUI list; see "OUI database" below. |
| `hostname` | Three-layer ladder (mDNS → NetBIOS → reverse-DNS PTR) | Mirrors Phase 5.4.4's hostname-resolution pattern for WAN destinations. |
| `first_seen_unix_ns` | Daemon clock | When this `mac` first responded to any probe. |
| `last_seen_unix_ns` | Daemon clock | When this `mac` last responded. |

Out: open-port scan per device (crosses into reconnaissance), OS fingerprinting (TTL/window heuristics; nmap territory), WiFi-specific metadata (SSID, signal strength — Beholder is a network tool, not a WiFi tool).

### Identity model

**Identity = MAC address.** Rationale:

- **IP is mutable** (DHCP lease changes, static-vs-dynamic configuration drift). Keying on IP would produce false "new device" entries every lease renewal.
- **MAC is the durable layer-2 identifier.** Standard practice for LAN device tracking (Fing, GlassWire, every router admin UI).
- **MAC randomization is a known limitation.** Modern iOS / Android / Windows 11 randomize MACs per-SSID. The Scanner will record more "new device" entries than philosophically ideal for these. This is the same trade-off as ADR 007's path-based fallback for unsigned binaries: **we don't pretend to know more than is observable on the wire.** The Scanner tab's UX makes "this is a tracked snapshot of what answered ARP" explicit rather than "this is a roster of physical devices."

When a known `mac` appears at a different `ip`, we update the row (no new entry, no event). When a known `ip` is associated with a different `mac`, we record a `LanDeviceMacChanged` event in the chain (potential ARP spoof signal, though more commonly just DHCP reassignment).

### Event log and alert taxonomy

**No new alert kinds.** ADR 002 caps alerts at three (`NewProcess`, `HashChanged`, `ChainError`) on alert-fatigue grounds. LAN devices on a home network come and go constantly (phones reconnecting, IoT power blips, guests). Auto-alerting on every new device would reproduce exactly the failure mode ADR 002 was written to prevent.

Instead, the Scanner mirrors the Firewall pattern:

- **Chain events** (`LanDeviceFirstSeen`, `LanDeviceMacChanged`) are written to `event_log` with `kind` strings matching the existing convention. They are **auditable** (chain-hashed, never deleted) and **surfaced in a recent-activity strip in the Scanner tab**, the same way firewall rule changes appear in the Firewall tab.
- **They are NOT alerts.** They do not appear in the Alerts tab, do not produce OS toasts, do not increment any alert badge.

Promoting a chain event to an alert is a Phase 13 Settings concern ("notify me when a new device appears on my LAN" toggle, opt-in, default off). If that ships, it adds a `NewLanDevice` alert kind — but that's a separate ADR superseding 002, not part of Phase 9.

### Cross-link with the Traffic tab

**Day-1 feature.** Clicking a LAN device in the Scanner deep-links to the Traffic tab with the table filtered to flows where `remote_address == device.ip`. Implementation reuses the existing Alerts → Firewall deep-link pattern (selected-tab message + a filter parameter on the target VM). No new RPC; the existing `GetProcessDestinations` returns rows keyed on `remote_address` already.

This is the differentiator from Fing/GlassWire. Those tools show you devices; we show you devices **and the per-process flows with each device**, because we already have the per-process flow log.

### OUI database

Embed the IEEE OUI list as a build-time asset (analogous to the embedded Natural Earth GeoJSON for the world map). Updates ship in Beholder releases; users who want fresher data run a `Beholder.Tools.OuiFetcher` console tool that downloads the latest list from IEEE's public registration page (CC0 / public domain data). Same operational pattern as `Beholder.Tools.GeoIpFetcher`.

OUI is ~30k vendor prefixes, ~1MB compressed. Lookup is `mac[0:3]` → vendor string; the entire table fits in memory at daemon start.

### Cross-platform shape

`ILanDeviceProbe` lives in `Beholder.Core`. Windows implementation (`WindowsLanDeviceProbe`) lives in `Beholder.Daemon.Windows` and uses:

- `GetIpNetTable2` (iphlpapi.dll) for the existing ARP cache
- `SendARP` (iphlpapi.dll) for active ARP probes
- `DnsServiceBrowse` (dnsapi.dll) for mDNS — same DLL used by ADR 004's preload path
- `NetBIOSAdapterStatus` (netapi32.dll) for NetBIOS name queries

Linux implementation (`LinuxLanDeviceProbe`) is deferred to whenever `Beholder.Daemon.Linux` ships its first real implementations. Phase 9 ships Windows-only on the daemon side, mirroring the existing Windows-first posture.

OUI lookup, hostname-ladder orchestration, and the scan scheduler are all platform-agnostic and live in `Beholder.Daemon/Scanner/` (parallel to `Beholder.Daemon/Detectors/`).

### Auto-scan cadence

Every 5 minutes by default. Configurable via `Scanner:ScanIntervalSeconds` in `appsettings.json`, with the same `IOptionsMonitor<T>` live-reload behavior as existing options. Manual refresh button in the Scanner tab UI for on-demand scans. Probe rate is bounded (one ARP per ~5ms across the subnet) to avoid burst patterns that overly aggressive scanners produce.

### RPC surface additions

Two new RPCs on `beholder_local.proto`:

- `ListLanDevices(ListLanDevicesRequest) → ListLanDevicesResponse` — paginated by last-seen DESC. Request supports filtering (e.g., `seen_since_unix_ns` for "active in the last hour").
- `TriggerScan(TriggerScanRequest) → TriggerScanResponse` — manual refresh button binding. Returns immediately (200 OK) and the scan completes async; new devices stream via the existing `SubscribeEvents` channel.

The existing `SubscribeEvents` stream carries `LanDeviceFirstSeen` / `LanDeviceMacChanged` chain events alongside the existing `DaemonEvent` payloads. The protocol's `DaemonEvent` oneof gains two new variants.

RPC surface count: 18 → 20.

## Consequences

**Schema:**

```sql
CREATE TABLE lan_device (
    mac                 TEXT    PRIMARY KEY,   -- canonical lowercase hex with colons
    ip                  TEXT    NOT NULL,
    vendor              TEXT    NULL,          -- NULL if OUI not in DB
    hostname            TEXT    NULL,          -- NULL if all three resolution layers failed
    first_seen_unix_ns  INTEGER NOT NULL,
    last_seen_unix_ns   INTEGER NOT NULL
);
CREATE INDEX idx_lan_device_ip ON lan_device(ip);
CREATE INDEX idx_lan_device_last_seen ON lan_device(last_seen_unix_ns);
```

Plus the new `kind` values in `event_log` (`LanDeviceFirstSeen`, `LanDeviceMacChanged`).

**Code:**

- `Beholder.Core/ILanDeviceProbe.cs` (new interface), `LanDevice.cs` (record).
- `Beholder.Core/ILanDeviceStore.cs` (storage interface), implemented by an extension to `SqliteTrafficStore` or a new `SqliteLanDeviceStore` (decide at planning time).
- `Beholder.Daemon/Scanner/` — `LanScannerService` (hosted service, scheduler), `OuiVendorLookup`, `HostnameResolutionLadder`.
- `Beholder.Daemon.Windows/WindowsLanDeviceProbe.cs` — P/Invoke layer.
- `Beholder.Tools.OuiFetcher` — new console project.
- `Beholder.Ui/ViewModels/ScannerTabViewModel.cs` — replace the stub.
- `Beholder.Ui/Views/Tabs/ScannerTabView.axaml` — replace the placeholder.

**ARCHITECTURE.md:**

- Alert taxonomy table gets a footnote: "LAN device discovery events (`LanDeviceFirstSeen`, `LanDeviceMacChanged`) are recorded in the chain for audit but are not alerts — see ADR 009."
- `event_log.kind` enumeration comment extends.
- New "LAN Discovery" subsection under "Data Collection" alongside the existing ETW / DNS / SNI subsections.

**Cross-platform:**

Linux daemon doesn't exist yet, so the LAN scanner's Linux implementation is documented as "ships with the Linux daemon when that daemon stabilizes." No regression — the Linux daemon is itself unimplemented today.

**Trust / network surface:**

The daemon's posture (no outbound WAN connections by default; uplink is opt-in) is preserved. ARP/mDNS/NetBIOS are all link-local: layer-2 broadcasts (ARP, NetBIOS) and link-local multicast (mDNS 224.0.0.251 / FF02::FB). They never traverse the gateway. No new firewall implications for the user — these are protocols every modern OS already broadcasts during normal operation.

**Performance:**

- Per-scan cost: ~256 ARP probes + ~10 mDNS queries + ~10 NetBIOS queries per 5-minute interval. Negligible.
- Memory: OUI table ~1MB resident. `lan_device` row count is bounded by physical reality (small dozens on a home LAN, low hundreds on a business LAN).
- SQLite: writes per scan = O(devices changed). Bounded.

**Testing:**

- `FakeLanDeviceProbe` test double mirrors the existing `FakeFlowSource` pattern.
- Mocking mDNS / NetBIOS requires either a fake at the `ILanDeviceProbe` boundary (simpler) or a fake at the OS API layer (deeper). The boundary-level fake is sufficient for Scanner-VM and scheduler tests; the OS-API tests are integration-tier and ship with the daemon-side `WindowsLanDeviceProbe` implementation.
- Cross-link to Traffic tab is end-to-end testable in `Beholder.Tests` using existing `FakeDaemonClient` patterns.

## Out of scope

- **Port scanning on LAN hosts.** Active TCP/UDP probing of LAN devices is reconnaissance, not monitoring. Even on devices you own. Out of character for Beholder.
- **CVE feed integration.** Requires daemon outbound network (violates `PRINCIPLES.md` daemon-egress posture). The CPE matching surface is also enormous; this is a separate product, not a Beholder feature.
- **Anomaly detection on existing flows.** Different concept from "scanning." Belongs in its own scoping discussion if it ships at all.
- **OS fingerprinting.** TTL / TCP window-size heuristics get into nmap territory and produce confident-sounding wrong answers. Out.
- **WiFi-specific metadata** (SSID, signal strength, channel). Beholder is a network tool, not a WiFi-specific tool. Out.
- **Active probing of WAN destinations.** Layer-2 broadcasts are link-local; the daemon never sends discovery probes beyond the local subnet.
- **IPv6 device discovery.** Phase 9 ships IPv4 only. IPv6 neighbor discovery (NDP) is a fast-follow if/when the user base needs it.
- **Multi-subnet enumeration.** Phase 9 scans the primary NIC's primary IPv4 subnet. Multi-NIC machines (VPN tunnels, virtual adapters, multiple physical NICs) scan only the primary. Multi-NIC support is a fast-follow.
- **`NewLanDevice` alert kind.** Deferred to a future ADR that supersedes 002 and ships alongside a Phase 13 Settings toggle.
- **Auto-refresh of the embedded OUI database.** The daemon doesn't dial out. Users run `Beholder.Tools.OuiFetcher` manually when they want fresher vendor names.
- **Linux daemon-side probe.** Ships with the Linux daemon stabilization, not Phase 9.

## Sub-phase skeleton (for the implementation plan that follows)

- **9.1 — Schema + OUI database.** SQLite migration, `Beholder.Tools.OuiFetcher`, embedded OUI snapshot.
- **9.2 — Daemon scan engine.** `ILanDeviceProbe` + `WindowsLanDeviceProbe` + `LanScannerService` hosted-service scheduler + hostname-resolution ladder.
- **9.3 — RPC surface.** `ListLanDevices`, `TriggerScan`, `DaemonEvent` oneof additions.
- **9.4 — Scanner tab UI.** Device list + recent-activity strip + manual-refresh button.
- **9.5 — Cross-link to Traffic tab.** Click a device → Traffic tab filtered by `remote_address`.
- **9.6 — Tests + verification.** UI quality bar (three window sizes, 30s + real LAN traffic, edge cases including empty LAN / single-device / 50+-device scenarios).

Each sub-phase ships as a separate atomic commit per the established workflow.
