using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class SqliteFirewallRuleStoreTests : IDisposable {
    private static readonly DateTimeOffset DefaultTimestamp = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly SqliteFirewallRuleStore _store;

    public SqliteFirewallRuleStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _store = new SqliteFirewallRuleStore(new ConnectionFactory(_databasePath, pooling: false));
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_NullConnectionFactory_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new SqliteFirewallRuleStore(null!));
    }

    [Fact]
    public async Task GetByProcessAndDirectionAsync_NullProcessPath_ThrowsArgumentNullException() {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.GetByProcessAndDirectionAsync(null!, Direction.Outbound, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertAsync_NullRule_ThrowsArgumentNullException() {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.UpsertAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_NullProcessPath_ThrowsArgumentNullException() {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _store.RemoveAsync(null!, Direction.Outbound, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertAsync_NewRule_InsertsAndReturnsWithId() {
        var rule = MakeRule();

        var stored = await _store.UpsertAsync(rule, CancellationToken.None);

        Assert.True(stored.Id > 0);
        Assert.Equal(rule.ProcessPath, stored.ProcessPath);
        Assert.Equal(rule.Direction, stored.Direction);
        Assert.Equal(rule.Action, stored.Action);
        Assert.Equal(rule.Source, stored.Source);
        Assert.Equal(rule.CreatedAt, stored.CreatedAt);
        Assert.Equal(rule.UpdatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRule_UpdatesActionSourceAndTimestamp() {
        var initial = MakeRule(
            action: FirewallAction.Allow,
            source: RuleSource.Manual,
            updatedAt: DefaultTimestamp);
        var first = await _store.UpsertAsync(initial, CancellationToken.None);

        var laterTimestamp = DefaultTimestamp.AddHours(2);
        var updated = MakeRule(
            action: FirewallAction.Block,
            source: RuleSource.Default,
            updatedAt: laterTimestamp);
        var second = await _store.UpsertAsync(updated, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(FirewallAction.Block, second.Action);
        Assert.Equal(RuleSource.Default, second.Source);
        Assert.Equal(laterTimestamp, second.UpdatedAt);
    }

    [Fact]
    public async Task GetByProcessAndDirectionAsync_Exists_ReturnsRule() {
        var stored = await _store.UpsertAsync(MakeRule(), CancellationToken.None);

        var fetched = await _store.GetByProcessAndDirectionAsync("/usr/bin/curl", Direction.Outbound, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(stored.Id, fetched.Id);
        Assert.Equal(stored.ProcessPath, fetched.ProcessPath);
        Assert.Equal(stored.Direction, fetched.Direction);
        Assert.Equal(stored.Action, fetched.Action);
        Assert.Equal(stored.Source, fetched.Source);
        Assert.Equal(stored.CreatedAt, fetched.CreatedAt);
        Assert.Equal(stored.UpdatedAt, fetched.UpdatedAt);
    }

    [Fact]
    public async Task GetByProcessAndDirectionAsync_NotFound_ReturnsNull() {
        var fetched = await _store.GetByProcessAndDirectionAsync("/nonexistent", Direction.Outbound, CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task ListAllAsync_MultipleRules_ReturnsAll() {
        await _store.UpsertAsync(MakeRule(processPath: "/usr/bin/curl"), CancellationToken.None);
        await _store.UpsertAsync(MakeRule(processPath: "/usr/bin/wget"), CancellationToken.None);
        await _store.UpsertAsync(MakeRule(processPath: "/usr/bin/ssh"), CancellationToken.None);

        var rules = await _store.ListAllAsync(CancellationToken.None);

        Assert.Equal(3, rules.Count);
        var paths = rules.Select(r => r.ProcessPath).ToHashSet();
        Assert.Contains("/usr/bin/curl", paths);
        Assert.Contains("/usr/bin/wget", paths);
        Assert.Contains("/usr/bin/ssh", paths);
    }

    [Fact]
    public async Task ListAllAsync_EmptyTable_ReturnsEmptyList() {
        var rules = await _store.ListAllAsync(CancellationToken.None);

        Assert.NotNull(rules);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task ListAllAsync_ReturnsRulesOrderedByIdAscending() {
        var first = await _store.UpsertAsync(MakeRule(processPath: "/usr/bin/curl"), CancellationToken.None);
        var second = await _store.UpsertAsync(MakeRule(processPath: "/usr/bin/wget"), CancellationToken.None);
        var third = await _store.UpsertAsync(MakeRule(processPath: "/usr/bin/ssh"), CancellationToken.None);

        var rules = await _store.ListAllAsync(CancellationToken.None);

        Assert.Equal(3, rules.Count);
        Assert.Equal(first.Id, rules[0].Id);
        Assert.Equal(second.Id, rules[1].Id);
        Assert.Equal(third.Id, rules[2].Id);
        Assert.True(rules[0].Id < rules[1].Id);
        Assert.True(rules[1].Id < rules[2].Id);
    }

    [Fact]
    public async Task RemoveAsync_Exists_ReturnsTrueAndDeletes() {
        await _store.UpsertAsync(MakeRule(), CancellationToken.None);

        var removed = await _store.RemoveAsync("/usr/bin/curl", Direction.Outbound, CancellationToken.None);

        Assert.True(removed);
        var fetched = await _store.GetByProcessAndDirectionAsync("/usr/bin/curl", Direction.Outbound, CancellationToken.None);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task RemoveAsync_NotFound_ReturnsFalse() {
        var removed = await _store.RemoveAsync("/nonexistent", Direction.Outbound, CancellationToken.None);

        Assert.False(removed);
    }

    [Fact]
    public async Task UpsertAsync_SameProcessDifferentDirections_AreSeparateRules() {
        var inbound = await _store.UpsertAsync(MakeRule(direction: Direction.Inbound), CancellationToken.None);
        var outbound = await _store.UpsertAsync(MakeRule(direction: Direction.Outbound), CancellationToken.None);

        var rules = await _store.ListAllAsync(CancellationToken.None);

        Assert.Equal(2, rules.Count);
        Assert.NotEqual(inbound.Id, outbound.Id);
        var directions = rules.Select(r => r.Direction).ToHashSet();
        Assert.Contains(Direction.Inbound, directions);
        Assert.Contains(Direction.Outbound, directions);
    }

    [Fact]
    public async Task UpsertAsync_PreservesCreatedAt_OnUpdate() {
        var originalCreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var attemptedNewCreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await _store.UpsertAsync(MakeRule(createdAt: originalCreatedAt), CancellationToken.None);
        await _store.UpsertAsync(MakeRule(createdAt: attemptedNewCreatedAt), CancellationToken.None);

        var fetched = await _store.GetByProcessAndDirectionAsync("/usr/bin/curl", Direction.Outbound, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(originalCreatedAt, fetched.CreatedAt);
    }

    private static FirewallRule MakeRule(
        string processPath = "/usr/bin/curl",
        Direction direction = Direction.Outbound,
        FirewallAction action = FirewallAction.Allow,
        RuleSource source = RuleSource.Manual,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null
    ) => new FirewallRule(
        id: 0,
        processPath: processPath,
        direction: direction,
        action: action,
        source: source,
        createdAt: createdAt ?? DefaultTimestamp,
        updatedAt: updatedAt ?? DefaultTimestamp
    );
}
