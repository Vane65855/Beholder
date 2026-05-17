# Manual Test: LAN Scanner (Phase 9.2)

`WindowsLanDeviceProbe` + `ArpScanProbe` + `IphlpapiInterop` can't be
unit-tested — they need a real LAN with actual devices responding to ARP
requests, plus the OS's iphlpapi.dll. This runbook is the end-to-end
verification.

The `LanScannerService` orchestration logic is covered by
`LanScannerServiceTests` in `Beholder.Tests` (against a `FakeLanDeviceProbe`);
this runbook covers the Windows probe stack.

## Requirements

- Windows 10/11 or Server 2019+
- .NET 10 SDK
- A connected LAN interface (Ethernet or WiFi) with a default gateway
- Administrator terminal (Run as administrator — NOT a non-elevated terminal)
- Note: Admin elevation isn't required for `SendARP` itself, but the daemon's
  other startup paths (ETW, WFP) require it; running unelevated will fail on
  the first ETW session and you'll never see the scanner log lines.

## Steps

1. Build the solution from a normal terminal:
   ```
   dotnet build
   ```
   Expect zero errors, zero warnings.

2. Open an **elevated** terminal and verify elevation:
   ```
   whoami /groups | findstr "S-1-16-12288"
   ```
   A non-empty match means you are running as High Integrity (admin).

3. If `data/oui.csv` is not present (fresh clone without running the fetcher),
   populate it now:
   ```
   dotnet run --project Beholder.Tools.OuiFetcher
   ```
   Expect `Wrote .../data/oui.csv (~3,700,000 bytes)`. Without this file the
   scanner still runs but vendor columns will all be NULL.

4. Start the daemon from the elevated terminal:
   ```
   dotnet run --project Beholder.Daemon
   ```
   Expect the startup log to include:
   ```
   info: Beholder.Daemon.Scanner.LanScannerService[0]
         LAN scanner started (interval 300 s)
   ```
   followed within **~5 seconds** (steady-state — Windows' ARP cache covers
   most devices already) or **~30 seconds** (cold-cache — parallel SendARP
   sweeps the full subnet) by the first scan result:
   ```
   info: Beholder.Daemon.Scanner.LanScannerService[0]
         LAN scanner: N devices observed, M first-seen, 0 mac-changed (K with hostname)
   ```
   On the first run M should equal N (every device is being seen for the first
   time). N typically ranges from 2 (gateway + this machine) to ~20 (home/SMB
   LAN with a few devices and IoT) to ~50+ (busy office network). K (devices
   with a resolved hostname) varies widely by LAN — see the Known non-issues
   section below. K may legitimately be zero on LANs where no device responds
   to mDNS-PTR or NetBIOS NBSTAT (verify with `nbtstat -A <ip>` if in doubt).

   **If the daemon has been running for more than ~30 seconds with no scan-result
   log line**, something is wrong with the Phase 9.2.1 cache-walk + parallel-probe
   path — Phase 9.2's sequential implementation could legitimately take 4+ minutes
   but the 9.2.1 fix should keep wall-clock under 30 s even on a cold cache.

5. From a separate terminal (any privilege level), query the SQLite database:
   ```
   sqlite3 Beholder.Daemon\bin\Debug\net10.0-windows10.0.17763.0\data\beholder.db ^
     "SELECT mac, ip, vendor, hostname, last_seen_unix_ns FROM lan_device ORDER BY last_seen_unix_ns DESC LIMIT 20;"
   ```
   Expect rows for visible LAN devices — at minimum the default gateway, the
   local machine. Vendor column should be populated for devices whose OUI is
   in the IEEE registry; phones/laptops/IoT typically resolve to recognizable
   names (Apple, Inc. / Intel Corporate / Raspberry Pi Trading /
   TP-LINK TECHNOLOGIES). Hostname column (Phase 9.2.5+) is populated for
   devices that respond to mDNS PTR queries (Apple, Linux/Avahi, Chromecast/
   Sonos/Hue, recent IoT) or NetBIOS NBSTAT queries (Windows machines, NAS).
   Random-MAC phones and minimal home routers typically leave hostname NULL.

6. Query the event log for the new chain event kinds:
   ```
   sqlite3 Beholder.Daemon\bin\Debug\net10.0-windows10.0.17763.0\data\beholder.db ^
     "SELECT seq, kind, ts_unix_ns FROM event_log WHERE kind IN ('LanDeviceFirstSeen', 'LanDeviceMacChanged') ORDER BY seq DESC LIMIT 20;"
   ```
   Expect `LanDeviceFirstSeen` rows for each device on the first scan, zero
   `LanDeviceMacChanged` rows in normal operation (the latter only fires when
   a known IP shows up with a different MAC).

7. Stop the daemon with Ctrl+C. Expect the scanner's stop log line with
   lifetime counters:
   ```
   info: Beholder.Daemon.Scanner.LanScannerService[0]
         LAN scanner stopped (totalScans=1, observations=N, firstSeen=N, macChanged=0)
   ```

8. Restart the daemon. Expect the next scan to log `0 first-seen` (every
   device is now known) but the same `N devices observed`:
   ```
   info: Beholder.Daemon.Scanner.LanScannerService[0]
         LAN scanner: N devices observed, 0 first-seen, 0 mac-changed
   ```
   Re-query the event log: no new `LanDeviceFirstSeen` rows should appear
   (chain has zero net writes for repeat observations of known devices).

