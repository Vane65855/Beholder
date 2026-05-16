using Beholder.Core;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Protocol;
using Grpc.Core;
using Local = Beholder.Protocol.Local;

namespace Beholder.Daemon.Grpc;

/// <summary>
/// Server-side implementation of the <c>beholder.local.BeholderLocal</c> gRPC
/// service. Exposes the daemon's full local IPC surface: <see cref="Subscribe"/>
/// streams live events, <see cref="GetSnapshot"/> returns current state,
/// <see cref="ApplyFirewallRule"/> / <see cref="RemoveFirewallRule"/> /
/// <see cref="ListFirewallRules"/> / <see cref="SetFirewallEnabled"/>
/// coordinate firewall enforcement with persistence and chain logging,
/// <see cref="MarkAlertRead"/> stamps an alert's viewed-at time, and
/// <see cref="VerifyChain"/> validates the chain-hashed event log. Historical
/// query RPCs round out the surface for the UI's traffic views.
/// </summary>
internal sealed class BeholderLocalService : Local.BeholderLocal.BeholderLocalBase {
    private const int RecentAlertLimit = 100;

    private readonly BroadcastService _broadcaster;
    private readonly FlowEventPipeline _pipeline;
    private readonly IFirewallRuleStore _firewallStore;
    private readonly IAlertStore _alertStore;
    private readonly IFirewallController _firewallController;
    private readonly IFirewallEnforcementState _enforcementState;
    private readonly IEventStore _eventStore;
    private readonly ITrafficStore _trafficStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BeholderLocalService> _logger;

