using Beholder.Core;
using Beholder.Daemon.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public sealed class SqliteAppIdentityRuleStoreTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly FakeTimeProvider _timeProvider;
    private readonly SqliteAppIdentityRuleStore _store;

    public SqliteAppIdentityRuleStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _store = new SqliteAppIdentityRuleStore(
            new ConnectionFactory(_databasePath, pooling: false), _timeProvider);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ListAllAsync_EmptyStore_ReturnsEmpty() {
        var rules = await _store.ListAllAsync(CancellationToken.None);

        Assert.Empty(rules);
    }

    [Fact]
    public async Task AddAsync_NewRule_ReturnsRowWithDbAssignedId() {
        var rule = await _store.AddAsync(
            @"C:\Users\Vane\AppData\Local\Discord", "Discord.exe", "Discord",
            CancellationToken.None);

        Assert.NotNull(rule);
        Assert.True(rule.Id > 0);
        Assert.Equal(@"C:\Users\Vane\AppData\Local\Discord", rule.AnchorPath);
        Assert.Equal("Discord.exe", rule.Filename);
        Assert.Equal("Discord", rule.DisplayName);
    }

    [Fact]
    public async Task AddAsync_DuplicateAnchorAndFilename_ReturnsNull() {
        await _store.AddAsync(@"C:\App", "App.exe", null, CancellationToken.None);

        var second = await _store.AddAsync(@"C:\App", "App.exe", "different label",
            CancellationToken.None);

        Assert.Null(second);
        // Verify only one row landed.
        var list = await _store.ListAllAsync(CancellationToken.None);
        Assert.Single(list);
    }

    [Fact]
    public async Task AddAsync_TrailingSeparatorOnAnchor_NormalizedBeforeStorage() {
        var rule = await _store.AddAsync(
            @"C:\Users\Vane\AppData\Local\Discord\", "Discord.exe", null,
            CancellationToken.None);

        Assert.NotNull(rule);
        Assert.Equal(@"C:\Users\Vane\AppData\Local\Discord", rule.AnchorPath);
    }

    [Fact]
    public async Task ListAllAsync_ReturnsRulesInIdOrder() {
        await _store.AddAsync(@"C:\A", "A.exe", null, CancellationToken.None);
        await _store.AddAsync(@"C:\B", "B.exe", null, CancellationToken.None);
        await _store.AddAsync(@"C:\C", "C.exe", null, CancellationToken.None);

        var rules = await _store.ListAllAsync(CancellationToken.None);

        Assert.Equal(3, rules.Count);
        Assert.Equal("A.exe", rules[0].Filename);
        Assert.Equal("B.exe", rules[1].Filename);
        Assert.Equal("C.exe", rules[2].Filename);
    }

    [Fact]
    public async Task RemoveAsync_ExistingId_ReturnsTrueAndDeletes() {
        var rule = await _store.AddAsync(@"C:\App", "App.exe", null, CancellationToken.None);
        Assert.NotNull(rule);

        var removed = await _store.RemoveAsync(rule.Id, CancellationToken.None);

        Assert.True(removed);
        var list = await _store.ListAllAsync(CancellationToken.None);
        Assert.Empty(list);
    }

    [Fact]
    public async Task RemoveAsync_UnknownId_ReturnsFalse() {
        var removed = await _store.RemoveAsync(9999, CancellationToken.None);
        Assert.False(removed);
    }

    [Fact]
    public async Task MatchAsync_NoRules_ReturnsNull() {
        var match = await _store.MatchAsync(
            "Discord.exe", @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task MatchAsync_GrandparentAndFilenameMatch_ReturnsRule() {
        await _store.AddAsync(
            @"C:\Users\Vane\AppData\Local\Discord", "Discord.exe", null,
            CancellationToken.None);

        var match = await _store.MatchAsync(
            "Discord.exe",
            @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(@"C:\Users\Vane\AppData\Local\Discord", match.AnchorPath);
    }

    [Fact]
    public async Task MatchAsync_FilenameMatchesButGrandparentDoesNot_ReturnsNull() {
        await _store.AddAsync(@"C:\App\Discord", "Discord.exe", null, CancellationToken.None);

        var match = await _store.MatchAsync(
            "Discord.exe",
            @"C:\OtherDir\app-1.0.9235\Discord.exe",
            CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task MatchAsync_GrandparentMatchesButFilenameDoesNot_ReturnsNull() {
        await _store.AddAsync(
            @"C:\Users\Vane\AppData\Local\Discord", "Discord.exe", null,
            CancellationToken.None);

        var match = await _store.MatchAsync(
            "Setup.exe",
            @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9235\Setup.exe",
            CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task MatchAsync_FileTooShallow_ReturnsNull() {
        // Zero variable segments — file is DIRECTLY in the anchor.
        await _store.AddAsync(@"C:\App", "App.exe", null, CancellationToken.None);

        var match = await _store.MatchAsync(
            "App.exe", @"C:\App\App.exe", CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task MatchAsync_FileTooDeep_ReturnsNull() {
        // Two variable segments between anchor and file — too deep.
        await _store.AddAsync(@"C:\App", "App.exe", null, CancellationToken.None);

        var match = await _store.MatchAsync(
            "App.exe", @"C:\App\v2\dist\App.exe", CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task MatchAsync_CaseInsensitiveOnWindows() {
        if (!OperatingSystem.IsWindows()) return; // skip on Linux

        await _store.AddAsync(
            @"C:\Users\Vane\AppData\Local\Discord", "Discord.exe", null,
            CancellationToken.None);

        var match = await _store.MatchAsync(
            "discord.exe",
            @"c:\USERS\vane\appdata\local\DISCORD\app-1.0.9235\discord.exe",
            CancellationToken.None);

        Assert.NotNull(match);
    }
}
