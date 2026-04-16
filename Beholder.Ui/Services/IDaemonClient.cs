using System;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;

namespace Beholder.Ui.Services;

internal enum ConnectionState {
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

internal record DaemonStatusInfo(ConnectionState State, string Label) {
    public static DaemonStatusInfo FromState(ConnectionState state) => state switch {
        ConnectionState.Disconnected => new(state, "offline"),
        ConnectionState.Connecting => new(state, "connecting\u2026"),
        ConnectionState.Connected => new(state, "online"),
        ConnectionState.Reconnecting => new(state, "reconnecting\u2026"),
        _ => new(ConnectionState.Disconnected, "offline"),
    };
}

internal interface IDaemonClient : IAsyncDisposable {
    ConnectionState State { get; }
    DaemonStatusInfo StatusInfo { get; }
    event Action<DaemonStatusInfo>? StateChanged;

    Task ConnectAsync(CancellationToken ct);

    Task<GetSnapshotResponse> GetSnapshotAsync(CancellationToken ct);
    Task<ApplyFirewallRuleResponse> ApplyFirewallRuleAsync(ApplyFirewallRuleRequest request, CancellationToken ct);
    Task<MarkAlertReadResponse> MarkAlertReadAsync(MarkAlertReadRequest request, CancellationToken ct);
    Task<VerifyChainResponse> VerifyChainAsync(VerifyChainRequest request, CancellationToken ct);
    Task<GetProcessTimelineResponse> GetProcessTimelineAsync(GetProcessTimelineRequest request, CancellationToken ct);
    Task<GetAggregateTimelineResponse> GetAggregateTimelineAsync(GetAggregateTimelineRequest request, CancellationToken ct);
    Task<GetProcessDestinationsResponse> GetProcessDestinationsAsync(GetProcessDestinationsRequest request, CancellationToken ct);
    Task<GetCountryBreakdownResponse> GetCountryBreakdownAsync(GetCountryBreakdownRequest request, CancellationToken ct);
    Task<GetProcessSummariesResponse> GetProcessSummariesAsync(GetProcessSummariesRequest request, CancellationToken ct);
    Grpc.Core.AsyncServerStreamingCall<DaemonEvent> Subscribe(CancellationToken ct);
}
