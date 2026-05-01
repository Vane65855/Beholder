using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Phase 7 detector: emits a <see cref="AlertKind.NewProcess"/> alert the first
/// time a binary accesses the network. Phase 7.5 (per ADR 007) extended the
/// dedup model from "first time per file path" to "first time per logical
/// app at install location," using PE VersionInfo + Authenticode signature
/// metadata supplied by an optional <see cref="IBinaryIdentityProvider"/>.
/// Subscribes to
/// <see cref="IProcessFirstNetworkFlowSource.OnProcessFirstNetworkFlow"/>
/// (the engine's session-scoped fire-once-per-key event).
/// </summary>
/// <remarks>
/// <para>
/// Three-tier dedup walk:
/// </para>
/// <list type="number">
/// <item>Path-based: if the exact path is already in the registry, this
/// is a daemon-restart re-observation — refresh last_seen, no alert.</item>
/// <item>Logical-identity: if the binary has VersionInfo + a valid
/// signature, look up by (CompanyName, ProductName, InstallRoot). Match
/// with same publisher → silent (Squirrel auto-update). Match with
/// different publisher → SPOOF DETECTED, fire HashChanged with
/// publisher-mismatch summary.</item>
/// <item>Genuinely new: register and fire NewProcess.</item>
/// </list>
/// <para>
/// When the identity provider is null (Linux/macOS) or returns null
/// metadata (unsigned binaries, missing VersionInfo), the detector falls
/// back to path-based behavior — current pre-Phase-7.5 semantics
/// preserved unchanged.
/// </para>
/// <para>
/// Errors per event are caught + logged: one binary's failure (filesystem,
/// SQLite, RPC) must not knock the detector offline for everything else.
/// </para>
/// </remarks>
internal sealed class NewProcessDetector : IHostedService {
    private readonly IProcessFirstNetworkFlowSource _flowSource;
    private readonly IProcessRegistry _processRegistry;
    private readonly IAlertEmitter _alertEmitter;
    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NewProcessDetector> _logger;
    private readonly IBinaryIdentityProvider? _identityProvider;

    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _subscribed;

