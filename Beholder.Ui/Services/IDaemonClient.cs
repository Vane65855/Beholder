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

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<GetSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<ApplyFirewallRuleResponse> ApplyFirewallRuleAsync(ApplyFirewallRuleRequest request, CancellationToken cancellationToken);
    Task<MarkAlertReadResponse> MarkAlertReadAsync(MarkAlertReadRequest request, CancellationToken cancellationToken);
    Task<VerifyChainResponse> VerifyChainAsync(VerifyChainRequest request, CancellationToken cancellationToken);
    Task<GetProcessTimelineResponse> GetProcessTimelineAsync(GetProcessTimelineRequest request, CancellationToken cancellationToken);
    Task<GetAggregateTimelineResponse> GetAggregateTimelineAsync(GetAggregateTimelineRequest request, CancellationToken cancellationToken);
    Task<GetProcessDestinationsResponse> GetProcessDestinationsAsync(GetProcessDestinationsRequest request, CancellationToken cancellationToken);
    Task<GetCountryBreakdownResponse> GetCountryBreakdownAsync(GetCountryBreakdownRequest request, CancellationToken cancellationToken);
    Task<GetProtocolBreakdownResponse> GetProtocolBreakdownAsync(GetProtocolBreakdownRequest request, CancellationToken cancellationToken);
    Task<GetProcessSummariesResponse> GetProcessSummariesAsync(GetProcessSummariesRequest request, CancellationToken cancellationToken);
    Grpc.Core.AsyncServerStreamingCall<DaemonEvent> Subscribe(CancellationToken cancellationToken);
}
