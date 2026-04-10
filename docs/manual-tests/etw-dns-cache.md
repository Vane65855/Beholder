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
   Expect three `info:` startup log lines (order may vary slightly):
   ```
   info: Beholder.Daemon.Windows.EtwDnsCache[0]
         DNS ETW trace session started
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session started
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Flow event pipeline started
   ```
   If you see `DNS ETW session creation failed — ensure the daemon is running as Administrator`, the terminal is not elevated. Go back to step 2.

5. From **another terminal** (any privilege level), visit a domain you have not visited recently:
   ```
   curl https://example.org
   ```
   `example.org` is chosen deliberately over `example.com` because `.com` is likely already cached from the flow-source runbook or normal browsing. You can substitute any domain the test machine has not hit in the last few minutes.

6. Watch the daemon's terminal. Within a second or two, aggregated `Counter` lines should appear for any process that generated traffic:
   ```
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Counter curl.exe Δin=1472 Δout=517 total_in=1472 total_out=517 conns=1
   ```
   **Phase 2.4 note**: the daemon no longer logs per-flow lines with hostnames. `EtwDnsCache` still populates the in-memory IP→hostname map whenever the DNS Client ETW provider fires, but nothing currently consumes it — the UI (Phase 6+) will surface hostnames per connection. For now, this runbook verifies only that the DNS trace session starts and stops cleanly alongside the rest of the pipeline.

7. Stop the daemon with Ctrl+C. Expect the pipeline and both trace sessions to stop cleanly:
   ```
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Flow event pipeline stopped
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session stopped
   info: Beholder.Daemon.Windows.EtwDnsCache[0]
         DNS ETW trace session stopped
   ```

8. Verify clean restart: immediately restart the daemon from the same elevated terminal:
   ```
   dotnet run --project Beholder.Daemon
   ```
   All three "started" lines should reappear with no errors. `EtwDnsCache` sweeps any orphaned `Beholder-DnsTrace` session on startup, so a prior crash does not require a reboot or a manual `logman stop`.

## What success looks like

- Both `DNS ETW trace session started` and `ETW kernel network trace session started` log lines appear on startup, followed by `Flow event pipeline started`.
- All three stop lines appear cleanly on Ctrl+C.
- Restarting the daemon is silent — no "session already exists" errors on `Beholder-DnsTrace` or the NT Kernel Logger.
- **Hostname verification is not possible from log output in Phase 2.4.** `EtwDnsCache` is populated but unconsumed by the logging pipeline. End-to-end hostname verification is deferred to the Phase 6 UI, which will read `IDnsCache.Resolve` per connection.

## Known non-issues

- **No hostnames in the Counter log lines**: expected. `CounterSnapshot` is a per-process aggregate across many flow events that may have touched different remote endpoints; there is no single hostname to attach. Hostnames are a per-connection concern that the UI will display, not a per-process summary concern.
- **`ipconfig /flushdns` is still required for a fresh population run**: expected. The OS resolver caches DNS answers for the record's TTL; cached answers do not re-fire the DNS Client ETW provider. Flushing forces the next resolution to go through the DNS client and emit the ETW event that populates the cache.

## Related

- `etw-flow-source.md` — the runbook for `EtwFlowSource` (the flow-line generator that `EtwDnsCache` enriches).
