using Beholder.Core;

namespace Beholder.Tests;

/// <summary>
/// In-memory <see cref="IAlertStore"/> for tests that need to satisfy the
/// dependency but don't exercise alert reads themselves. Existing alert RPC
/// tests construct the SQLite store directly; this fake exists so unrelated
/// RPC tests (e.g., LAN scanner ones) can construct
/// <c>BeholderLocalService</c> without dragging in the SQLite boilerplate.
/// </summary>
internal sealed class FakeAlertStore : IAlertStore {
    private readonly Dictionary<long, Alert> _alerts = new();

    public Task<IReadOnlyList<Alert>> GetAlertsAsync(int limit, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        IReadOnlyList<Alert> recent = _alerts.Values
            .OrderByDescending(a => a.Timestamp)
            .Take(limit)
            .ToList();
        return Task.FromResult(recent);
    }

    public Task MarkAlertReadAsync(long seq, DateTimeOffset viewedAt, CancellationToken cancellationToken) {
        if (_alerts.TryGetValue(seq, out var existing) && existing.FirstViewedAt is null) {
            _alerts[seq] = new Alert(
                seq: existing.Seq,
                kind: existing.Kind,
                processPath: existing.ProcessPath,
                summary: existing.Summary,
                timestamp: existing.Timestamp,
                firstViewedAt: viewedAt);
        }
        return Task.CompletedTask;
    }

    /// <summary>Test-only helper: seed an alert directly without going through the chain.</summary>
    public void Seed(Alert alert) {
        ArgumentNullException.ThrowIfNull(alert);
        _alerts[alert.Seq] = alert;
    }
}