    public NewProcessDetector(
        IProcessFirstNetworkFlowSource flowSource,
        IProcessRegistry processRegistry,
        IAlertEmitter alertEmitter,
        IOptionsMonitor<AlertOptions> options,
        TimeProvider timeProvider,
        ILogger<NewProcessDetector> logger,
        IBinaryIdentityProvider? identityProvider = null
    ) {
        ArgumentNullException.ThrowIfNull(flowSource);
        ArgumentNullException.ThrowIfNull(processRegistry);
        ArgumentNullException.ThrowIfNull(alertEmitter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _flowSource = flowSource;
        _processRegistry = processRegistry;
        _alertEmitter = alertEmitter;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _identityProvider = identityProvider;  // null on Linux/macOS — see ADR 007
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _flowSource.OnProcessFirstNetworkFlow += OnFirstFlow;
        _subscribed = true;
        _logger.LogInformation("NewProcessDetector started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_subscribed) {
            _flowSource.OnProcessFirstNetworkFlow -= OnFirstFlow;
            _subscribed = false;
        }
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
        _logger.LogInformation("NewProcessDetector stopped");
    }

    private void OnFirstFlow(string processPath) {
        // Fire and forget: the engine consumer thread must not block on
        // SQLite + chain I/O. Errors propagate into ProcessAsync's catch.
        _ = Task.Run(() => ProcessAsync(processPath, _shutdownCts.Token));
    }

    /// <summary>
    /// Test seam: synchronously walks the dedup chain so tests can observe
    /// alerts and registry updates without timing on the fire-and-forget
    /// Task spawned by <see cref="OnFirstFlow"/>.
    /// </summary>
    internal async Task ProcessAsync(string processPath, CancellationToken cancellationToken) {
        try {
            if (!_options.CurrentValue.EnableNewProcessDetection) return;

            // Tier 1: path-based dedup catches daemon-restart re-observation.
            var existing = await _processRegistry.GetByPathAsync(processPath, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null) {
                await RefreshLastSeenAsync(existing, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Read identity metadata up front (Windows-only; null on Linux/macOS).
            var identity = _identityProvider is null
                ? null
                : await _identityProvider.ReadIdentityAsync(processPath, cancellationToken).ConfigureAwait(false);
            var installRoot = ResolveInstallRoot(processPath, identity?.ProductName);

            // Tier 2: logical-identity dedup. Only viable when we have all
            // three identity components AND a validated signature.
            if (identity?.CompanyName is { } company
                && identity.ProductName is { } product
                && installRoot is not null
                && identity.Signature is { Status: SignatureValidationStatus.Valid } signature) {

                var sameLogical = await _processRegistry
                    .FindByLogicalIdentityAsync(company, product, installRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (sameLogical is not null) {
                    await HandleLogicalIdentityMatchAsync(
                        processPath, identity, signature, installRoot, sameLogical, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }

            // Tier 3: genuinely new — fire NewProcess and register.
            await EmitNewProcessAsync(processPath, identity, installRoot, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } catch (Exception ex) {
            _logger.LogError(ex,
                "NewProcessDetector failed to process {ProcessPath}", processPath);
        }
    }

    private async Task RefreshLastSeenAsync(ProcessInfo existing, CancellationToken cancellationToken) {
        var refreshed = new ProcessInfo(
            path: existing.Path,
            displayName: existing.DisplayName,
            sha256: existing.Sha256,
            firstSeen: existing.FirstSeen,
            lastSeen: _timeProvider.GetUtcNow(),
            lastHashedAt: existing.LastHashedAt,
            companyName: existing.CompanyName,
            productName: existing.ProductName,
            installRoot: existing.InstallRoot,
            certSubjectCn: existing.CertSubjectCn,
            certIssuerCn: existing.CertIssuerCn,
            signatureStatus: existing.SignatureStatus);
        await _processRegistry.RegisterAsync(refreshed, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleLogicalIdentityMatchAsync(
        string processPath, BinaryIdentity identity, AuthenticodeInfo signature,
        string installRoot, ProcessInfo trusted, CancellationToken cancellationToken
    ) {
        var sigsMatch =
            string.Equals(trusted.CertSubjectCn, signature.SubjectCn, StringComparison.Ordinal)
            && string.Equals(trusted.CertIssuerCn, signature.IssuerCn, StringComparison.Ordinal);

        // Register the new path with full identity regardless of cert match —
        // we want the new path tracked so future flows from it short-circuit
        // on the path-based dedup tier.
        await RegisterAsync(processPath, identity, installRoot, cancellationToken).ConfigureAwait(false);

        if (sigsMatch) {
            // Auto-update at same install location → silent.
            return;
        }

        // Spoof: same logical app at same location, different publisher.
        var summary =
            $"Publisher mismatch for {identity.ProductName}: signed by " +
            $"{signature.SubjectCn} instead of trusted {trusted.CertSubjectCn}";
        await _alertEmitter
            .EmitAlertAsync(AlertKind.HashChanged, processPath, summary, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EmitNewProcessAsync(
        string processPath, BinaryIdentity? identity, string? installRoot, CancellationToken cancellationToken
    ) {
        await RegisterAsync(processPath, identity, installRoot, cancellationToken).ConfigureAwait(false);
        var displayName = ExtractDisplayName(processPath);
        var summary = $"{displayName} accessed the network for the first time";
        await _alertEmitter
            .EmitAlertAsync(AlertKind.NewProcess, processPath, summary, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RegisterAsync(
        string processPath, BinaryIdentity? identity, string? installRoot, CancellationToken cancellationToken
    ) {
        var now = _timeProvider.GetUtcNow();
        var info = new ProcessInfo(
            path: processPath,
            displayName: ExtractDisplayName(processPath),
            sha256: null,
            firstSeen: now,
            lastSeen: now,
            lastHashedAt: null,
            companyName: identity?.CompanyName,
            productName: identity?.ProductName,
            installRoot: installRoot,
            certSubjectCn: identity?.Signature?.SubjectCn,
            certIssuerCn: identity?.Signature?.IssuerCn,
            signatureStatus: identity?.Signature?.Status);
        await _processRegistry.RegisterAsync(info, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks ancestors of <paramref name="processPath"/> and returns the
    /// first ancestor folder whose name matches <paramref name="productName"/>
    /// case-insensitively. Returns null when no ancestor matches OR when
    /// productName is null. The matched folder is the "install root" of the
    /// logical app — see ADR 007.
    /// </summary>
    internal static string? ResolveInstallRoot(string processPath, string? productName) {
        if (string.IsNullOrWhiteSpace(productName)) return null;
        var dir = Path.GetDirectoryName(processPath);
        while (!string.IsNullOrEmpty(dir)) {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, productName, StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Returns the file name component of <paramref name="processPath"/>, or
    /// the path itself when it has no separator (defensive — should never
    /// happen for OS-supplied paths but cheap to handle).
    /// </summary>
    private static string ExtractDisplayName(string processPath) {
        var name = Path.GetFileName(processPath);
        return string.IsNullOrEmpty(name) ? processPath : name;
    }
}
