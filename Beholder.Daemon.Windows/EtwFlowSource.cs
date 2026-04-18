using System.Net;
using Beholder.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows;

/// <summary>
/// <see cref="IFlowSource"/> implementation that subscribes to the NT Kernel Logger's
/// network keyword via <see cref="KernelTraceEventParser"/> and emits one
/// <see cref="FlowEvent"/> per observed TCP/UDP send or receive (IPv4 and IPv6).
/// Requires Administrator privileges to create the underlying
/// <see cref="TraceEventSession"/>. Events are raised on the ETW processing thread —
/// consumers are responsible for marshalling to their own worker thread per
/// <see cref="IFlowSource.OnFlowEvent"/>.
/// </summary>
public sealed class EtwFlowSource : IFlowSource, IAsyncDisposable, IDisposable {
    // ETW Source.Process() drains on a background thread. 5 s is well above any
    // observed drain time in practice; long enough to flush buffered events,
    // short enough that a stuck session doesn't block daemon shutdown.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<EtwFlowSource> _logger;
    private readonly IProcessPathResolver _resolver;
    private readonly object _lifecycleLock = new();

    private TraceEventSession? _session;
    private Task? _processingTask;
    private bool _disposed;

    public EtwFlowSource(ILogger<EtwFlowSource> logger)
        : this(logger, new ProcessPathResolver()) { }

    internal EtwFlowSource(ILogger<EtwFlowSource> logger, IProcessPathResolver resolver) {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(resolver);
        _logger = logger;
        _resolver = resolver;
    }

    public event Action<FlowEvent>? OnFlowEvent;

