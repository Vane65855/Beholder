using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory test double for <see cref="IAppIdentityRuleStore"/>. Mirrors the
/// production store's depth-1 grandparent-equality match semantics
/// (case-insensitive on Windows) without hitting SQLite. Includes a
/// <see cref="ThrowOnAdd"/> hook so RPC tests can drive the persistence-
/// soft-failure path.
/// </summary>
internal sealed class FakeAppIdentityRuleStore : IAppIdentityRuleStore {
    private readonly List<AppIdentityRule> _rules = [];
    private int _nextId = 1;

    public bool ThrowOnAdd { get; set; }
    public Exception AddException { get; set; } = new InvalidOperationException("simulated");

    public Task<AppIdentityRule?> AddAsync(
        string anchorPath, string filename, string? displayName, CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnAdd) throw AddException;
        var normalizedAnchor = anchorPath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = _rules.FirstOrDefault(r =>
            r.AnchorPath.Equals(normalizedAnchor, StringComparison.OrdinalIgnoreCase)
            && r.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return Task.FromResult<AppIdentityRule?>(null);

        var rule = new AppIdentityRule(
            Id: _nextId++,
            AnchorPath: normalizedAnchor,
            Filename: filename,
            DisplayName: string.IsNullOrEmpty(displayName) ? null : displayName,
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(0)); // tests can override via direct seed
        _rules.Add(rule);
        return Task.FromResult<AppIdentityRule?>(rule);
    }

    public Task<bool> RemoveAsync(int id, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _rules.FindIndex(r => r.Id == id);
        if (index < 0) return Task.FromResult(false);
        _rules.RemoveAt(index);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AppIdentityRule>> ListAllAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AppIdentityRule>>(
            _rules.OrderBy(r => r.Id).ToList());
    }

    public Task<AppIdentityRule?> MatchAsync(
        string filename, string fullPath, CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)) return Task.FromResult<AppIdentityRule?>(null);
        var grandparent = Path.GetDirectoryName(parent);
        if (string.IsNullOrEmpty(grandparent)) return Task.FromResult<AppIdentityRule?>(null);
        var normalizedGrandparent = grandparent.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var match = _rules.FirstOrDefault(r =>
            r.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase)
            && normalizedGrandparent.Equals(r.AnchorPath, pathComparison));
        return Task.FromResult<AppIdentityRule?>(match);
    }

    /// <summary>Test helper: pre-populate without going through AddAsync (skips ThrowOnAdd).</summary>
    public AppIdentityRule Seed(string anchorPath, string filename, string? displayName = null) {
        var rule = new AppIdentityRule(
            Id: _nextId++,
            AnchorPath: anchorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Filename: filename,
            DisplayName: string.IsNullOrEmpty(displayName) ? null : displayName,
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(0));
        _rules.Add(rule);
        return rule;
    }
}
