# Manual Test: EtwFlowSource

`EtwFlowSource` cannot be tested with xUnit — ETW requires a live Windows kernel,
Administrator privileges, and network traffic. This runbook is the end-to-end
verification.

## Requirements

- Windows 10/11 or Server 2019+
- .NET 10 SDK
- Administrator terminal (Run as Administrator — NOT a non-elevated terminal)
- A running network interface

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

3. Start the daemon from the elevated terminal:
   ```
   dotnet run --project Beholder.Daemon
   ```
   Expect `info:` log lines showing both ETW sessions and the pipeline starting:
   ```
   info: Beholder.Daemon.Windows.EtwDnsCache[0]
         DNS ETW trace session started
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session started
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Flow event pipeline started
   ```
   If you see `ETW session creation failed — ensure the daemon is running as Administrator`, the terminal is not elevated. Go back to step 2.

4. From **another terminal** (any privilege level), generate some outbound traffic:
   ```
   curl https://example.com
   ```
   or just open a browser.

5. Watch the daemon's terminal. Within a second or two you should see aggregated `Counter` lines, one per active process per second:
   ```
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Counter curl.exe Δin=1472 Δout=517 total_in=1472 total_out=517 conns=1
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Counter firefox.exe Δin=12453 Δout=891 total_in=847291 total_out=42891 conns=3
   ```
   `Δin` / `Δout` are the bytes observed during the last 1-second tick; `total_in` / `total_out` are the running totals since the daemon started; `conns` is the number of distinct `(remote IP, remote port)` endpoints the process touched during the tick.

6. Stop the daemon with Ctrl+C. Expect the pipeline and both ETW sessions to stop cleanly:
   ```
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Flow event pipeline stopping
   info: Beholder.Daemon.Pipeline.FlowEventPipeline[0]
         Flow event pipeline stopped
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session stopped
   info: Beholder.Daemon.Windows.EtwDnsCache[0]
         DNS ETW trace session stopped
   ```

7. Verify clean shutdown: immediately restart the daemon from the same elevated terminal:
   ```
   dotnet run --project Beholder.Daemon
   ```
   The "ETW kernel network trace session started" line should reappear with no "session already exists" warning. If you see an error about an existing session, the previous run did not shut down cleanly — file a bug and include the daemon log.

   Note: this implementation uses the **NT Kernel Logger** session, which is a per-machine singleton. If another tool (Windows Performance Recorder, xperf, perfview) is already using it, daemon startup will fail. Stop the conflicting tool and retry.

## What success looks like

- `Counter` lines appear within ~2 seconds of generating traffic.
- You see **one line per active process per second**, not dozens per second — roughly `N` lines per tick where `N` is the number of processes that moved bytes in that second.
- Each `Counter` line has a real process name, nonzero Δ or total bytes, and a connection count.
- A process with no activity in a given second produces no line that second — the TrafficEngine omits inactive processes from the batch by design.
- Country is not shown in the Counter log template. GeoIP still runs in the TrafficEngine (per-country bytes-out is carried inside `CounterSnapshot.BytesOutByCountry`), but surfacing it is a UI-phase concern.
- Shutdown is silent (no stack traces), and restart works with no orphaned-session errors.

## Known non-issues

- **Counter lines for "System" or other kernel-attributed processes**: expected. Windows emits kernel network events for SMB, DNS resolver service, Windows Update, and other system components. These are real traffic, not a bug.
- **IPv6 connection counts for loopback-heavy processes**: expected. Windows prefers IPv6 for localhost on modern builds, so `conns=` reflects IPv6 endpoints.
- **No Counter lines from loopback pings**: expected. ICMP is not a TCP/UDP protocol and the kernel network ETW provider does not emit loopback ICMP through the `TcpIp`/`UdpIp` kernel events that `EtwFlowSource` subscribes to.
- **The `Δin` / `Δout` template uses the Greek delta character**: expected. Structured log templates tolerate Unicode literally. If your terminal font lacks coverage you may see a fallback glyph; this is cosmetic.
