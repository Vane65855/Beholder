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
   Expect an `info:` log line:
   ```
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session started
   ```
   If you see `ETW session creation failed — ensure the daemon is running as Administrator`, the terminal is not elevated. Go back to step 2.

4. From **another terminal** (any privilege level), generate some outbound traffic:
   ```
   curl https://example.com
   ```
   or just open a browser.

5. Watch the daemon's terminal. Within a second or two you should see `info:` log lines like:
   ```
   info: Beholder.Daemon.Worker[0]
         Flow curl (12345) 93.184.216.34:443 in=0 out=517
   info: Beholder.Daemon.Worker[0]
         Flow curl (12345) 93.184.216.34:443 in=1472 out=0
   ```
   One line per TCP/UDP send or receive, split by direction. Bytes appear in exactly one of `in` / `out` per event.

6. Stop the daemon with Ctrl+C. Expect:
   ```
   info: Beholder.Daemon.Windows.EtwFlowSource[0]
         ETW kernel network trace session stopped
   ```

7. Verify clean shutdown: immediately restart the daemon from the same elevated terminal:
   ```
   dotnet run --project Beholder.Daemon
   ```
   The "ETW kernel network trace session started" line should reappear with no "session already exists" warning. If you see an error about an existing session, the previous run did not shut down cleanly — file a bug and include the daemon log.

   Note: this implementation uses the **NT Kernel Logger** session, which is a per-machine singleton. If another tool (Windows Performance Recorder, xperf, perfview) is already using it, daemon startup will fail. Stop the conflicting tool and retry.

## What success looks like

- FlowEvent log lines appear within ~2 seconds of generating traffic.
- Each line has a real process name (not "unknown"), a valid remote IP, a port, and a nonzero byte count in exactly one direction.
- Country in logs is not checked — GeoIP happens later in the pipeline. `EtwFlowSource` always emits `CountryCode.Unknown`.
- Shutdown is silent (no stack traces), and restart works with no orphaned-session errors.

## Known non-issues

- **Many events with process name "System" or "unknown"**: expected. Windows emits kernel network events for SMB, DNS resolver service, Windows Update, and other system components. These are real traffic, not a bug.
- **IPv6 addresses appearing for local traffic**: expected. Windows prefers IPv6 for localhost on modern builds.
- **No events from loopback pings**: expected. ICMP is not a TCP/UDP protocol and the kernel network ETW provider does not emit loopback ICMP through the `TcpIp`/`UdpIp` kernel events that `EtwFlowSource` subscribes to.
