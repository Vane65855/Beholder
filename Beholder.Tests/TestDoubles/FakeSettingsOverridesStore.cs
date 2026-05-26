using System.Collections.Concurrent;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="ISettingsOverridesStore"/>. Backed by an
/// in-memory dictionary; <see cref="ThrowOnUpsert"/> can be flipped to
/// simulate persistence failures for RPC-handler error-path tests.
/// </summary>
internal sealed class FakeSettingsOverridesStore : ISettingsOverridesStore {
    private readonly ConcurrentDictionary<string, string> _entries = new();

    public bool ThrowOnUpsert { get; set; }
    public Exception UpsertException { get; set; } = new InvalidOperationException("simulated persistence failure");

    public int UpsertCallCount => _upsertCallCount;
    private int _upsertCallCount;

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.TryGetValue(name, out var value) ? value : null);
    }

    public Task UpsertAsync(string name, string valueJson, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _upsertCallCount);
        if (ThrowOnUpsert) throw UpsertException;
        _entries[name] = valueJson;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ListAllAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(_entries));
    }

    /// <summary>Test helper to pre-populate the store without calling Upsert.</summary>
    public void Seed(string name, string valueJson) {
        _entries[name] = valueJson;
    }
}
