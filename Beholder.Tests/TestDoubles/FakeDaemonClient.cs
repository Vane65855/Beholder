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

    // Throw-control hooks. When non-null, the corresponding RPC throws the
    // exception on its next call. Per-RPC nullable to avoid a global throw
    // that breaks unrelated tests. Used by tests exercising OCE re-throw and
    // RpcException-swallow semantics in UI-layer catch blocks.
    public Exception? SnapshotException { get; set; }
    public Exception? ProcessTimelineException { get; set; }
    public Exception? AggregateTimelineException { get; set; }
    public Exception? ProcessSummariesException { get; set; }
    public Exception? ProcessDestinationsException { get; set; }
    public Exception? CountryBreakdownException { get; set; }
    public Exception? ProtocolBreakdownException { get; set; }
    public Exception? ApplyFirewallRuleException { get; set; }
    public Exception? RemoveFirewallRuleException { get; set; }
    public Exception? ListFirewallRulesException { get; set; }
    public Exception? SetFirewallEnabledException { get; set; }
    public Exception? FirewallActivityException { get; set; }
    public Exception? MarkAlertReadException { get; set; }
    public Exception? ListLanDevicesException { get; set; }
    public Exception? TriggerScanException { get; set; }
    public Exception? SetLanDeviceLabelException { get; set; }

    // Optional canned snapshot/response bodies so tests can drive real data
    // through the seeding path. Existing callers that don't set these get the
    // empty-response default as before.
    public GetSnapshotResponse? SnapshotResponse { get; set; }

    /// <summary>
    /// Optional async responder for <see cref="GetSnapshotAsync"/>. Lets tests
    /// inject a deferred Task (e.g., a TaskCompletionSource the test signals
    /// when ready) so they can exercise the cold-start race between two
    /// concurrent <c>ActivateAsync</c> callers. When set, takes precedence
    /// over <see cref="SnapshotResponse"/>; when null, the synchronous
    /// path applies.
    /// </summary>
    public Func<CancellationToken, Task<GetSnapshotResponse>>? SnapshotResponder { get; set; }
    public Func<GetProcessTimelineRequest, GetProcessTimelineResponse>? ProcessTimelineResponder { get; set; }
    public GetProcessSummariesResponse? ProcessSummariesResponse { get; set; }
    public GetAggregateTimelineResponse? AggregateTimelineResponse { get; set; }
    public Func<GetProcessDestinationsRequest, GetProcessDestinationsResponse>? ProcessDestinationsResponder { get; set; }
    public Func<GetCountryBreakdownRequest, GetCountryBreakdownResponse>? CountryBreakdownResponder { get; set; }
    public Func<GetProtocolBreakdownRequest, GetProtocolBreakdownResponse>? ProtocolBreakdownResponder { get; set; }
    public Func<ApplyFirewallRuleRequest, ApplyFirewallRuleResponse>? ApplyFirewallRuleResponder { get; set; }
    public Func<RemoveFirewallRuleRequest, RemoveFirewallRuleResponse>? RemoveFirewallRuleResponder { get; set; }
    public ListFirewallRulesResponse? ListFirewallRulesResponse { get; set; }
    public Func<SetFirewallEnabledRequest, SetFirewallEnabledResponse>? SetFirewallEnabledResponder { get; set; }
    public Func<GetFirewallActivityRequest, GetFirewallActivityResponse>? FirewallActivityResponder { get; set; }
    public GetFirewallActivityResponse? FirewallActivityResponse { get; set; }
    public Func<ListLanDevicesRequest, ListLanDevicesResponse>? ListLanDevicesResponder { get; set; }
    public ListLanDevicesResponse? ListLanDevicesResponse { get; set; }
    /// <summary>
    /// Async responder for <see cref="ListLanDevicesAsync"/>. Lets tests gate
    /// the response on a <see cref="TaskCompletionSource{T}"/> they signal
    /// later, exercising the cold-start race in
    /// <see cref="Beholder.Ui.ViewModels.ScannerTabViewModel.ActivateAsync"/>.
    /// Takes precedence over the synchronous responder when both are set;
    /// mirrors the <see cref="SnapshotResponder"/> precedent.
    /// </summary>
    public Func<ListLanDevicesRequest, CancellationToken, Task<ListLanDevicesResponse>>? AsyncListLanDevicesResponder { get; set; }
    public Func<TriggerScanRequest, TriggerScanResponse>? TriggerScanResponder { get; set; }
    public Func<SetLanDeviceLabelRequest, SetLanDeviceLabelResponse>? SetLanDeviceLabelResponder { get; set; }

    // Captured invocations for assertions.
    public List<ApplyFirewallRuleRequest> ApplyFirewallRuleCalls { get; } = new();
    public List<RemoveFirewallRuleRequest> RemoveFirewallRuleCalls { get; } = new();
    public List<SetFirewallEnabledRequest> SetFirewallEnabledCalls { get; } = new();
    public List<MarkAlertReadRequest> MarkAlertReadCalls { get; } = new();
    public List<ListLanDevicesRequest> ListLanDevicesCalls { get; } = new();
    public List<TriggerScanRequest> TriggerScanCalls { get; } = new();
    public List<SetLanDeviceLabelRequest> SetLanDeviceLabelCalls { get; } = new();
    // Responder variant for tests that need the CancellationToken the VM
    // passed (e.g., cancellation-plumbing tests). Takes precedence over
    // AggregateTimelineResponse when set.
    public Func<GetAggregateTimelineRequest, CancellationToken, GetAggregateTimelineResponse>? AggregateTimelineResponder { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

    public AsyncServerStreamingCall<DaemonEvent> Subscribe(CancellationToken cancellationToken) {
        var reader = new ChannelStreamReader<DaemonEvent>(_channel.Reader, cancellationToken);
        return new AsyncServerStreamingCall<DaemonEvent>(
            reader,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    public Task<GetSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) {
        if (SnapshotException is not null) throw SnapshotException;
        if (SnapshotResponder is not null) return SnapshotResponder(cancellationToken);
        return Task.FromResult(SnapshotResponse ?? new GetSnapshotResponse());
    }

    public Task<ApplyFirewallRuleResponse> ApplyFirewallRuleAsync(
        ApplyFirewallRuleRequest request, CancellationToken cancellationToken) {
        ApplyFirewallRuleCalls.Add(request);
        if (ApplyFirewallRuleException is not null) throw ApplyFirewallRuleException;
        return Task.FromResult(ApplyFirewallRuleResponder?.Invoke(request) ?? new ApplyFirewallRuleResponse());
    }

    public Task<RemoveFirewallRuleResponse> RemoveFirewallRuleAsync(
        RemoveFirewallRuleRequest request, CancellationToken cancellationToken) {
        RemoveFirewallRuleCalls.Add(request);
        if (RemoveFirewallRuleException is not null) throw RemoveFirewallRuleException;
        return Task.FromResult(RemoveFirewallRuleResponder?.Invoke(request) ?? new RemoveFirewallRuleResponse());
    }

    public Task<ListFirewallRulesResponse> ListFirewallRulesAsync(
        ListFirewallRulesRequest request, CancellationToken cancellationToken) {
        if (ListFirewallRulesException is not null) throw ListFirewallRulesException;
        return Task.FromResult(ListFirewallRulesResponse ?? new ListFirewallRulesResponse());
    }

    public Task<SetFirewallEnabledResponse> SetFirewallEnabledAsync(
        SetFirewallEnabledRequest request, CancellationToken cancellationToken) {
        SetFirewallEnabledCalls.Add(request);
        if (SetFirewallEnabledException is not null) throw SetFirewallEnabledException;
        return Task.FromResult(SetFirewallEnabledResponder?.Invoke(request)
            ?? new SetFirewallEnabledResponse { Enabled = request.Enabled });
    }

    public Task<MarkAlertReadResponse> MarkAlertReadAsync(
        MarkAlertReadRequest request, CancellationToken cancellationToken) {
        MarkAlertReadCalls.Add(request);
        if (MarkAlertReadException is not null) throw MarkAlertReadException;
        return Task.FromResult(new MarkAlertReadResponse());
    }

    public Task<VerifyChainResponse> VerifyChainAsync(
        VerifyChainRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new VerifyChainResponse());

    public Task<GetProcessTimelineResponse> GetProcessTimelineAsync(
        GetProcessTimelineRequest request, CancellationToken cancellationToken) {
        if (ProcessTimelineException is not null) throw ProcessTimelineException;
        return Task.FromResult(ProcessTimelineResponder?.Invoke(request) ?? new GetProcessTimelineResponse());
    }

    public Task<GetAggregateTimelineResponse> GetAggregateTimelineAsync(
        GetAggregateTimelineRequest request, CancellationToken cancellationToken) {
        if (AggregateTimelineException is not null) throw AggregateTimelineException;
        if (AggregateTimelineResponder is not null)
            return Task.FromResult(AggregateTimelineResponder(request, cancellationToken));
        return Task.FromResult(AggregateTimelineResponse ?? new GetAggregateTimelineResponse());
    }

    public Task<GetProcessDestinationsResponse> GetProcessDestinationsAsync(
        GetProcessDestinationsRequest request, CancellationToken cancellationToken) {
        if (ProcessDestinationsException is not null) throw ProcessDestinationsException;
        return Task.FromResult(ProcessDestinationsResponder?.Invoke(request) ?? new GetProcessDestinationsResponse());
    }

    public Task<GetCountryBreakdownResponse> GetCountryBreakdownAsync(
        GetCountryBreakdownRequest request, CancellationToken cancellationToken) {
        if (CountryBreakdownException is not null) throw CountryBreakdownException;
        return Task.FromResult(CountryBreakdownResponder?.Invoke(request) ?? new GetCountryBreakdownResponse());
    }

    public Task<GetProtocolBreakdownResponse> GetProtocolBreakdownAsync(
        GetProtocolBreakdownRequest request, CancellationToken cancellationToken) {
        if (ProtocolBreakdownException is not null) throw ProtocolBreakdownException;
        return Task.FromResult(ProtocolBreakdownResponder?.Invoke(request) ?? new GetProtocolBreakdownResponse());
    }

    public Task<GetProcessSummariesResponse> GetProcessSummariesAsync(
        GetProcessSummariesRequest request, CancellationToken cancellationToken) {
        if (ProcessSummariesException is not null) throw ProcessSummariesException;
        return Task.FromResult(ProcessSummariesResponse ?? new GetProcessSummariesResponse());
    }

    public Task<GetFirewallActivityResponse> GetFirewallActivityAsync(
        GetFirewallActivityRequest request, CancellationToken cancellationToken) {
        if (FirewallActivityException is not null) throw FirewallActivityException;
        return Task.FromResult(FirewallActivityResponder?.Invoke(request)
            ?? FirewallActivityResponse ?? new GetFirewallActivityResponse());
    }

    public Task<ListLanDevicesResponse> ListLanDevicesAsync(
        ListLanDevicesRequest request, CancellationToken cancellationToken) {
        ListLanDevicesCalls.Add(request);
        if (ListLanDevicesException is not null) throw ListLanDevicesException;
        if (AsyncListLanDevicesResponder is not null)
            return AsyncListLanDevicesResponder(request, cancellationToken);
        return Task.FromResult(ListLanDevicesResponder?.Invoke(request)
            ?? ListLanDevicesResponse ?? new ListLanDevicesResponse());
    }

    public Task<TriggerScanResponse> TriggerScanAsync(
        TriggerScanRequest request, CancellationToken cancellationToken) {
        TriggerScanCalls.Add(request);
        if (TriggerScanException is not null) throw TriggerScanException;
        return Task.FromResult(TriggerScanResponder?.Invoke(request) ?? new TriggerScanResponse());
    }

    public Task<SetLanDeviceLabelResponse> SetLanDeviceLabelAsync(
        SetLanDeviceLabelRequest request, CancellationToken cancellationToken) {
        SetLanDeviceLabelCalls.Add(request);
        if (SetLanDeviceLabelException is not null) throw SetLanDeviceLabelException;
        return Task.FromResult(SetLanDeviceLabelResponder?.Invoke(request)
            ?? new SetLanDeviceLabelResponse { Success = true });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Adapts a <see cref="ChannelReader{T}"/> to <see cref="IAsyncStreamReader{T}"/>
/// for use with <see cref="AsyncServerStreamingCall{T}"/> in tests.
/// </summary>
internal sealed class ChannelStreamReader<T> : IAsyncStreamReader<T> {
    private readonly ChannelReader<T> _reader;
    private readonly CancellationToken _cancellationToken;
    private T _current = default!;

    public ChannelStreamReader(ChannelReader<T> reader, CancellationToken cancellationToken) {
        _reader = reader;
        _cancellationToken = cancellationToken;
    }

    public T Current => _current;

    public async Task<bool> MoveNext(CancellationToken cancellationToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken);
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
