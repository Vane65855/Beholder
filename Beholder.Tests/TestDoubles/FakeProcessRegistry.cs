using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IProcessRegistry"/> for detector tests. Mirrors the
/// SQLite implementation's contract: <see cref="RegisterAsync"/> upserts on
/// path; <see cref="GetByPathAsync"/> returns null for unknown paths;
/// <see cref="ListAllAsync"/> returns every registered entry in insertion
/// order (the production store orders by last_seen DESC, but no tests
/// depend on that ordering today).
/// </summary>
internal sealed class FakeProcessRegistry : IProcessRegistry {
    private readonly Dictionary<string, ProcessInfo> _store =
        new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ProcessInfo> Snapshot => _store;

    public Task<ProcessInfo?> GetByPathAsync(string path, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _store.TryGetValue(path, out var info);
        return Task.FromResult(info);
    }

    public Task RegisterAsync(ProcessInfo info, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(info);
        _store[info.Path] = info;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProcessInfo>> ListAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProcessInfo>>(_store.Values.ToList());

    public Task<ProcessInfo?> FindByLogicalIdentityAsync(
        string companyName, string productName, string installRoot,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        foreach (var info in _store.Values) {
            if (string.Equals(info.CompanyName, companyName, StringComparison.Ordinal)
                && string.Equals(info.ProductName, productName, StringComparison.Ordinal)
                && string.Equals(info.InstallRoot, installRoot, StringComparison.OrdinalIgnoreCase)) {
                return Task.FromResult<ProcessInfo?>(info);
            }
        }
        return Task.FromResult<ProcessInfo?>(null);
    }
}