9. **Failure mode: missing OUI file.** Stop the daemon. Rename
   `data/oui.csv` away (e.g. to `data/oui.csv.bak`). Restart. The scanner
   still functions — vendor column is NULL for newly-discovered MACs but the
   `lan_device` rows still appear. The startup log shows an
   `OuiVendorLookup` warning about the missing file. Restore `data/oui.csv`
   when done.

10. **Failure mode: no LAN connectivity.** With the daemon running on a
    machine with WiFi/Ethernet, disconnect from the network. The next scan
    logs:
    ```
    warn: Beholder.Daemon.Windows.Scanner.WindowsLanDeviceProbe[0]
          LAN scan skipped: no active NIC with a default gateway (no LAN-attached interface)
    ```
    Reconnect and wait for the next interval — scans resume cleanly.

## What success looks like

- `LAN scanner started (interval 300 s)` and `LAN scanner: N devices observed, M first-seen, 0 mac-changed` log lines appear on every cycle.
- `sqlite3` confirms `lan_device` table populates with real devices, with vendor names from the IEEE OUI registry for known prefixes.
- `sqlite3` confirms `event_log` gains `LanDeviceFirstSeen` rows on first observation per device and zero `LanDeviceMacChanged` rows in steady-state operation.
- Restart shows `0 first-seen` (idempotent — devices already known).
- Missing OUI file or disconnected NIC degrade gracefully with a warning log; daemon stays running.

## Known non-issues

- **`N devices observed` doesn't include the local machine if it doesn't answer its own ARP**: expected. Windows often answers ARP for its own IP via the loopback path that `SendARP` bypasses. Devices with manual static ARP entries also won't reply.
- **`hostname` column is NULL for every row in 9.2**: expected. Hostname-resolution sub-probes (mDNS via `DnsServiceBrowse`, NetBIOS via `NbtStatRemote`) ship in Phase 9.2.5. Until then the Scanner tab (Phase 9.4) displays devices by MAC + vendor + IP.
- **MAC randomization causes "new device" churn on modern phones**: expected per ADR 009. Modern iOS / Android / Windows 11 randomize MACs per-SSID; reconnecting devices appear as a new entry with each randomization. This accurately reflects what's observable on the wire.
- **Scan duration scales with subnet size and ARP cache warmth**: expected. Steady-state (cache populated): ~5 s for /24, ~10 s for /22, ~30 s for /20. Cold cache (first run after a fresh boot): roughly double. The 5-minute default interval means even a /20 spends < 10% of wall-clock on scanning.
- **`ARP cache walk skipped: ...` warning at startup**: expected only on pre-Win10 or where `iphlpapi.dll` is stripped — in that degraded mode the scanner falls back to parallel SendARP for the entire subnet (still bounded by the 60 s per-scan deadline). Should not appear on normal Win10 / Win11 installs.
- **K (hostname count) is often less than N (device count), and may be zero on some LANs**: expected. Not every LAN device speaks mDNS or NetBIOS — modern phones with MAC randomization typically advertise neither; minimal home routers respond to neither; many Windows installs have NetBIOS-over-TCP/IP disabled (`TcpipNetbiosOptions=0` on the active NIC, the modern default); many mDNS responders don't answer reverse-IP PTR queries (they advertise services via `_workstation._tcp.local` etc. instead — service-discovery browsing is a deferred 9.2.6 enhancement). To sanity-check whether your LAN has responders: run `nbtstat -A <some-ip>` (NetBIOS) or use a Bonjour tool (`dns-sd -B _services._dns-sd._udp` on macOS, `avahi-browse -a` on Linux); if neither returns names, Beholder's K=0 result is correct behavior. Useful hits typically include the local Windows machine (NetBIOS, when enabled), Apple devices (mDNS, especially with Bonjour-aware peers), Linux/Avahi machines, Chromecast/Sonos/Hue/Roku/network printers (mDNS).
- **mDNS-bonjour port conflict not an issue**: even on machines where the Bonjour service (bundled with iTunes / Adobe Acrobat) has bound UDP port 5353, the scanner uses an ephemeral source port and sets the QU bit per RFC 6762 §5.4 so responders unicast replies to the ephemeral port instead of multicasting to 5353. No port competition.
- **To disable hostname resolution entirely**: set `Scanner:EnableHostnameResolution = false` in `appsettings.json` (or `Scanner__EnableHostnameResolution=false` env var) and restart the daemon. The scanner falls back to ARP-only behavior; the scan-result log line drops the `(K with hostname)` suffix to `(0 with hostname)` and new device rows have NULL hostname.
- **`LanDeviceMacChanged` log rare or never seen**: expected. The event only fires when a known IP responds from a different MAC — typically a DHCP reassignment between two devices, occasionally a potential ARP-spoof scenario. On a stable LAN with fixed DHCP leases it can be zero indefinitely.

## Related

- `docs/decisions/009-scanner-as-lan-device-discovery.md` — Phase 9 scoping ADR.
- `Beholder.Daemon.Windows/Scanner/IphlpapiInterop.cs` — the P/Invoke surface (unit-tested via the manual runbook only; mirrors ADR 004's `DnsApiInterop` pattern).
- `Beholder.Tests/LanScannerServiceTests.cs` — orchestration logic + state-transition rules (covered by xUnit against `FakeLanDeviceProbe`).
