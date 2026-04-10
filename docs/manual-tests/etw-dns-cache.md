# Manual Test: EtwDnsCache

`EtwDnsCache` cannot be tested with xUnit — the DNS Client ETW provider requires
a live Windows kernel, Administrator privileges, and real DNS traffic from some
process on the machine. This runbook is the end-to-end verification.

## Requirements

- Windows 10/11 or Server 2019+
- .NET 10 SDK
- Administrator terminal (Run as Administrator — NOT a non-elevated terminal)
- A running network interface
- Outbound DNS resolution working on the machine

## Steps

1. Build the solution from a normal terminal:
   ```
   dotnet build
   ```
   Expect zero errors, zero warnings.

2. Open an **elevated** terminal (right-click the terminal app → "Run as administrator"). Verify elevation:
   ```
   whoami /groups | findstr "S-1-16-12288"
   ```
   A non-empty match means you are running as High Integrity (admin). If the match is empty, ETW session creation will fail.

3. From the elevated terminal, flush the OS DNS resolver cache so fresh lookups actually fire the ETW provider:
   ```
   ipconfig /flushdns
   ```
   Without this step, cached OS-resolver answers will short-circuit the DNS client before it emits an event, and the cache will stay empty for already-visited domains.

4. Start the daemon from the elevated terminal:
   ```
   dotnet run --project Beholder.Daemon
   ```
   Expect two `info:` log lines (order may vary slightly):
   ```
   info: Beholder.Daemon.Windows.EtwDnsCache[0]
         DNS ETW trace session started
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session started
   ```
   If you see `DNS ETW session creation failed — ensure the daemon is running as Administrator`, the terminal is not elevated. Go back to step 2.

5. From **another terminal** (any privilege level), visit a domain you have not visited recently:
   ```
   curl https://example.org
   ```
   `example.org` is chosen deliberately over `example.com` because `.com` is likely already cached from the flow-source runbook or normal browsing. You can substitute any domain the test machine has not hit in the last few minutes.

6. Watch the daemon's terminal. Within a second or two, flow lines should include the hostname:
   ```
   info: Beholder.Daemon.Worker[0]
         Flow curl (12345) example.org (93.184.216.34):443 in=0 out=517
   info: Beholder.Daemon.Worker[0]
         Flow curl (12345) example.org (93.184.216.34):443 in=1472 out=0
   ```
   Cache misses still show the raw IP in the old format:
   ```
   info: Beholder.Daemon.Worker[0]
         Flow SearchApp (8824) 23.55.245.124:443 in=0 out=517
   ```

7. Stop the daemon with Ctrl+C. Expect both stop lines:
   ```
   info: Beholder.Daemon.Windows.EtwDnsCache[0]
         DNS ETW trace session stopped
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session stopped
   ```

8. Verify clean restart: immediately restart the daemon from the same elevated terminal:
   ```
   dotnet run --project Beholder.Daemon
   ```
   Both "session started" lines should reappear with no errors. `EtwDnsCache` sweeps any orphaned `Beholder-DnsTrace` session on startup, so a prior crash does not require a reboot or a manual `logman stop`.

## What success looks like

- At least some `Flow` lines include a hostname in the `{Hostname} ({Remote}):{Port}` format.
- The hostname matches the domain the user actually typed (not a generic CDN reverse-DNS name).
- Restarting the daemon is silent — no "session already exists" error on `Beholder-DnsTrace`.

## Known non-issues

- **Many flow lines still show raw IPs**: expected. Background services (Windows Update, telemetry, Edge WebView, etc.) cache DNS results across daemon restarts. Their flows are real, but the DNS event fired before the daemon was running so the IP→hostname mapping was never observed.
- **`ipconfig /flushdns` is required for fresh visibility**: expected. The OS resolver caches DNS answers for the record's TTL; cached answers do not re-fire the DNS Client ETW provider. If you want to see hostnames for a domain you already visited, flush the cache first.
- **A hostname sometimes flips between two names for the same CDN IP**: expected and correct. Multiple domains can legitimately resolve to the same CDN edge IP; the cache uses last-write-wins, so whichever query happened most recently is what you see on the next flow line.

## Related

- `etw-flow-source.md` — the runbook for `EtwFlowSource` (the flow-line generator that `EtwDnsCache` enriches).
