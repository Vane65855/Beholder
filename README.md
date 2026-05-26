# Beholder NMT

**Network Monitoring Tool** — see what your machine is doing on the network.

Beholder NMT is an open-source network monitoring and firewall management application for Windows (Linux planned). It provides real-time per-process traffic visibility, a simple application firewall, alert detection for new processes and binary tampering, and a tamper-evident audit log of all network activity.

**Status:** Pre-release / under active development. All core tabs shipped end-to-end: Traffic (with the Phase 8 world heatmap MAP sub-view + top-3 destinations per country on hover), Firewall (ALLOW/BLOCK/DEFAULT pills + master toggle + activity strip), Alerts (master-detail + OS toasts + spoof detection), Scanner (LAN device discovery + manual labels), and as of **Phase 13.4** — the **Settings** tab gains its third interactive sub-phase. 13.2 added Recording + Hostname Resolution, 13.3 added Alerts, and 13.4 adds the **Scanner** section with one toggle: `EnableHostnameResolution` (live, per scan tick). The 13.4 implementation surfaced a construction-vs-StartAsync ordering bug — the DI factory read the state singleton's value at construction time, but persisted overrides only apply during `SettingsOverridesService.StartAsync`. Fix: always inject both hostname-resolution probes into `WindowsLanDeviceProbe` and gate at scan time via the state singleton — sidesteps the ordering issue AND lifts the "(takes effect on next daemon start)" caption to live UX. Sets a precedent for future sub-phases: when snapshot-at-construction produces override-ordering bugs, prefer per-call live reads over deferred-construction fixes. RPC surface 23 → 24. New `EventKind.ScannerSettingsChanged = 14` + `ScannerSettingsPayloadEncoder`. **1308 tests** pass deterministically (was 1294 after 13.3; +14 in 13.4 across state-singleton, payload-encoder, RPC-handler, and VM-toggle tests). After 13.4, four Settings sections actively mutate daemon state. Only 13.5 (Storage retention preset switcher) remains as a non-trivial settings sub-phase. See [`docs/phases.md`](docs/phases.md) for the current state, lessons learned, and the full roadmap.

**Previous checkpoint:** Phase 13.3 added the **Alerts** section (`EnableNewProcessDetection` / `EnableHashChangeDetection` / `EnableChainIntegrityMonitor` — all live), with a security-fix behavior change: the mandatory startup chain verify is no longer skippable via the UI toggle. See [ADR 010](docs/decisions/010-runtime-mutable-settings.md) for the runtime-mutable-settings pattern that 13.3 + 13.4 both reuse.

## Features

### Working today

- **Per-process traffic monitoring** — which applications are using the network, how much they're sending and receiving, and where it's going. Stitched five-tier rollup serves 1-second fidelity on recent data and progressively coarser bucket sizes on older data, all from the same query.
- **Application firewall** — three-state ALLOW / BLOCK / DEFAULT pills per direction, with a master ON/OFF toggle that disables every Beholder-managed rule without losing the configuration. Active vs. inactive process grouping with orphaned-rule warning glyphs for uninstalled apps.
- **Alert system with logical-app-identity dedup** — fires `NewProcess` once per logical app (publisher + product + install-root from PE VersionInfo + Authenticode signature) rather than once per file path, so Squirrel auto-updaters like Discord, GitHub Desktop, and Slack stay silent across version bumps. A same-identity match with a *different* signing publisher fires `HashChanged` with a publisher-mismatch summary — spoof detection in the first network monitor on Windows to handle this class. See [ADR 007](docs/decisions/007-logical-app-identity-and-spoof-detection.md).
- **Tamper-evident audit log** — every state-changing event is stored as a SHA-256 hash-chained row in SQLite. Chain integrity is verified periodically and on startup; failures surface as `ChainError` alerts.
- **OS-native notifications** — Windows toasts via the unpackaged-exe path (Microsoft.Toolkit.Uwp.Notifications), with click-activation that restores the window and selects the matched alert in the Alerts tab.
- **Comprehensive hostname capture** — four-layer ladder: (1) Windows DNS resolver-cache preload at startup, (2) live `Microsoft-Windows-DNS-Client` ETW capture, (3) reverse-DNS PTR fallback for direct-IP destinations, (4) SNI extraction from TCP/443 ClientHello packets via `Microsoft-Windows-PktMon` ETW. See ADRs [004](docs/decisions/004-dns-cache-preload-undocumented-api.md), [005](docs/decisions/005-reverse-dns-fallback.md), [006](docs/decisions/006-sni-capture.md).
- **Geographic traffic map** — world heatmap of per-country traffic, accessible from the Traffic tab's MAP sub-view toggle. Custom Canvas-rendered Natural Earth 110m country polygons (CC0 public domain, embedded as a ~170 KB asset, zero external network for tiles), equirectangular projection, 5-stop heatmap ramp. Hover any country for a tooltip showing the country name, total byte totals, and the **top-3 destinations by total bytes** (hostname or raw-IP fallback) scoped to the active time range + per-process filter. LAN ("--") and Unknown ("??") traffic surface in a caption strip below the map since they have no geographic location.

