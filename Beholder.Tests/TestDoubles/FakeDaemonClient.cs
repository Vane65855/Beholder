using System.Threading.Channels;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Grpc.Core;

namespace Beholder.Tests;

internal sealed class FakeDaemonClient : IDaemonClient {
    private readonly Channel<DaemonEvent> _channel = Channel.CreateUnbounded<DaemonEvent>();

    private ConnectionState _state = ConnectionState.Disconnected;

    public ConnectionState State => _state;
    public DaemonStatusInfo StatusInfo => DaemonStatusInfo.FromState(_state);
    public event Action<DaemonStatusInfo>? StateChanged;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public void SimulateConnected() {
        _state = ConnectionState.Connected;
        StateChanged?.Invoke(StatusInfo);
    }

    public void SimulateDisconnected() {
        _state = ConnectionState.Disconnected;
        StateChanged?.Invoke(StatusInfo);
    }

    public void PushEvent(DaemonEvent daemonEvent) =>
        _channel.Writer.TryWrite(daemonEvent);

    public void CompleteStream() =>
        _channel.Writer.Complete();

    public AsyncServerStreamingCall<DaemonEvent> Subscribe(CancellationToken ct) {
        var reader = new ChannelStreamReader<DaemonEvent>(_channel.Reader, ct);
        return new AsyncServerStreamingCall<DaemonEvent>(
            reader,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    public Task<GetSnapshotResponse> GetSnapshotAsync(CancellationToken ct) =>
        Task.FromResult(new GetSnapshotResponse());

    public Task<ApplyFirewallRuleResponse> ApplyFirewallRuleAsync(
        ApplyFirewallRuleRequest request, CancellationToken ct) =>
        Task.FromResult(new ApplyFirewallRuleResponse());

    public Task<MarkAlertReadResponse> MarkAlertReadAsync(
        MarkAlertReadRequest request, CancellationToken ct) =>
        Task.FromResult(new MarkAlertReadResponse());

    public Task<VerifyChainResponse> VerifyChainAsync(
        VerifyChainRequest request, CancellationToken ct) =>
        Task.FromResult(new VerifyChainResponse());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Adapts a <see cref="ChannelReader{T}"/> to <see cref="IAsyncStreamReader{T}"/>
/// for use with <see cref="AsyncServerStreamingCall{T}"/> in tests.
/// </summary>
internal sealed class ChannelStreamReader<T> : IAsyncStreamReader<T> {
    private readonly ChannelReader<T> _reader;
    private readonly CancellationToken _ct;
    private T _current = default!;

    public ChannelStreamReader(ChannelReader<T> reader, CancellationToken ct) {
        _reader = reader;
        _ct = ct;
    }

    public T Current => _current;

    public async Task<bool> MoveNext(CancellationToken cancellationToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ct);
        try {
            if (await _reader.WaitToReadAsync(linked.Token)) {
                if (_reader.TryRead(out var item)) {
                    _current = item;
                    return true;
                }
            }
            return false;
        } catch (ChannelClosedException) {
            return false;
        }
    }
}
