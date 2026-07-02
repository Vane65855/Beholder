using System;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;

namespace Beholder.Ui.Services;

internal interface IDaemonClient : IAsyncDisposable {
    ConnectionState State { get; }
    DaemonStatusInfo StatusInfo { get; }
    event Action<DaemonStatusInfo>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<GetSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<ApplyFirewallRuleResponse> ApplyFirewallRuleAsync(ApplyFirewallRuleRequest request, CancellationToken cancellationToken);
    Task<RemoveFirewallRuleResponse> RemoveFirewallRuleAsync(RemoveFirewallRuleRequest request, CancellationToken cancellationToken);
    Task<ListFirewallRulesResponse> ListFirewallRulesAsync(ListFirewallRulesRequest request, CancellationToken cancellationToken);
    Task<SetFirewallEnabledResponse> SetFirewallEnabledAsync(SetFirewallEnabledRequest request, CancellationToken cancellationToken);
    Task<MarkAlertReadResponse> MarkAlertReadAsync(MarkAlertReadRequest request, CancellationToken cancellationToken);
    Task<VerifyChainResponse> VerifyChainAsync(VerifyChainRequest request, CancellationToken cancellationToken);
    Task<ExportChainResponse> ExportChainAsync(ExportChainRequest request, CancellationToken cancellationToken);
    Task<GetProcessTimelineResponse> GetProcessTimelineAsync(GetProcessTimelineRequest request, CancellationToken cancellationToken);
    Task<GetAggregateTimelineResponse> GetAggregateTimelineAsync(GetAggregateTimelineRequest request, CancellationToken cancellationToken);
    Task<GetProcessDestinationsResponse> GetProcessDestinationsAsync(GetProcessDestinationsRequest request, CancellationToken cancellationToken);
    Task<GetCountryBreakdownResponse> GetCountryBreakdownAsync(GetCountryBreakdownRequest request, CancellationToken cancellationToken);
    Task<GetProtocolBreakdownResponse> GetProtocolBreakdownAsync(GetProtocolBreakdownRequest request, CancellationToken cancellationToken);
    Task<GetProcessSummariesResponse> GetProcessSummariesAsync(GetProcessSummariesRequest request, CancellationToken cancellationToken);
    Task<GetFirewallActivityResponse> GetFirewallActivityAsync(GetFirewallActivityRequest request, CancellationToken cancellationToken);
    Task<ListLanDevicesResponse> ListLanDevicesAsync(ListLanDevicesRequest request, CancellationToken cancellationToken);
    Task<TriggerScanResponse> TriggerScanAsync(TriggerScanRequest request, CancellationToken cancellationToken);
    Task<SetLanDeviceLabelResponse> SetLanDeviceLabelAsync(SetLanDeviceLabelRequest request, CancellationToken cancellationToken);
    Task<GetStorageStatsResponse> GetStorageStatsAsync(GetStorageStatsRequest request, CancellationToken cancellationToken);
    Task<GetSettingsResponse> GetSettingsAsync(GetSettingsRequest request, CancellationToken cancellationToken);
    Task<SetRecordingSettingsResponse> SetRecordingSettingsAsync(SetRecordingSettingsRequest request, CancellationToken cancellationToken);
    Task<SetHostnameResolutionSettingsResponse> SetHostnameResolutionSettingsAsync(SetHostnameResolutionSettingsRequest request, CancellationToken cancellationToken);
    Task<SetAlertSettingsResponse> SetAlertSettingsAsync(SetAlertSettingsRequest request, CancellationToken cancellationToken);
    Task<SetScannerSettingsResponse> SetScannerSettingsAsync(SetScannerSettingsRequest request, CancellationToken cancellationToken);
    Task<SetTotalsSettingsResponse> SetTotalsSettingsAsync(SetTotalsSettingsRequest request, CancellationToken cancellationToken);
    Task<AddAppIdentityRuleResponse> AddAppIdentityRuleAsync(AddAppIdentityRuleRequest request, CancellationToken cancellationToken);
    Task<RemoveAppIdentityRuleResponse> RemoveAppIdentityRuleAsync(RemoveAppIdentityRuleRequest request, CancellationToken cancellationToken);
    Task<ListAppIdentityRulesResponse> ListAppIdentityRulesAsync(ListAppIdentityRulesRequest request, CancellationToken cancellationToken);
    Grpc.Core.AsyncServerStreamingCall<DaemonEvent> Subscribe(CancellationToken cancellationToken);
}
