using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Phase 11.1 hosted service: periodically signs the current chain head
/// with the daemon's persistent Ed25519 keypair and inserts a row into the
/// <c>checkpoint</c> table. The latest checkpoint becomes the anchor that
/// Phase 11.2's verify-from-anchor optimization will use to skip O(n)
/// re-hashing of pre-checkpoint rows.
/// </summary>
/// <remarks>
/// <para>
/// Cadence: <see cref="CheckpointOptions.SigningInterval"/> (default 1 hour)
/// driven by a <see cref="PeriodicTimer"/> using the injected
/// <see cref="TimeProvider"/> — tests advance via <c>FakeTimeProvider</c>.
/// </para>
/// <para>
/// Skip rules: tick is a no-op when (a) the chain is empty (<c>seq=0</c>)
/// or (b) the latest checkpoint already attests the current chain head's
/// seq. Both are normal-running quiet states — the daemon has nothing new
/// to sign. No alert, no warning log.
/// </para>
/// <para>
/// Signed payload format: <c>seq(8) ‖ row_hash(32) ‖ ts_unix_ns(8)</c>
/// big-endian, matching the schema comment in <c>DatabaseInitializer</c>
/// and the verify contract documented in ADR 012.
/// </para>
/// </remarks>
internal sealed class CheckpointSignerService : IHostedService, IDisposable {
    private readonly IEventStore _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ICheckpointKeyProvider _keyProvider;
    private readonly IOptionsMonitor<CheckpointOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckpointSignerService> _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _loopTask;
    private bool _disposed;

    public CheckpointSignerService(
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        ICheckpointKeyProvider keyProvider,
        IOptionsMonitor<CheckpointOptions> options,
        TimeProvider timeProvider,
        ILogger<CheckpointSignerService> logger
    ) {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _checkpointStore = checkpointStore;
        _keyProvider = keyProvider;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _loopTask = Task.Run(() => RunLoopAsync(_shutdownCts.Token), cancellationToken);
        _logger.LogInformation("CheckpointSignerService started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        if (_loopTask is not null) {
            try {
                await _loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // Expected on shutdown.
            }
            _loopTask = null;
        }
        _logger.LogInformation("CheckpointSignerService stopped");
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken) {
        try {
            var interval = _options.CurrentValue.SigningInterval;
            using var timer = new PeriodicTimer(interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                if (!_options.CurrentValue.EnableSigning) continue;
                await SignOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } catch (Exception ex) {
            _logger.LogError(ex, "CheckpointSignerService loop crashed");
        }
    }

    /// <summary>
    /// Test seam: synchronously runs one signing pass. Tests bypass the loop
    /// and call this directly to exercise the skip/sign decision deterministically.
    /// </summary>
    internal async Task SignOnceAsync(CancellationToken cancellationToken) {
        ChainHead? head;
        try {
            head = await _eventStore.TryGetChainHeadAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "CheckpointSignerService: TryGetChainHeadAsync threw");
            return;
        }

        if (head is null) {
            // Empty chain — nothing to attest. Quiet skip.
            return;
        }

        Checkpoint? latest;
        try {
            latest = await _checkpointStore.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "CheckpointSignerService: GetLatestAsync threw");
            return;
        }

        if (latest is not null && latest.Seq >= head.Seq) {
            // Chain head hasn't advanced since the last signed anchor. Quiet skip.
            return;
        }

        // Sign over the SAME timestamp stored in the checkpoint row. The signed
        // payload and the persisted ts_unix_ns column must agree — the verifier
        // reconstructs the payload from the stored row, so signing over any
        // other value (e.g. the head row's own timestamp) makes verification
        // fail and the anchor silently degrade to a full walk every time.
        var signingTime = _timeProvider.GetUtcNow();
        var signedPayload = CheckpointSignaturePayload.Build(
            head.Seq, head.RowHash, signingTime.ToUnixTimeMilliseconds() * 1_000_000L);
        byte[] signature;
        string keyId;
        try {
            signature = _keyProvider.Sign(signedPayload);
            keyId = _keyProvider.KeyId;
        } catch (Exception ex) {
            _logger.LogError(ex, "CheckpointSignerService: signing failed at seq {Seq}", head.Seq);
            return;
        }

        var checkpoint = new Checkpoint(
            Seq: head.Seq,
            RowHash: head.RowHash,
            Timestamp: signingTime,
            Signature: signature,
            KeyId: keyId);

        try {
            await _checkpointStore.AppendAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                "Signed checkpoint at seq {Seq} (keyId={KeyId})", head.Seq, keyId);
        } catch (Exception ex) {
            _logger.LogError(ex,
                "CheckpointSignerService: failed to append checkpoint at seq {Seq}", head.Seq);
        }
    }
}
