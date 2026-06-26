using Beholder.Core;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Scanner;
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
    private const int DefaultLanDeviceListLimit = 200;
    private const int MaxLanDeviceListLimit = 1000;
    /// <summary>Phase 9.5: maximum user-supplied device label length. 100
    /// chars is the conventional ceiling for short cosmetic display names
    /// in this product class — long enough for descriptive labels
    /// ("Kitchen — Living Room TV (Samsung)") without enabling pathological
    /// store / wire sizes.</summary>
    private const int MaxLanDeviceLabelLength = 100;

    /// <summary>Phase 11.3: daemon version stamped into chain-export metadata
    /// for provenance. Read once from the assembly's informational version
    /// (falls back to the plain assembly version, then "unknown").</summary>
    private static readonly string DaemonVersion =
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute attr, ..]
            ? attr.InformationalVersion
            : System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private readonly BroadcastService _broadcaster;
    private readonly FlowEventPipeline _pipeline;
    private readonly IFirewallRuleStore _firewallStore;
    private readonly IAlertStore _alertStore;
    private readonly IFirewallController _firewallController;
    private readonly IFirewallEnforcementState _enforcementState;
    private readonly IEventStore _eventStore;
    private readonly ITrafficStore _trafficStore;
    private readonly ILanDeviceStore _lanDeviceStore;
    private readonly LanScannerService _lanScannerService;
    private readonly IChainStatusCache _chainStatusCache;
    private readonly IChainVerifier _chainVerifier;
    private readonly IChainExporter _chainExporter;
    private readonly IStorageStatsProvider _storageStatsProvider;
    private readonly IRecordingSettingsState _recordingSettings;
    private readonly IHostnameResolutionSettingsState _hostnameResolutionSettings;
    private readonly IAlertSettingsState _alertSettings;
    private readonly IScannerSettingsState _scannerSettings;
    private readonly ISettingsOverridesStore _settingsOverridesStore;
    private readonly IAppIdentityRuleStore _appIdentityRuleStore;
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
        ILanDeviceStore lanDeviceStore,
        LanScannerService lanScannerService,
        IChainStatusCache chainStatusCache,
        IChainVerifier chainVerifier,
        IChainExporter chainExporter,
        IStorageStatsProvider storageStatsProvider,
        IRecordingSettingsState recordingSettings,
        IHostnameResolutionSettingsState hostnameResolutionSettings,
        IAlertSettingsState alertSettings,
        IScannerSettingsState scannerSettings,
        ISettingsOverridesStore settingsOverridesStore,
        IAppIdentityRuleStore appIdentityRuleStore,
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
        ArgumentNullException.ThrowIfNull(lanDeviceStore);
        ArgumentNullException.ThrowIfNull(lanScannerService);
        ArgumentNullException.ThrowIfNull(chainStatusCache);
        ArgumentNullException.ThrowIfNull(chainVerifier);
        ArgumentNullException.ThrowIfNull(chainExporter);
        ArgumentNullException.ThrowIfNull(storageStatsProvider);
        ArgumentNullException.ThrowIfNull(recordingSettings);
        ArgumentNullException.ThrowIfNull(hostnameResolutionSettings);
        ArgumentNullException.ThrowIfNull(alertSettings);
        ArgumentNullException.ThrowIfNull(scannerSettings);
        ArgumentNullException.ThrowIfNull(settingsOverridesStore);
        ArgumentNullException.ThrowIfNull(appIdentityRuleStore);
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
        _lanDeviceStore = lanDeviceStore;
        _lanScannerService = lanScannerService;
        _chainStatusCache = chainStatusCache;
        _chainVerifier = chainVerifier;
        _chainExporter = chainExporter;
        _storageStatsProvider = storageStatsProvider;
        _recordingSettings = recordingSettings;
        _hostnameResolutionSettings = hostnameResolutionSettings;
        _alertSettings = alertSettings;
        _scannerSettings = scannerSettings;
        _settingsOverridesStore = settingsOverridesStore;
        _appIdentityRuleStore = appIdentityRuleStore;
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
        // The event-log sequence is 0-based, so the genesis row (the oldest
        // alert) is legitimately seq 0 — reject only negatives, not 0.
        if (request.Seq < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "seq must be non-negative"));

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
        ArgumentNullException.ThrowIfNull(request);
        try {
            // Phase 11.2: anchor on the latest signed checkpoint unless the
            // caller forces a full genesis-to-head walk.
            var result = await _chainVerifier
                .VerifyAsync(request.ForceFull, context.CancellationToken)
                .ConfigureAwait(false);
            // Mirror the ChainIntegrityMonitor cache write so user-triggered
            // verifications also update the Settings tab's "last verified at"
            // display. Either writer's most-recent result wins.
            _chainStatusCache.Update(result, _timeProvider.GetUtcNow());
            return result.ToProto();
        } catch (Exception ex) {
            _logger.LogError(ex, "Chain verification failed unexpectedly");
            throw new RpcException(new Status(StatusCode.Internal,
                $"Chain verification error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Phase 11.3: returns a signed JSON export of the chain-hashed event log
    /// over the requested seq range (both bounds inclusive; 0 = open-ended).
    /// Read-only — exporting does not mutate or audit the chain. See ADR 012
    /// for the envelope schema + verification contract.
    /// </summary>
    public override Task<Local.ExportChainResponse> ExportChain(
        Local.ExportChainRequest request, ServerCallContext context
    ) {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteQueryAsync(nameof(ExportChain), async cancellationToken => {
            var rows = await _eventStore
                .ReadRangeAsync(request.FromSeq, request.ToSeq, cancellationToken)
                .ConfigureAwait(false);
            var signedExport = _chainExporter.Export(
                rows, request.FromSeq, request.ToSeq, _timeProvider.GetUtcNow(), DaemonVersion);
            return new Local.ExportChainResponse {
                SignedExport = Google.Protobuf.ByteString.CopyFrom(signedExport),
                EventCount = rows.Count,
            };
        }, context);
    }

    /// <summary>
    /// Phase 13.1: returns per-table row counts + the database file size +
    /// the most-recent chain-verification result for the Settings tab's
    /// Data Storage section. Single round-trip; called once on tab activate
    /// and on every refresh-button press.
    /// </summary>
    public override Task<Local.GetStorageStatsResponse> GetStorageStats(
        Local.GetStorageStatsRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(GetStorageStats), async cancellationToken => {
        var stats = await _storageStatsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        return stats.ToProto();
    }, context);

    /// <summary>
    /// Phase 13.2: returns the current values for every Settings section the
    /// UI renders. Single round-trip; called once on Settings tab activate.
    /// Future sub-phases (13.3 Alerts, 13.4 Scanner, 13.5 Storage) add their
    /// own value sub-messages here.
    /// </summary>
    public override Task<Local.GetSettingsResponse> GetSettings(
        Local.GetSettingsRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(GetSettings), _ => {
        var response = new Local.GetSettingsResponse {
            Recording = new RecordingSettingsSnapshot(_recordingSettings.FilterSelfTraffic).ToProto(),
            HostnameResolution = new HostnameResolutionSettingsSnapshot(
                EnablePreload: _hostnameResolutionSettings.EnablePreload,
                EnableReverseDnsFallback: _hostnameResolutionSettings.EnableReverseDnsFallback,
                EnableSniCapture: _hostnameResolutionSettings.EnableSniCapture).ToProto(),
            Alerts = new AlertSettingsSnapshot(
                EnableNewProcessDetection: _alertSettings.EnableNewProcessDetection,
                EnableHashChangeDetection: _alertSettings.EnableHashChangeDetection,
                EnableChainIntegrityMonitor: _alertSettings.EnableChainIntegrityMonitor).ToProto(),
            Scanner = new ScannerSettingsSnapshot(_scannerSettings.EnableHostnameResolution).ToProto(),
        };
        return Task.FromResult(response);
    }, context);

    /// <summary>
    /// Phase 13.2: atomically updates the Recording section's settings.
    /// On a real transition (state actually changed), persists the new value
    /// to <c>settings_overrides</c> and appends a
    /// <see cref="EventKind.RecordingSettingsChanged"/> chain event so the
    /// change is audit-trailed. Idempotent: re-asserting the current value
    /// is a no-op (no persistence, no chain write). Recoverable failures
    /// surface as <c>success=false</c> with a human-readable message; hard
    /// validation errors throw <see cref="StatusCode.InvalidArgument"/>.
    /// </summary>
    public override async Task<Local.SetRecordingSettingsResponse> SetRecordingSettings(
        Local.SetRecordingSettingsRequest request, ServerCallContext context
    ) {
        if (request.Values is null) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "values is required"));
        }

        var ct = context.CancellationToken;
        var requested = request.Values.FilterSelfTraffic;
        var changed = _recordingSettings.SetSettings(requested);

        if (changed) {
            try {
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.RecordingFilterSelfTraffic,
                    requested ? "true" : "false",
                    ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to persist Recording settings override; in-memory state is updated but the change will not survive restart");
                return new Local.SetRecordingSettingsResponse {
                    Success = false,
                    Message = $"Failed to persist settings: {ex.Message}",
                    Values = new RecordingSettingsSnapshot(_recordingSettings.FilterSelfTraffic).ToProto(),
                };
            }

            try {
                var payload = RecordingSettingsPayloadEncoder.Encode(requested, _timeProvider.GetUtcNow());
                await _eventStore.AppendAsync(
                    EventKind.RecordingSettingsChanged, payload, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to append Recording settings change to chain — change is applied but unaudited");
            }
            _logger.LogInformation(
                "Recording settings updated (FilterSelfTraffic={FilterSelfTraffic})", requested);
        }

        return new Local.SetRecordingSettingsResponse {
            Success = true,
            Message = changed ? "Settings updated." : "Settings unchanged.",
            Values = new RecordingSettingsSnapshot(_recordingSettings.FilterSelfTraffic).ToProto(),
        };
    }

    /// <summary>
    /// Phase 13.2: atomically updates the Hostname Resolution section's
    /// settings. Same idempotency / persistence / chain-audit contract as
    /// <see cref="SetRecordingSettings"/>. Note that only
    /// <c>EnableReverseDnsFallback</c> takes effect immediately; the other
    /// two values are persisted but their consumers
    /// (<c>EtwDnsCache</c> / <c>PktmonSniSource</c>) read them once at
    /// startup, so toggles surface in behaviour only on the next daemon
    /// start — the UI renders a caption to make the timing honest.
    /// </summary>
    public override async Task<Local.SetHostnameResolutionSettingsResponse> SetHostnameResolutionSettings(
        Local.SetHostnameResolutionSettingsRequest request, ServerCallContext context
    ) {
        if (request.Values is null) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "values is required"));
        }

        var ct = context.CancellationToken;
        var values = request.Values;
        var changed = _hostnameResolutionSettings.SetSettings(
            enablePreload: values.EnablePreload,
            enableReverseDnsFallback: values.EnableReverseDnsFallback,
            enableSniCapture: values.EnableSniCapture);

        if (changed) {
            try {
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.DnsEnablePreload,
                    values.EnablePreload ? "true" : "false",
                    ct).ConfigureAwait(false);
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.DnsEnableReverseDnsFallback,
                    values.EnableReverseDnsFallback ? "true" : "false",
                    ct).ConfigureAwait(false);
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.SniEnableSniCapture,
                    values.EnableSniCapture ? "true" : "false",
                    ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to persist Hostname Resolution settings override; in-memory state is updated but the change will not survive restart");
                return new Local.SetHostnameResolutionSettingsResponse {
                    Success = false,
                    Message = $"Failed to persist settings: {ex.Message}",
                    Values = new HostnameResolutionSettingsSnapshot(
                        EnablePreload: _hostnameResolutionSettings.EnablePreload,
                        EnableReverseDnsFallback: _hostnameResolutionSettings.EnableReverseDnsFallback,
                        EnableSniCapture: _hostnameResolutionSettings.EnableSniCapture).ToProto(),
                };
            }

            try {
                var payload = HostnameResolutionSettingsPayloadEncoder.Encode(
                    values.EnablePreload,
                    values.EnableReverseDnsFallback,
                    values.EnableSniCapture,
                    _timeProvider.GetUtcNow());
                await _eventStore.AppendAsync(
                    EventKind.HostnameResolutionSettingsChanged, payload, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to append Hostname Resolution settings change to chain — change is applied but unaudited");
            }
            _logger.LogInformation(
                "Hostname Resolution settings updated (EnablePreload={Preload}, EnableReverseDnsFallback={Rdns}, EnableSniCapture={Sni})",
                values.EnablePreload, values.EnableReverseDnsFallback, values.EnableSniCapture);
        }

        return new Local.SetHostnameResolutionSettingsResponse {
            Success = true,
            Message = changed ? "Settings updated." : "Settings unchanged.",
            Values = new HostnameResolutionSettingsSnapshot(
                EnablePreload: _hostnameResolutionSettings.EnablePreload,
                EnableReverseDnsFallback: _hostnameResolutionSettings.EnableReverseDnsFallback,
                EnableSniCapture: _hostnameResolutionSettings.EnableSniCapture).ToProto(),
        };
    }

    /// <summary>
    /// Phase 13.3: atomically updates the Alerts section's settings (the
    /// three master kill-switches for the alert detector pipeline). Same
    /// contract as <see cref="SetRecordingSettings"/> /
    /// <see cref="SetHostnameResolutionSettings"/>: idempotent re-asserts are
    /// no-ops; real transitions persist all three keys to
    /// <c>settings_overrides</c> and append one chain entry.
    /// </summary>
    public override async Task<Local.SetAlertSettingsResponse> SetAlertSettings(
        Local.SetAlertSettingsRequest request, ServerCallContext context
    ) {
        if (request.Values is null) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "values is required"));
        }

        var ct = context.CancellationToken;
        var values = request.Values;
        var changed = _alertSettings.SetSettings(
            enableNewProcessDetection: values.EnableNewProcessDetection,
            enableHashChangeDetection: values.EnableHashChangeDetection,
            enableChainIntegrityMonitor: values.EnableChainIntegrityMonitor);

        if (changed) {
            try {
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.AlertEnableNewProcessDetection,
                    values.EnableNewProcessDetection ? "true" : "false",
                    ct).ConfigureAwait(false);
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.AlertEnableHashChangeDetection,
                    values.EnableHashChangeDetection ? "true" : "false",
                    ct).ConfigureAwait(false);
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.AlertEnableChainIntegrityMonitor,
                    values.EnableChainIntegrityMonitor ? "true" : "false",
                    ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to persist Alert settings override; in-memory state is updated but the change will not survive restart");
                return new Local.SetAlertSettingsResponse {
                    Success = false,
                    Message = $"Failed to persist settings: {ex.Message}",
                    Values = new AlertSettingsSnapshot(
                        EnableNewProcessDetection: _alertSettings.EnableNewProcessDetection,
                        EnableHashChangeDetection: _alertSettings.EnableHashChangeDetection,
                        EnableChainIntegrityMonitor: _alertSettings.EnableChainIntegrityMonitor).ToProto(),
                };
            }

            try {
                var payload = AlertSettingsPayloadEncoder.Encode(
                    values.EnableNewProcessDetection,
                    values.EnableHashChangeDetection,
                    values.EnableChainIntegrityMonitor,
                    _timeProvider.GetUtcNow());
                await _eventStore.AppendAsync(
                    EventKind.AlertSettingsChanged, payload, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to append Alert settings change to chain — change is applied but unaudited");
            }
            _logger.LogInformation(
                "Alert settings updated (EnableNewProcessDetection={NewProc}, EnableHashChangeDetection={Hash}, EnableChainIntegrityMonitor={Chain})",
                values.EnableNewProcessDetection,
                values.EnableHashChangeDetection,
                values.EnableChainIntegrityMonitor);
        }

        return new Local.SetAlertSettingsResponse {
            Success = true,
            Message = changed ? "Settings updated." : "Settings unchanged.",
            Values = new AlertSettingsSnapshot(
                EnableNewProcessDetection: _alertSettings.EnableNewProcessDetection,
                EnableHashChangeDetection: _alertSettings.EnableHashChangeDetection,
                EnableChainIntegrityMonitor: _alertSettings.EnableChainIntegrityMonitor).ToProto(),
        };
    }

    /// <summary>
    /// Phase 13.4: atomically updates the Scanner section's settings (a single
    /// bool kill-switch for the LAN scanner's hostname-resolution pass). Same
    /// contract as the other <c>Set*</c> handlers. Live read at scan time —
    /// the toggle takes effect on the next scan tick, NOT on next daemon start
    /// (see <see cref="WindowsLanDeviceProbe"/>'s ScanAsync gate).
    /// </summary>
    public override async Task<Local.SetScannerSettingsResponse> SetScannerSettings(
        Local.SetScannerSettingsRequest request, ServerCallContext context
    ) {
        if (request.Values is null) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "values is required"));
        }

        var ct = context.CancellationToken;
        var requested = request.Values.EnableHostnameResolution;
        var changed = _scannerSettings.SetSettings(requested);

        if (changed) {
            try {
                await _settingsOverridesStore.UpsertAsync(
                    SettingsKeys.ScannerEnableHostnameResolution,
                    requested ? "true" : "false",
                    ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to persist Scanner settings override; in-memory state is updated but the change will not survive restart");
                return new Local.SetScannerSettingsResponse {
                    Success = false,
                    Message = $"Failed to persist settings: {ex.Message}",
                    Values = new ScannerSettingsSnapshot(_scannerSettings.EnableHostnameResolution).ToProto(),
                };
            }

            try {
                var payload = ScannerSettingsPayloadEncoder.Encode(requested, _timeProvider.GetUtcNow());
                await _eventStore.AppendAsync(
                    EventKind.ScannerSettingsChanged, payload, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to append Scanner settings change to chain — change is applied but unaudited");
            }
            _logger.LogInformation(
                "Scanner settings updated (EnableHostnameResolution={Enabled})", requested);
        }

        return new Local.SetScannerSettingsResponse {
            Success = true,
            Message = changed ? "Settings updated." : "Settings unchanged.",
            Values = new ScannerSettingsSnapshot(_scannerSettings.EnableHostnameResolution).ToProto(),
        };
    }

    /// <summary>
    /// Phase 13.6 (ADR 011): adds a manual application-identity rule. The
    /// daemon trusts the user's anchor + filename pairing and treats matching
    /// binaries as the same logical app — suppresses NewProcess alerts on
    /// future versions sharing the rule's anchor. Soft-failure on duplicate
    /// (returns <c>success=false</c> with a structured message); hard
    /// validation errors (empty anchor or filename) throw
    /// <see cref="StatusCode.InvalidArgument"/>.
    /// </summary>
    public override async Task<Local.AddAppIdentityRuleResponse> AddAppIdentityRule(
        Local.AddAppIdentityRuleRequest request, ServerCallContext context
    ) {
        if (string.IsNullOrWhiteSpace(request.AnchorPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "anchor_path is required"));
        if (string.IsNullOrWhiteSpace(request.Filename))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "filename is required"));

        var ct = context.CancellationToken;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName;

        AppIdentityRule? created;
        try {
            created = await _appIdentityRuleStore
                .AddAsync(request.AnchorPath, request.Filename, displayName, ct)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to persist app-identity rule (anchor={Anchor} filename={Filename})",
                request.AnchorPath, request.Filename);
            return new Local.AddAppIdentityRuleResponse {
                Success = false,
                Message = $"Failed to persist rule: {ex.Message}",
            };
        }

        if (created is null) {
            // Duplicate (anchor, filename) — soft-fail; UI surfaces inline.
            return new Local.AddAppIdentityRuleResponse {
                Success = false,
                Message = "A rule with this anchor + filename already exists.",
            };
        }

        try {
            var payload = AppIdentityRulePayloadEncoder.Encode(created);
            await _eventStore.AppendAsync(EventKind.AppIdentityRuleCreated, payload, ct)
                .ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to append AppIdentityRuleCreated to chain — rule is persisted but unaudited (id={Id})",
                created.Id);
        }

        _logger.LogInformation(
            "App-identity rule created (id={Id}, anchor={Anchor}, filename={Filename})",
            created.Id, created.AnchorPath, created.Filename);

        return new Local.AddAppIdentityRuleResponse {
            Success = true,
            Message = "Rule created.",
            Rule = created.ToProto(),
        };
    }

    /// <summary>
    /// Phase 13.6: removes a manual application-identity rule by ID.
    /// Idempotent — unknown ID returns <c>removed=false</c> with no chain
    /// write.
    /// </summary>
    public override async Task<Local.RemoveAppIdentityRuleResponse> RemoveAppIdentityRule(
        Local.RemoveAppIdentityRuleRequest request, ServerCallContext context
    ) {
        if (request.Id <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "id must be positive"));

        var ct = context.CancellationToken;

        // Snapshot the row BEFORE delete so the chain payload carries the
        // removed rule's anchor / filename / display name (the row no longer
        // exists post-delete; the chain audit needs the values).
        var existing = (await _appIdentityRuleStore.ListAllAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(r => r.Id == request.Id);

        bool removed;
        try {
            removed = await _appIdentityRuleStore.RemoveAsync(request.Id, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to remove app-identity rule (id={Id})", request.Id);
            throw new RpcException(new Status(StatusCode.Internal,
                $"Failed to remove rule: {ex.Message}"));
        }

        if (removed && existing is not null) {
            try {
                var payload = AppIdentityRulePayloadEncoder.Encode(existing);
                await _eventStore.AppendAsync(EventKind.AppIdentityRuleRemoved, payload, ct)
                    .ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to append AppIdentityRuleRemoved to chain — rule is removed but unaudited (id={Id})",
                    request.Id);
            }
            _logger.LogInformation("App-identity rule removed (id={Id})", request.Id);
        }

        return new Local.RemoveAppIdentityRuleResponse { Removed = removed };
    }

    /// <summary>
    /// Phase 13.6: returns every persisted manual application-identity rule
    /// in insertion (ID) order. Called by the Settings tab on activate to
    /// populate the rule list.
    /// </summary>
    public override Task<Local.ListAppIdentityRulesResponse> ListAppIdentityRules(
        Local.ListAppIdentityRulesRequest request, ServerCallContext context
    ) => ExecuteQueryAsync(nameof(ListAppIdentityRules), async cancellationToken => {
        var rules = await _appIdentityRuleStore.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var response = new Local.ListAppIdentityRulesResponse();
        foreach (var rule in rules) response.Rules.Add(rule.ToProto());
        return response;
    }, context);

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
            // Phase 9.6 fix: empty string = no filter (preserves pre-9.6 contract).
            var remoteAddress = string.IsNullOrEmpty(request.RemoteAddress)
                ? null
                : request.RemoteAddress;

            var points = await _trafficStore.GetProcessTimelineAsync(
                request.ProcessPath, from, to, resolution, cancellationToken,
                remoteAddress)
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
            // Phase 9.6 fix: empty string = no filter (preserves pre-9.6 contract).
            var remoteAddress = string.IsNullOrEmpty(request.RemoteAddress)
                ? null
                : request.RemoteAddress;

            var points = await _trafficStore.GetAggregateTimelineAsync(
                from, to, resolution, cancellationToken, remoteAddress)
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
        // Phase 9.6: empty string = no filter (preserves pre-9.6 behavior).
        var remoteAddress = string.IsNullOrEmpty(request.RemoteAddress)
            ? null
            : request.RemoteAddress;

        var summaries = await _trafficStore.GetProcessSummariesAsync(
            from, to, cancellationToken, remoteAddress)
            .ConfigureAwait(false);

        var response = new Local.GetProcessSummariesResponse();
        foreach (var summary in summaries) response.Summaries.Add(summary.ToProto());
        return response;
    }, context);

    public override Task<Local.ListLanDevicesResponse> ListLanDevices(
        Local.ListLanDevicesRequest request, ServerCallContext context
    ) {
        if (request.Limit < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "limit must be non-negative"));

        return ExecuteQueryAsync(nameof(ListLanDevices), async cancellationToken => {
            // 0 → server default; clamped to the hard cap so a client can't
            // ask the daemon for an unbounded slice of the store.
            var requestedLimit = request.Limit == 0
                ? DefaultLanDeviceListLimit
                : Math.Min(request.Limit, MaxLanDeviceListLimit);
            DateTimeOffset? seenSince = request.SeenSinceUnixNs > 0
                ? request.SeenSinceUnixNs.FromUnixTimeNanoseconds()
                : null;
            var query = new LanDeviceQuery(seenSince, requestedLimit);

            var devices = await _lanDeviceStore.ListAsync(query, cancellationToken).ConfigureAwait(false);

            var response = new Local.ListLanDevicesResponse();
            foreach (var device in devices) response.Devices.Add(device.ToProto());
            return response;
        }, context);
    }

    public override async Task<Local.TriggerScanResponse> TriggerScan(
        Local.TriggerScanRequest request, ServerCallContext context
    ) {
        try {
            var count = await _lanScannerService
                .RunOnceManuallyAsync(context.CancellationToken)
                .ConfigureAwait(false);
            return new Local.TriggerScanResponse {
                Success = true,
                Message = $"Scan complete: {count} devices observed",
                DevicesObserved = count,
            };
        } catch (OperationCanceledException) {
            // Caller (e.g., disconnected UI) cancelled — let gRPC surface
            // StatusCode.Cancelled rather than swallowing into success=false.
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "TriggerScan failed");
            // Recoverable failure — return success=false with a structured
            // message so the UI can render the reason inline rather than
            // surfacing a generic RpcException. Mirrors the ApplyFirewallRule
            // "soft-failure" precedent for caller-actionable conditions.
            return new Local.TriggerScanResponse {
                Success = false,
                Message = $"Scan failed: {ex.Message}",
                DevicesObserved = 0,
            };
        }
    }

    /// <summary>
    /// Phase 9.5: sets or clears the user-supplied cosmetic label for a LAN
    /// device. Persists via <see cref="ILanDeviceStore.SetLabelAsync"/> and
    /// broadcasts a <c>LanDeviceLabelChangedEvent</c> so other subscribed
    /// UIs (if any) refresh in real time. Recoverable failures (no such
    /// MAC, label too long, store throws) surface as <c>success=false</c>
    /// with a human-readable message; hard validation errors (empty MAC)
    /// throw <see cref="StatusCode.InvalidArgument"/>; cancellation
    /// propagates. Labels are NOT chain-audited — cosmetic UI state only.
    /// </summary>
    public override async Task<Local.SetLanDeviceLabelResponse> SetLanDeviceLabel(
        Local.SetLanDeviceLabelRequest request, ServerCallContext context
    ) {
        if (string.IsNullOrWhiteSpace(request.Mac)) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "mac is required"));
        }
        // Treat empty / whitespace-only label as "clear the existing label".
        // The store also normalises this, but normalising here too lets us
        // surface a meaningful "cleared" message in the response.
        var normalizedLabel = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label;
        if (normalizedLabel is not null && normalizedLabel.Length > MaxLanDeviceLabelLength) {
            return new Local.SetLanDeviceLabelResponse {
                Success = false,
                Message = $"Label exceeds the {MaxLanDeviceLabelLength}-character limit "
                          + $"(got {normalizedLabel.Length}).",
            };
        }

        try {
            await _lanDeviceStore
                .SetLabelAsync(request.Mac, normalizedLabel, context.CancellationToken)
                .ConfigureAwait(false);
            // Re-fetch so we have a snapshot to broadcast and to confirm the
            // device actually exists (SetLabelAsync is a silent no-op on
            // unknown MAC per the interface contract).
            var refreshed = await _lanDeviceStore
                .GetByMacAsync(request.Mac, context.CancellationToken)
                .ConfigureAwait(false);
            if (refreshed is null) {
                return new Local.SetLanDeviceLabelResponse {
                    Success = false,
                    Message = $"Device not found: {request.Mac}",
                };
            }
            try {
                _broadcaster.BroadcastLanDeviceLabelChanged(refreshed);
            } catch (Exception ex) {
                _logger.LogError(ex, "SetLanDeviceLabel broadcast failed for {Mac}", request.Mac);
                // Broadcast failure does NOT fail the call — the label is
                // already persisted. The UI that initiated the change has the
                // optimistic update; other UIs will catch up on next refresh.
            }
            return new Local.SetLanDeviceLabelResponse {
                Success = true,
                Message = normalizedLabel is null
                    ? "Label cleared."
                    : $"Label updated to \"{normalizedLabel}\".",
            };
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "SetLanDeviceLabel failed for {Mac}", request.Mac);
            return new Local.SetLanDeviceLabelResponse {
                Success = false,
                Message = $"Failed to set label: {ex.Message}",
            };
        }
    }

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