### Planned
- **Uplink to remote aggregator** (Phase 10) — optional outbound TLS gRPC to a centralized aggregator for fleet monitoring. Off by default; the daemon and UI are fully functional standalone.
- **Signed checkpoints + chain export** (Phase 11) — Ed25519 signed checkpoints over the audit chain; signed JSON export of filtered events.
- **Linux platform** — `Beholder.Daemon.Linux` (netlink + nftables) and Linux UI port. Project stubs exist; Windows is the primary platform today.
- **Scanner tab** (Phase 9, unscoped) — feature surface defined by ADR before implementation.

## Architecture

Beholder NMT consists of two components:

**Daemon** (`Beholder.Daemon`) — a background service that collects network telemetry, enforces firewall rules, resolves IP geolocation, runs the detector pipeline (`NewProcessDetector` + `BinaryHashMonitor` + `ChainIntegrityMonitor`), and maintains the audit log. Runs with elevated privileges. Communicates with the UI over a local named pipe (Windows) or Unix domain socket (Linux, planned).

**UI** (`Beholder.Ui`) — an Avalonia desktop application that connects to the local daemon and provides the user interface. Runs with normal user privileges. Does not access the network directly.

## Building

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Windows 10 1809+ (for the toast-notification surface) or a modern Linux distribution
- Git

### Build

```bash
git clone https://github.com/Vane65855/Beholder.git
cd Beholder
dotnet build
```

### Run (Development)

```bash
# Start the daemon (requires elevated privileges)
# Windows: run terminal as Administrator
# Linux: use sudo or grant NET_ADMIN + NET_RAW + SYS_PTRACE capabilities (Linux daemon not yet implemented)
dotnet run --project Beholder.Daemon

# In a separate terminal, start the UI
dotnet run --project Beholder.Ui
```

### Run Tests

```bash
dotnet test
```

## Project Structure

```
Beholder.Core              — Models, interfaces, shared logic (zero OS dependencies)
Beholder.Protocol          — gRPC protocol definitions (.proto files)
Beholder.Daemon            — Background service host (DI, scheduling, IPC server)
Beholder.Daemon.Windows    — Windows ETW + WFP + Authenticode + PE VersionInfo
Beholder.Daemon.Linux      — Linux netlink + nftables (stub; no impl yet)
Beholder.Daemon.GeoIp      — IP geolocation via DB-IP Lite MMDB
Beholder.Daemon.Uplink     — Optional outbound aggregator connection (stub)
Beholder.Ui                — Avalonia desktop UI (MVVM). Single project; Windows-only code (OS toast service) lives inline behind `#if PLATFORM_WINDOWS` per ADR 008
Beholder.Tests             — Unit and integration tests
Beholder.Tests.UplinkStub  — Reference gRPC server for uplink integration testing
Beholder.Tools.GeoIpFetcher — Console tool that downloads the DB-IP Lite MMDB
```

## Data Sources

- **IP geolocation**: [DB-IP Lite](https://db-ip.com/db/lite.php) (CC BY 4.0). IP geolocation by [DB-IP](https://db-ip.com).
- **Network telemetry**: ETW (Windows) — `NT Kernel Logger` for TCP/UDP, `Microsoft-Windows-DNS-Client` for DNS resolution, `Microsoft-Windows-PktMon` for SNI extraction. No third-party collection agents, no kernel drivers.

## Configuration

The daemon reads configuration from `appsettings.json` in its working directory via the standard ASP.NET Core options binding. A default file is created on first run. The key binding surfaces:

```json
{
  "Rollup": {
    "Preset": "Balanced"
  },
  "Recording": {
    "FilterSelfTraffic": true
  },
  "Dns": {
    "EnablePreload": true,
    "EnableReverseDnsFallback": true
  },
  "Sni": {
    "EnableSniCapture": true
  },
  "Alert": {
    "EnableNewProcessDetection": true,
    "EnableHashChangeDetection": true,
    "BinaryHashCheckIntervalMinutes": 60
  },
  "Firewall": {
    "EnableEnforcement": true
  },
  "Scanner": {
    "ScanIntervalSeconds": 300,
    "EnableHostnameResolution": true
  }
}
```

`Rollup.Preset` is `Balanced` (default, ~1.4 GB year-1 footprint) or `Compact` (~580 MB). Runtime-mutable toggles — currently just master firewall enforcement — flow through the gRPC `SetFirewallEnabled` RPC, not config edits. Most other options bind via `IOptionsMonitor<T>` so flipping a value in `appsettings.json` and saving takes effect on the next event without a daemon restart.

## License

Copyright (C) 2026 Vane65855

Beholder NMT (daemon and UI) is licensed under the **GNU Affero General Public License v3.0 or later** (AGPL-3.0-or-later). See [LICENSE](LICENSE) for the full text.

The AGPL-3.0 license means:
- You are free to use, modify, and distribute this software
- If you modify and deploy it as a network service, you must share your modifications under the same license
- The `.proto` files in `Beholder.Protocol` define an interface specification; implementing the protocol in a separate program does not create a derivative work

### Third-Party Attributions

- IP geolocation data by [DB-IP](https://db-ip.com), licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
- Windows toast notifications via [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) (MIT License)
