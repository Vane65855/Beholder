using Beholder.Core;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Protocol;
using Grpc.Core;
using Local = Beholder.Protocol.Local;

namespace Beholder.Daemon.Grpc;

/// <summary>
/// Server-side implementation of the <c>beholder.local.BeholderLocal</c> gRPC
/// service. <see cref="Subscribe"/> streams live events; <see cref="GetSnapshot"/>
/// returns the current state for initial UI population. The remaining unary
/// RPCs return <see cref="StatusCode.Unimplemented"/> until later phases.
/// </summary>
internal sealed class BeholderLocalService : Local.BeholderLocal.BeholderLocalBase {
    private const int RecentAlertLimit = 100;

    private readonly BroadcastService _broadcaster;
    private readonly FlowEventPipeline _pipeline;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly IAlertStore _alertStore;
    private readonly ILogger<BeholderLocalService> _logger;

    public BeholderLocalService(
        BroadcastService broadcaster,
        FlowEventPipeline pipeline,
        SqliteFirewallRuleStore firewallStore,
        IAlertStore alertStore,
        ILogger<BeholderLocalService> logger
    ) {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(firewallStore);
        ArgumentNullException.ThrowIfNull(alertStore);
        ArgumentNullException.ThrowIfNull(logger);
        _broadcaster = broadcaster;
        _pipeline = pipeline;
        _firewallStore = firewallStore;
        _alertStore = alertStore;
        _logger = logger;
    }

    public override async Task Subscribe(
        Local.SubscribeRequest request,
        IServerStreamWriter<Local.DaemonEvent> responseStream,
        ServerCallContext context
    ) {
        _logger.LogInformation("Local IPC subscriber connected from {Peer}", context.Peer);
        try {
            await foreach (var daemonEvent in _broadcaster
                .SubscribeAsync(context.CancellationToken)
                .ConfigureAwait(false)) {
                await responseStream
                    .WriteAsync(daemonEvent, context.CancellationToken)
                    .ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) {
            // Client disconnected — expected.
        } finally {
            _logger.LogInformation("Local IPC subscriber disconnected from {Peer}", context.Peer);
        }
    }

    public override async Task<Local.GetSnapshotResponse> GetSnapshot(
        Local.GetSnapshotRequest request, ServerCallContext context
    ) {
        var ct = context.CancellationToken;
        var snapshots = await _pipeline.GetCurrentSnapshotsAsync(ct).ConfigureAwait(false);
        var rules = await _firewallStore.ListAllAsync(ct).ConfigureAwait(false);
        var alerts = await _alertStore.GetAlertsAsync(RecentAlertLimit, ct).ConfigureAwait(false);

        var response = new Local.GetSnapshotResponse();
        foreach (var snapshot in snapshots) response.Snapshots.Add(snapshot.ToProto());
        foreach (var rule in rules) response.FirewallRules.Add(rule.ToProto());
        foreach (var alert in alerts) response.RecentAlerts.Add(alert.ToProto());
        return response;
    }

    public override Task<Local.ApplyFirewallRuleResponse> ApplyFirewallRule(
        Local.ApplyFirewallRuleRequest request, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "ApplyFirewallRule lands in Phase 4.4"));

    public override Task<Local.MarkAlertReadResponse> MarkAlertRead(
        Local.MarkAlertReadRequest request, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "MarkAlertRead lands in Phase 4.4"));

    public override Task<Local.VerifyChainResponse> VerifyChain(
        Local.VerifyChainRequest request, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "VerifyChain lands in Phase 4.5"));
}
