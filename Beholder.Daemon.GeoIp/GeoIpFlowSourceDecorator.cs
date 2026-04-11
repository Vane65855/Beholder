using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.GeoIp;

/// <summary>
/// Wraps an <see cref="IFlowSource"/> and rewrites each observed event's
/// <see cref="FlowEvent.Country"/> using an <see cref="IGeoIpResolver"/>
/// before re-forwarding the event to its own subscribers. The wrapped source
/// is owned by DI; the decorator only owns the event subscription.
/// </summary>
public sealed class GeoIpFlowSourceDecorator : IFlowSource, IAsyncDisposable, IDisposable {
    private readonly IFlowSource _inner;
    private readonly IGeoIpResolver _resolver;
    private readonly ILogger<GeoIpFlowSourceDecorator> _logger;
    private readonly object _subscriptionLock = new();
    private bool _subscribed;
    private bool _disposed;

    public GeoIpFlowSourceDecorator(
        IFlowSource inner,
        IGeoIpResolver resolver,
        ILogger<GeoIpFlowSourceDecorator> logger
    ) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _resolver = resolver;
        _logger = logger;
    }

    public event Action<FlowEvent>? OnFlowEvent;

    public Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_subscriptionLock) {
            if (!_subscribed) {
                _inner.OnFlowEvent += OnInnerFlowEvent;
                _subscribed = true;
            }
        }
        return _inner.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        lock (_subscriptionLock) {
            if (_subscribed) {
                _inner.OnFlowEvent -= OnInnerFlowEvent;
                _subscribed = false;
            }
        }
        return _inner.StopAsync(cancellationToken);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        lock (_subscriptionLock) {
            if (_subscribed) {
                _inner.OnFlowEvent -= OnInnerFlowEvent;
                _subscribed = false;
            }
        }
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnInnerFlowEvent(FlowEvent flowEvent) {
        CountryCode country;
        try {
            country = _resolver.Resolve(flowEvent.RemoteAddress);
        } catch (Exception ex) {
            // Outer-boundary catch: a resolver failure must not break the flow
            // pipeline. Forward the original (unenriched) event so downstream
            // aggregation still happens.
            _logger.LogWarning(ex,
                "GeoIP enrichment failed for {Address}, forwarding unchanged",
                flowEvent.RemoteAddress);
            OnFlowEvent?.Invoke(flowEvent);
            return;
        }

        var enriched = new FlowEvent(
            processId: flowEvent.ProcessId,
            processName: flowEvent.ProcessName,
            processPath: flowEvent.ProcessPath,
            remoteAddress: flowEvent.RemoteAddress,
            remotePort: flowEvent.RemotePort,
            bytesIn: flowEvent.BytesIn,
            bytesOut: flowEvent.BytesOut,
            country: country,
            timestamp: flowEvent.Timestamp);
        OnFlowEvent?.Invoke(enriched);
    }
}