    public Task StartAsync(CancellationToken cancellationToken) {
        lock (_lifecycleLock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) throw new InvalidOperationException("ETW session already started.");

            TraceEventSession session;
            try {
                // The NT Kernel Logger is a per-machine singleton; constructing a session
                // with this name stops any prior instance, which means a crashed daemon
                // doesn't leave an orphaned session behind.
                session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
            } catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "ETW session creation failed — ensure the daemon is running as Administrator");
                throw;
            }

            try {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                var kernel = session.Source.Kernel;
                kernel.TcpIpSend += OnTcpSend;
                kernel.TcpIpRecv += OnTcpRecv;
                kernel.TcpIpSendIPV6 += OnTcpSendV6;
                kernel.TcpIpRecvIPV6 += OnTcpRecvV6;
                kernel.UdpIpSend += OnUdpSend;
                kernel.UdpIpRecv += OnUdpRecv;
                kernel.UdpIpSendIPV6 += OnUdpSendV6;
                kernel.UdpIpRecvIPV6 += OnUdpRecvV6;
                _processingTask = Task.Run(() => session.Source.Process());
                _session = session;
            } catch (Exception ex) {
                // EnableKernelProvider is the path that actually throws
                // UnauthorizedAccessException when unelevated — kernel session
                // construction above can succeed without Administrator. Emit
                // the admin hint here so the user sees it in the stack trace
                // path they'll actually encounter.
                _logger.LogError(
                    ex,
                    "ETW kernel provider enable failed — ensure the daemon is running as Administrator");
                session.Dispose();
                throw;
            }

            _logger.LogInformation("ETW kernel network trace session started");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        TraceEventSession? sessionToStop;
        Task? taskToAwait;
        lock (_lifecycleLock) {
            sessionToStop = _session;
            taskToAwait = _processingTask;
            _session = null;
            _processingTask = null;
        }

        if (sessionToStop is null) return;

        var kernel = sessionToStop.Source.Kernel;
        kernel.TcpIpSend -= OnTcpSend;
        kernel.TcpIpRecv -= OnTcpRecv;
        kernel.TcpIpSendIPV6 -= OnTcpSendV6;
        kernel.TcpIpRecvIPV6 -= OnTcpRecvV6;
        kernel.UdpIpSend -= OnUdpSend;
        kernel.UdpIpRecv -= OnUdpRecv;
        kernel.UdpIpSendIPV6 -= OnUdpSendV6;
        kernel.UdpIpRecvIPV6 -= OnUdpRecvV6;
        sessionToStop.Stop();

        if (taskToAwait is not null) {
            var completed = await Task.WhenAny(
                taskToAwait,
                Task.Delay(StopTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != taskToAwait) {
                _logger.LogWarning(
                    "ETW processing task did not complete within 5 seconds of session stop");
            }
        }

        sessionToStop.Dispose();
        _logger.LogInformation("ETW kernel network trace session stopped");
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        // StopAsync is idempotent and safe when never started.
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // Fallback for code paths that still resolve IDisposable (e.g. `using`
    // in a sync context). Microsoft.Extensions.DependencyInjection always
    // prefers IAsyncDisposable when both are implemented, so in the daemon
    // this path is effectively dead. Intentionally does NOT block on
    // StopAsync — that would either reintroduce a banned sync-over-async
    // call or risk deadlock on a captured sync context. Prefer DisposeAsync.
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        TraceEventSession? session;
        lock (_lifecycleLock) {
            session = _session;
            _session = null;
            _processingTask = null;
        }
        session?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnTcpSend(TcpIpSendTraceData data) =>
        EmitFlowEvent(data, data.daddr, data.dport, bytesIn: 0, bytesOut: data.size);

    private void OnTcpRecv(TcpIpTraceData data) =>
        EmitFlowEvent(data, data.saddr, data.sport, bytesIn: data.size, bytesOut: 0);

    private void OnTcpSendV6(TcpIpV6SendTraceData data) =>
        EmitFlowEvent(data, data.daddr, data.dport, bytesIn: 0, bytesOut: data.size);

    private void OnTcpRecvV6(TcpIpV6TraceData data) =>
        EmitFlowEvent(data, data.saddr, data.sport, bytesIn: data.size, bytesOut: 0);

    private void OnUdpSend(UdpIpTraceData data) =>
        EmitFlowEvent(data, data.daddr, data.dport, bytesIn: 0, bytesOut: data.size);

    private void OnUdpRecv(UdpIpTraceData data) =>
        EmitFlowEvent(data, data.saddr, data.sport, bytesIn: data.size, bytesOut: 0);

    private void OnUdpSendV6(UpdIpV6TraceData data) =>
        EmitFlowEvent(data, data.daddr, data.dport, bytesIn: 0, bytesOut: data.size);

    private void OnUdpRecvV6(UpdIpV6TraceData data) =>
        EmitFlowEvent(data, data.saddr, data.sport, bytesIn: data.size, bytesOut: 0);

    private void EmitFlowEvent(
        TraceEvent ev,
        IPAddress remoteAddress,
        int remotePort,
        long bytesIn,
        long bytesOut
    ) {
        try {
            var pid = ev.ProcessID;
            if (pid <= 0) {
                _logger.LogDebug("Skipping ETW event with unresolved PID {Pid}", pid);
                return;
            }
            if (bytesIn == 0 && bytesOut == 0) return;

            var (name, path) = _resolver.Resolve(pid);
            var flowEvent = new FlowEvent(
                processId: pid,
                processName: name,
                processPath: path,
                remoteAddress: remoteAddress,
                remotePort: remotePort,
                bytesIn: bytesIn,
                bytesOut: bytesOut,
                country: CountryCode.Unknown,
                timestamp: new DateTimeOffset(ev.TimeStamp.ToUniversalTime(), TimeSpan.Zero));

            OnFlowEvent?.Invoke(flowEvent);
        } catch (Exception ex) {
            // Outer ETW boundary: exceptions propagating back into TraceEvent can tear
            // down the entire trace session and, on some Windows SKUs, leave it in a
            // zombie state that persists until reboot. Log and swallow.
            _logger.LogWarning(ex, "Failed to emit FlowEvent from {EventName}", ev.EventName);
        }
    }
}
