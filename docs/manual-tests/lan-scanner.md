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
   followed within ~2-3 seconds (or up to ~1.3 s after subnet discovery for a /24)
   by the first scan result:
   ```
   info: Beholder.Daemon.Scanner.LanScannerService[0]
         LAN scanner: N devices observed, M first-seen, 0 mac-changed
   ```
   On the first run M should equal N (every device is being seen for the first
   time). N typically ranges from 2 (gateway + this machine) to ~20 (home/SMB
   LAN with a few devices and IoT) to ~50+ (busy office network).

5. From a separate terminal (any privilege level), query the SQLite database:
   ```
   sqlite3 Beholder.Daemon\bin\Debug\net10.0-windows10.0.17763.0\data\beholder.db ^
     "SELECT mac, ip, vendor, hostname, last_seen_unix_ns FROM lan_device ORDER BY last_seen_unix_ns DESC LIMIT 20;"
   ```
   Expect rows for visible LAN devices — at minimum the default gateway, the
   local machine. Vendor column should be populated for devices whose OUI is
   in the IEEE registry; phones/laptops/IoT typically resolve to recognizable
   names (Apple, Inc. / Intel Corporate / Raspberry Pi Trading /
   TP-LINK TECHNOLOGIES). Hostname column will be NULL for all rows in 9.2
   (populated in Phase 9.2.5 once mDNS + NetBIOS sub-probes ship).

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
- **Scan duration scales with subnet size**: expected. ~1.3 s for /24, ~5 s for /22, ~20 s for /20 (the defensive ceiling). The 5-minute default interval means even a /20 spends < 7% of wall-clock on scanning.
- **`LanDeviceMacChanged` log rare or never seen**: expected. The event only fires when a known IP responds from a different MAC — typically a DHCP reassignment between two devices, occasionally a potential ARP-spoof scenario. On a stable LAN with fixed DHCP leases it can be zero indefinitely.

## Related

- `docs/decisions/009-scanner-as-lan-device-discovery.md` — Phase 9 scoping ADR.
- `Beholder.Daemon.Windows/Scanner/IphlpapiInterop.cs` — the P/Invoke surface (unit-tested via the manual runbook only; mirrors ADR 004's `DnsApiInterop` pattern).
- `Beholder.Tests/LanScannerServiceTests.cs` — orchestration logic + state-transition rules (covered by xUnit against `FakeLanDeviceProbe`).
