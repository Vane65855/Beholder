# Beholder NMT

**Network Monitoring Tool** — see what your machine is doing on the network.

Beholder NMT is an open-source network monitoring and firewall management application for Windows and Linux. It provides real-time per-process traffic visibility, a simple application firewall, geographic traffic mapping, and a tamper-evident audit log of all network activity.

## Features

- **Per-process traffic monitoring** — see which applications are using the network, how much data they're sending and receiving, and where that data is going
- **Application firewall** — block or allow network access per application, per direction (inbound/outbound), with one click
- **Geographic traffic map** — world heat map showing which countries your machine is communicating with, powered by DB-IP Lite
- **Tamper-evident audit log** — all events are stored in a hash-chained SQLite database with optional Ed25519 signed checkpoints, making it cryptographically difficult to alter historical records
- **Alert system** — notifications for new processes accessing the network, binary hash changes, and chain integrity warnings
- **Cross-platform** — runs on Windows (as a service) and Linux (as a systemd unit) with a shared Avalonia UI
- **Uplink-ready** — optional outbound connection to a remote aggregator for centralized monitoring (aggregator sold separately; the daemon and UI are fully functional standalone)

## Architecture

Beholder NMT consists of two components:

**Daemon** (`Beholder.Daemon`) — a background service that collects network telemetry, enforces firewall rules, resolves IP geolocation, and maintains the audit log. Runs with elevated privileges. Communicates with the UI over a local named pipe (Windows) or Unix domain socket (Linux).

**UI** (`Beholder.Ui`) — an Avalonia desktop application that connects to the local daemon and provides the user interface. Runs with normal user privileges. Does not access the network directly.

## Building

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Windows 10+ or a modern Linux distribution
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
# Linux: use sudo or grant NET_ADMIN + NET_RAW + SYS_PTRACE capabilities
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
Beholder.Core              — Models, interfaces, shared logic
Beholder.Protocol          — gRPC protocol definitions (.proto files)
Beholder.Daemon            — Background service host
Beholder.Daemon.Windows    — Windows ETW + WFP implementations
Beholder.Daemon.Linux      — Linux netlink + nftables implementations
Beholder.Daemon.GeoIp      — IP geolocation via DB-IP Lite
Beholder.Daemon.Uplink     — Optional outbound aggregator connection
Beholder.Ui                — Avalonia desktop UI
Beholder.Tests             — Unit and integration tests
Beholder.Tests.UplinkStub  — Test stub for the uplink protocol
```

## Data Sources

- **IP geolocation**: [DB-IP Lite](https://db-ip.com/db/lite.php) (CC BY 4.0). IP geolocation by [DB-IP](https://db-ip.com).
- **Network telemetry**: ETW (Windows) / netlink + /proc (Linux) — no third-party collection agents

## Configuration

The daemon reads configuration from `beholder.toml` in its working directory. A default configuration file is created on first run. Key settings:

```toml
[daemon]
storage_path = "/var/lib/beholder"        # where the SQLite database lives
log_level = "Information"

[firewall]
default_policy = "allow"                  # "allow" or "block" for unseen processes

[geoip]
database_path = "data/dbip-country-lite.mmdb"
auto_update = true                        # check for monthly MMDB updates

[uplink]
enabled = false                           # off by default, no outbound connections
# endpoint = "https://aggregator.example.com:8443"
# key_file = "/etc/beholder/uplink.key"
# trusted_issuers = ["/etc/beholder/issuers/"]
```

## License

Copyright (C) 2026 [Your Name]

Beholder NMT (daemon and UI) is licensed under the **GNU Affero General Public License v3.0 or later** (AGPL-3.0-or-later). See [LICENSE](LICENSE) for the full text.

The AGPL-3.0 license means:
- You are free to use, modify, and distribute this software
- If you modify and deploy it as a network service, you must share your modifications under the same license
- The `.proto` files in `Beholder.Protocol` define an interface specification; implementing the protocol in a separate program does not create a derivative work

### Third-Party Attributions

- IP geolocation data by [DB-IP](https://db-ip.com), licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
- Country boundary data via LiveCharts2 (Natural Earth, public domain)
