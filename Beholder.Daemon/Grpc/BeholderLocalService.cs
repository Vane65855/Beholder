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
/// returns the current state for initial UI population;
/// <see cref="ApplyFirewallRule"/> coordinates OS enforcement, persistence, chain
/// logging, and subscriber notification. Remaining unary RPCs return
/// <see cref="StatusCode.Unimplemented"/> until later phases.
/// </summary>
internal sealed class BeholderLocalService : Local.BeholderLocal.BeholderLocalBase {
    private const int RecentAlertLimit = 100;

    private readonly BroadcastService _broadcaster;
    private readonly FlowEventPipeline _pipeline;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly IAlertStore _alertStore;
    private readonly IFirewallController _firewallController;
    private readonly IEventStore _eventStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BeholderLocalService> _logger;

    public BeholderLocalService(
        BroadcastService broadcaster,
        FlowEventPipeline pipeline,
        SqliteFirewallRuleStore firewallStore,
        IAlertStore alertStore,
        IFirewallController firewallController,
        IEventStore eventStore,
        TimeProvider timeProvider,
        ILogger<BeholderLocalService> logger
    ) {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(firewallStore);
        ArgumentNullException.ThrowIfNull(alertStore);
        ArgumentNullException.ThrowIfNull(firewallController);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _broadcaster = broadcaster;
        _pipeline = pipeline;
        _firewallStore = firewallStore;
        _alertStore = alertStore;
        _firewallController = firewallController;
        _eventStore = eventStore;
        _timeProvider = timeProvider;
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

    public override async Task<Local.ApplyFirewallRuleResponse> ApplyFirewallRule(
        Local.ApplyFirewallRuleRequest request, ServerCallContext context
    ) {
        var ct = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(request.ProcessPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "process_path is required"));

        var direction = request.Direction.FromProto();
        var action = request.Action.FromProto();
        var source = request.Source.FromProto();

        var existing = await _firewallStore.GetByProcessAndDirectionAsync(
            request.ProcessPath, direction, ct).ConfigureAwait(false);
        var isUpdate = existing is not null;

        var now = _timeProvider.GetUtcNow();
        var candidateRule = new FirewallRule(
            id: existing?.Id ?? 0,
            processPath: request.ProcessPath,
            direction: direction,
            action: action,
            source: source,
            createdAt: existing?.CreatedAt ?? now,
            updatedAt: now);

        try {
            await _firewallController.AddRuleAsync(candidateRule, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to apply firewall rule for {ProcessPath}", request.ProcessPath);
            throw new RpcException(new Status(StatusCode.Internal,
                $"Failed to apply firewall rule: {ex.Message}"));
        }

        FirewallRule persistedRule;
        try {
            persistedRule = await _firewallStore.UpsertAsync(candidateRule, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to persist firewall rule after OS apply, attempting rollback");
            try {
                await _firewallController.RemoveRuleAsync(
                    request.ProcessPath, direction, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception rollbackEx) {
                _logger.LogCritical(rollbackEx,
                    "OS rollback failed — daemon view is inconsistent with OS firewall state for {ProcessPath}",
                    request.ProcessPath);
            }
            throw new RpcException(new Status(StatusCode.Internal,
                $"Failed to persist firewall rule: {ex.Message}"));
        }

        try {
            var eventKind = isUpdate ? EventKind.FirewallRuleChanged : EventKind.FirewallRuleCreated;
            var payload = FirewallRulePayloadEncoder.Encode(persistedRule);
            await _eventStore.AppendAsync(eventKind, payload, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to append firewall rule change to chain — rule is applied but unaudited");
        }

        try {
            var changeKind = isUpdate
                ? Local.FirewallRuleChange.Types.ChangeKind.Changed
                : Local.FirewallRuleChange.Types.ChangeKind.Created;
            _broadcaster.BroadcastRuleChange(persistedRule, changeKind);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to broadcast firewall rule change");
        }

        return new Local.ApplyFirewallRuleResponse { Rule = persistedRule.ToProto() };
    }

    public override async Task<Local.MarkAlertReadResponse> MarkAlertRead(
        Local.MarkAlertReadRequest request, ServerCallContext context
    ) {
        if (request.Seq <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "seq must be positive"));

        var viewedAt = _timeProvider.GetUtcNow();

        try {
            await _alertStore.MarkAlertReadAsync(request.Seq, viewedAt, context.CancellationToken)
                .ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to mark alert {Seq} as read", request.Seq);
            throw new RpcException(new Status(StatusCode.Internal,
                $"Failed to mark alert as read: {ex.Message}"));
        }

        return new Local.MarkAlertReadResponse();
    }

    public override async Task<Local.VerifyChainResponse> VerifyChain(
        Local.VerifyChainRequest request, ServerCallContext context
    ) {
        try {
            var result = await _eventStore.VerifyAsync(context.CancellationToken)
                .ConfigureAwait(false);
            return result.ToProto();
        } catch (Exception ex) {
            _logger.LogError(ex, "Chain verification failed unexpectedly");
            throw new RpcException(new Status(StatusCode.Internal,
                $"Chain verification error: {ex.Message}"));
        }
    }
}