    public BeholderLocalService(
        BroadcastService broadcaster,
        FlowEventPipeline pipeline,
        IFirewallRuleStore firewallStore,
        IAlertStore alertStore,
        IFirewallController firewallController,
        IFirewallEnforcementState enforcementState,
        IEventStore eventStore,
        ITrafficStore trafficStore,
        TimeProvider timeProvider,
        ILogger<BeholderLocalService> logger
    ) {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(firewallStore);
        ArgumentNullException.ThrowIfNull(alertStore);
        ArgumentNullException.ThrowIfNull(firewallController);
        ArgumentNullException.ThrowIfNull(enforcementState);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(trafficStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _broadcaster = broadcaster;
        _pipeline = pipeline;
        _firewallStore = firewallStore;
        _alertStore = alertStore;
        _firewallController = firewallController;
        _enforcementState = enforcementState;
        _eventStore = eventStore;
        _trafficStore = trafficStore;
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
        try {
            var ct = context.CancellationToken;
            var snapshots = await _pipeline.GetCurrentSnapshotsAsync(ct).ConfigureAwait(false);
            var rules = await _firewallStore.ListAllAsync(ct).ConfigureAwait(false);
            var alerts = await _alertStore.GetAlertsAsync(RecentAlertLimit, ct).ConfigureAwait(false);

            var response = new Local.GetSnapshotResponse {
                FirewallEnforcementEnabled = _enforcementState.Enabled,
            };
            foreach (var snapshot in snapshots) response.Snapshots.Add(snapshot.ToProto());
            foreach (var rule in rules) response.FirewallRules.Add(rule.ToProto());
            foreach (var alert in alerts) response.RecentAlerts.Add(alert.ToProto());
            return response;
        } catch (Exception ex) {
            _logger.LogError(ex, "GetSnapshot failed");
            throw new RpcException(new Status(StatusCode.Internal,
                $"Failed to get snapshot: {ex.Message}"));
        }
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

        // Honour the master enforcement toggle: when disabled, persist + chain
        // + broadcast still run (so the rule is configured) but the OS firewall
        // is untouched. FirewallEnforcementService replays every persisted rule
        // through the controller when the master toggle flips back on.
        if (_enforcementState.Enabled) {
            try {
                await _firewallController.AddRuleAsync(candidateRule, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex, "Failed to apply firewall rule for {ProcessPath}", request.ProcessPath);
                throw new RpcException(new Status(StatusCode.Internal,
                    $"Failed to apply firewall rule: {ex.Message}"));
            }
        }

        FirewallRule persistedRule;
        try {
            persistedRule = await _firewallStore.UpsertAsync(candidateRule, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to persist firewall rule after OS apply, attempting rollback");
            // Only roll back the OS rule if we actually wrote one — when the
            // master toggle is OFF the AddRuleAsync above was skipped.
            if (_enforcementState.Enabled) {
                try {
                    await _firewallController.RemoveRuleAsync(
                        request.ProcessPath, direction, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception rollbackEx) {
                    _logger.LogCritical(rollbackEx,
                        "OS rollback failed — daemon view is inconsistent with OS firewall state for {ProcessPath}",
                        request.ProcessPath);
                }
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

    public override async Task<Local.RemoveFirewallRuleResponse> RemoveFirewallRule(
        Local.RemoveFirewallRuleRequest request, ServerCallContext context
    ) {
        var ct = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(request.ProcessPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "process_path is required"));

        var direction = request.Direction.FromProto();

        var existing = await _firewallStore.GetByProcessAndDirectionAsync(
            request.ProcessPath, direction, ct).ConfigureAwait(false);
        if (existing is null) {
            // Idempotent: nothing to remove from SQLite. The pass-through to
            // the OS controller (in case the persisted view drifted behind the
            // OS state) only fires when the master enforcement toggle is on —
            // off-mode means SQLite is the only thing we touch.
            if (_enforcementState.Enabled) {
                try {
                    await _firewallController.RemoveRuleAsync(
                        request.ProcessPath, direction, ct).ConfigureAwait(false);
                } catch (Exception ex) {
                    _logger.LogWarning(ex,
                        "Idempotent OS-side remove for unpersisted rule {ProcessPath} ({Direction}) failed",
                        request.ProcessPath, direction);
                }
            }
            return new Local.RemoveFirewallRuleResponse { Removed = false };
        }

        // Same enforcement gate as the Apply handler — when off, SQLite +
        // chain + broadcast still run below, but the OS firewall isn't touched.
        if (_enforcementState.Enabled) {
            try {
                await _firewallController.RemoveRuleAsync(
                    request.ProcessPath, direction, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to remove firewall rule from OS for {ProcessPath}", request.ProcessPath);
                throw new RpcException(new Status(StatusCode.Internal,
                    $"Failed to remove firewall rule: {ex.Message}"));
            }
        }

        // Per plan: if OS removal succeeded but SQLite delete fails we do NOT
        // roll back the OS state — the user's intent was "remove" and the OS
        // already matches that intent. Log Warning and continue.
        try {
            await _firewallStore.RemoveAsync(request.ProcessPath, direction, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "Failed to delete persisted firewall rule for {ProcessPath} after OS remove succeeded",
                request.ProcessPath);
        }

        try {
            var payload = FirewallRulePayloadEncoder.Encode(existing);
            await _eventStore.AppendAsync(EventKind.FirewallRuleRemoved, payload, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to append firewall rule removal to chain — rule is removed but unaudited");
        }

        try {
            _broadcaster.BroadcastRuleChange(
                existing, Local.FirewallRuleChange.Types.ChangeKind.Removed);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to broadcast firewall rule removal");
        }

        return new Local.RemoveFirewallRuleResponse {
            Removed = true,
            Rule = existing.ToProto(),
        };
    }

    public override Task<Local.ListFirewallRulesResponse> ListFirewallRules(
        Local.ListFirewallRulesRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(ListFirewallRules), async cancellationToken => {
        var rules = await _firewallStore.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var response = new Local.ListFirewallRulesResponse();
        foreach (var rule in rules) response.Rules.Add(rule.ToProto());
        return response;
    }, context);

    public override async Task<Local.SetFirewallEnabledResponse> SetFirewallEnabled(
        Local.SetFirewallEnabledRequest request, ServerCallContext context
    ) {
        var ct = context.CancellationToken;
        var changed = _enforcementState.SetEnabled(request.Enabled);

        // Only chain-audit real transitions — re-asserting the current state
        // is a no-op and would otherwise spam the activity strip.
        if (changed) {
            try {
                var payload = FirewallEnforcementTogglePayloadEncoder.Encode(
                    request.Enabled, _timeProvider.GetUtcNow());
                await _eventStore.AppendAsync(
                    EventKind.FirewallEnforcementToggled, payload, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to append enforcement toggle ({Enabled}) to chain", request.Enabled);
            }
            _logger.LogInformation("Firewall enforcement set to {Enabled}", request.Enabled);
        }

        return new Local.SetFirewallEnabledResponse { Enabled = _enforcementState.Enabled };
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

    public override Task<Local.GetProcessTimelineResponse> GetProcessTimeline(
        Local.GetProcessTimelineRequest request, ServerCallContext context
    ) {
        if (string.IsNullOrWhiteSpace(request.ProcessPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "process_path is required"));
        if (request.ResolutionMs <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "resolution_ms must be positive"));

        return ExecuteQueryAsync(nameof(GetProcessTimeline), async cancellationToken => {
            var from = request.FromUnixNs.FromUnixTimeNanoseconds();
            var to = request.ToUnixNs.FromUnixTimeNanoseconds();
            var resolution = TimeSpan.FromMilliseconds(request.ResolutionMs);

            var points = await _trafficStore.GetProcessTimelineAsync(
                request.ProcessPath, from, to, resolution, cancellationToken)
                .ConfigureAwait(false);

            var response = new Local.GetProcessTimelineResponse();
            foreach (var point in points) response.Points.Add(point.ToProto());
            return response;
        }, context);
    }

    public override Task<Local.GetProcessDestinationsResponse> GetProcessDestinations(
        Local.GetProcessDestinationsRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(GetProcessDestinations), async cancellationToken => {
        var from = request.FromUnixNs.FromUnixTimeNanoseconds();
        var to = request.ToUnixNs.FromUnixTimeNanoseconds();
        // Empty process_path = aggregate across all processes (Phase 6.3
        // widening for the Traffic tab's COLS view). Empty country =
        // no country filter (Phase 8 polish — the map hover passes a
        // specific alpha-2 to get per-country top-N).
        var processPath = string.IsNullOrWhiteSpace(request.ProcessPath) ? null : request.ProcessPath;
        var country = string.IsNullOrWhiteSpace(request.Country) ? null : request.Country;
        var query = new DestinationsQuery(processPath, from, to, country, request.Limit);

        var destinations = await _trafficStore.GetDestinationsAsync(query, cancellationToken)
            .ConfigureAwait(false);

        var response = new Local.GetProcessDestinationsResponse();
        foreach (var dest in destinations) response.Destinations.Add(dest.ToProto());
        return response;
    }, context);

    public override Task<Local.GetAggregateTimelineResponse> GetAggregateTimeline(
        Local.GetAggregateTimelineRequest request, ServerCallContext context
    ) {
        if (request.ResolutionMs <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "resolution_ms must be positive"));

        return ExecuteQueryAsync(nameof(GetAggregateTimeline), async cancellationToken => {
            var from = request.FromUnixNs.FromUnixTimeNanoseconds();
            var to = request.ToUnixNs.FromUnixTimeNanoseconds();
            var resolution = TimeSpan.FromMilliseconds(request.ResolutionMs);

            var points = await _trafficStore.GetAggregateTimelineAsync(
                from, to, resolution, cancellationToken)
                .ConfigureAwait(false);

            var response = new Local.GetAggregateTimelineResponse();
            foreach (var point in points) response.Points.Add(point.ToProto());
            return response;
        }, context);
    }

    public override Task<Local.GetCountryBreakdownResponse> GetCountryBreakdown(
        Local.GetCountryBreakdownRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(GetCountryBreakdown), async cancellationToken => {
        var from = request.FromUnixNs.FromUnixTimeNanoseconds();
        var to = request.ToUnixNs.FromUnixTimeNanoseconds();
        var processPath = string.IsNullOrWhiteSpace(request.ProcessPath) ? null : request.ProcessPath;

        var breakdown = await _trafficStore.GetCountryBreakdownAsync(
            processPath, from, to, cancellationToken)
            .ConfigureAwait(false);

        var response = new Local.GetCountryBreakdownResponse();
        foreach (var summary in breakdown) response.Countries.Add(summary.ToProto());
        return response;
    }, context);

    public override Task<Local.GetProtocolBreakdownResponse> GetProtocolBreakdown(
        Local.GetProtocolBreakdownRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(GetProtocolBreakdown), async cancellationToken => {
        var from = request.FromUnixNs.FromUnixTimeNanoseconds();
        var to = request.ToUnixNs.FromUnixTimeNanoseconds();
        var processPath = string.IsNullOrWhiteSpace(request.ProcessPath) ? null : request.ProcessPath;

        var breakdown = await _trafficStore.GetProtocolBreakdownAsync(
            processPath, from, to, cancellationToken)
            .ConfigureAwait(false);

        var response = new Local.GetProtocolBreakdownResponse();
        foreach (var summary in breakdown) response.Protocols.Add(summary.ToProto());
        return response;
    }, context);

    public override Task<Local.GetFirewallActivityResponse> GetFirewallActivity(
        Local.GetFirewallActivityRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(GetFirewallActivity), async cancellationToken => {
        // 0 → server default. Negative → invalid (caught by ExecuteQueryAsync's
        // ArgumentOutOfRangeException → InvalidArgument mapping). Above the
        // hard cap → silently clamped: clients shouldn't be able to ask the
        // daemon for an unbounded slice of the chain through this RPC.
        const int defaultLimit = 100;
        const int hardCap = 500;
        var limit = request.Limit == 0 ? defaultLimit : request.Limit;
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(request.Limit), "limit must be non-negative");
        if (limit > hardCap) limit = hardCap;

        var firewallKinds = new[] {
            EventKind.FirewallRuleCreated,
            EventKind.FirewallRuleChanged,
            EventKind.FirewallRuleRemoved,
            EventKind.FirewallEnforcementToggled,
        };
        var entries = await _eventStore.ListByKindsAsync(firewallKinds, limit, cancellationToken)
            .ConfigureAwait(false);

        var response = new Local.GetFirewallActivityResponse();
        foreach (var entry in entries) {
            response.Events.Add(BuildActivityEvent(entry));
        }
        return response;
    }, context);

    /// <summary>
    /// Decodes the chain payload for one firewall activity entry and packs it
    /// into the wire message. Bad payloads (decoder returns null) round-trip
    /// as best-effort: the kind + timestamp + seq still surface, but the
    /// rule-level fields default to empty so the UI can still render the row.
    /// </summary>
    private static Local.FirewallActivityEvent BuildActivityEvent(EventLogEntry entry) {
        var ev = new Local.FirewallActivityEvent {
            Seq = entry.Seq,
            TimestampUnixNs = entry.Timestamp.ToUnixTimeNanoseconds(),
            Kind = entry.Kind switch {
                EventKind.FirewallRuleCreated => Local.FirewallActivityKind.RuleCreated,
                EventKind.FirewallRuleChanged => Local.FirewallActivityKind.RuleChanged,
                EventKind.FirewallRuleRemoved => Local.FirewallActivityKind.RuleRemoved,
                EventKind.FirewallEnforcementToggled => Local.FirewallActivityKind.EnforcementToggled,
                _ => Local.FirewallActivityKind.FirewallActivityUnknown,
            },
        };

        switch (entry.Kind) {
            case EventKind.FirewallRuleCreated:
            case EventKind.FirewallRuleChanged:
            case EventKind.FirewallRuleRemoved:
                var rule = FirewallRulePayloadEncoder.TryDecode(entry.Payload);
                if (rule is not null) {
                    ev.ProcessPath = rule.ProcessPath;
                    ev.Direction = rule.Direction.ToProto();
                    ev.Action = rule.Action.ToProto();
                    ev.Source = rule.Source.ToProto();
                }
                break;
            case EventKind.FirewallEnforcementToggled:
                var toggle = FirewallEnforcementTogglePayloadEncoder.TryDecode(entry.Payload);
                if (toggle is not null) {
                    ev.EnforcementEnabled = toggle.Value.Enabled;
                }
                break;
        }
        return ev;
    }

    public override Task<Local.GetProcessSummariesResponse> GetProcessSummaries(
        Local.GetProcessSummariesRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(GetProcessSummaries), async cancellationToken => {
        var from = request.FromUnixNs.FromUnixTimeNanoseconds();
        var to = request.ToUnixNs.FromUnixTimeNanoseconds();

        var summaries = await _trafficStore.GetProcessSummariesAsync(
            from, to, cancellationToken)
            .ConfigureAwait(false);

        var response = new Local.GetProcessSummariesResponse();
        foreach (var summary in summaries) response.Summaries.Add(summary.ToProto());
        return response;
    }, context);

    /// <summary>
    /// Runs a query RPC's inner work with unified exception classification.
    /// <see cref="ArgumentOutOfRangeException"/> (inverted ranges from store
    /// guards) and <see cref="ArgumentException"/> (whitespace string guards)
    /// surface as <see cref="StatusCode.InvalidArgument"/>; anything else is
    /// logged and surfaced as <see cref="StatusCode.Internal"/>.
    /// <see cref="OperationCanceledException"/> and existing
    /// <see cref="RpcException"/>s propagate untouched.
    /// </summary>
    private async Task<TResponse> ExecuteQueryAsync<TResponse>(
        string rpcName,
        Func<CancellationToken, Task<TResponse>> query,
        ServerCallContext context
    ) {
        try {
            return await query(context.CancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (RpcException) {
            throw;
        } catch (ArgumentOutOfRangeException ex) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        } catch (ArgumentException ex) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        } catch (Exception ex) {
            _logger.LogError(ex, "{Rpc} failed", rpcName);
            throw new RpcException(new Status(StatusCode.Internal,
                $"{rpcName} failed: {ex.Message}"));
        }
    }
}
