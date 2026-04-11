using Beholder.Daemon.Pipeline;
using Grpc.Core;
using Local = Beholder.Protocol.Local;

namespace Beholder.Daemon.Grpc;

/// <summary>
/// Server-side implementation of the <c>beholder.local.BeholderLocal</c> gRPC
/// service. Only the streaming <see cref="Subscribe"/> RPC is wired in
/// Phase 4.2; the four unary RPCs return <see cref="StatusCode.Unimplemented"/>
/// and will be filled in by subsequent prompts.
/// </summary>
internal sealed class BeholderLocalService : Local.BeholderLocal.BeholderLocalBase {
    private readonly BroadcastService _broadcaster;
    private readonly ILogger<BeholderLocalService> _logger;

    public BeholderLocalService(
        BroadcastService broadcaster,
        ILogger<BeholderLocalService> logger
    ) {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(logger);
        _broadcaster = broadcaster;
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

    public override Task<Local.GetSnapshotResponse> GetSnapshot(
        Local.GetSnapshotRequest request, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "GetSnapshot lands in Phase 4.3"));

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
