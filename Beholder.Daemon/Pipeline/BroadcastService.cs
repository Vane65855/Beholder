using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Beholder.Core;
using Beholder.Protocol;
using Local = Beholder.Protocol.Local;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Fans the single <see cref="ISnapshotBatchSource.OnSnapshotBatch"/> event
/// out into a per-subscriber bounded channel. Each subscription gets its own
/// channel so slow consumers drop their own old events instead of blocking the
/// publisher or starving other subscribers.
/// </summary>
internal sealed class BroadcastService : IHostedService, IDisposable {
    private const int DefaultSubscriberChannelCapacity = 100;

    private readonly ISnapshotBatchSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BroadcastService> _logger;
    private readonly int _subscriberChannelCapacity;
    private readonly ConcurrentDictionary<Guid, Channel<Local.DaemonEvent>> _subscribers = new();
    private bool _subscribed;
    private bool _disposed;

    public BroadcastService(
        ISnapshotBatchSource source,
        TimeProvider timeProvider,
        ILogger<BroadcastService> logger
    ) : this(source, timeProvider, logger, DefaultSubscriberChannelCapacity) { }

    // Test-only overload; internal visibility matches the class.
    internal BroadcastService(
        ISnapshotBatchSource source,
        TimeProvider timeProvider,
        ILogger<BroadcastService> logger,
        int subscriberChannelCapacity
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberChannelCapacity);

        _source = source;
        _timeProvider = timeProvider;
        _logger = logger;
        _subscriberChannelCapacity = subscriberChannelCapacity;
    }

    /// <summary>Diagnostic. Test-only — lets tests spin-wait for registration.</summary>
    internal int ActiveSubscriberCount => _subscribers.Count;

    public Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source.OnSnapshotBatch += OnSnapshotBatch;
        _subscribed = true;
        _logger.LogInformation("Broadcast service started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        if (_subscribed) {
            _source.OnSnapshotBatch -= OnSnapshotBatch;
            _subscribed = false;
        }
        foreach (var (_, channel) in _subscribers) {
            channel.Writer.TryComplete();
        }
        _subscribers.Clear();
        _logger.LogInformation("Broadcast service stopped");
        return Task.CompletedTask;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed) {
            _source.OnSnapshotBatch -= OnSnapshotBatch;
            _subscribed = false;
        }
        foreach (var (_, channel) in _subscribers) {
            channel.Writer.TryComplete();
        }
        _subscribers.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Returns an async stream of <see cref="Local.DaemonEvent"/> for one
    /// subscriber. The subscriber's channel is created and registered on first
    /// <c>MoveNextAsync</c> and removed when the caller stops enumerating
    /// (cancellation or normal completion).
    /// </summary>
    public async IAsyncEnumerable<Local.DaemonEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    ) {
        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateBounded<Local.DaemonEvent>(
            new BoundedChannelOptions(_subscriberChannelCapacity) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
        _subscribers.TryAdd(subscriberId, channel);
        _logger.LogInformation("Broadcast subscriber {Id} added", subscriberId);
        try {
            await foreach (var daemonEvent in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false)) {
                yield return daemonEvent;
            }
        } finally {
            _subscribers.TryRemove(subscriberId, out _);
            channel.Writer.TryComplete();
            _logger.LogInformation("Broadcast subscriber {Id} removed", subscriberId);
        }
    }

    private void OnSnapshotBatch(IReadOnlyList<CounterSnapshot> snapshots) {
        var batch = new Local.CounterBatch {
            TickTimestampUnixNs = _timeProvider.GetUtcNow().ToUnixTimeNanoseconds(),
        };
        foreach (var snapshot in snapshots) {
            batch.Snapshots.Add(snapshot.ToProto());
        }
        var daemonEvent = new Local.DaemonEvent { CounterBatch = batch };
        foreach (var (_, channel) in _subscribers) {
            // DropOldest means TryWrite always succeeds on an open writer; the only
            // false return is a completed writer (subscriber disconnected between
            // the snapshot of the dictionary and this call — benign, just skip).
            channel.Writer.TryWrite(daemonEvent);
        }
    }
}
