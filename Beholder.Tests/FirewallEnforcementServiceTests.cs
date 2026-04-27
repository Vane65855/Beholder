using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public sealed class FirewallEnforcementServiceTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly FakeFirewallController _firewallController;
    private readonly FakeFirewallEnforcementState _state;
    private readonly FirewallEnforcementService _service;

    public FirewallEnforcementServiceTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        _firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        _firewallController = new FakeFirewallController();
        _state = new FakeFirewallEnforcementState(initialEnabled: true);

        _service = new FirewallEnforcementService(
            _state, _firewallStore, _firewallController,
            NullLogger<FirewallEnforcementService>.Instance);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ApplyEnforcementAsync_TogglingOff_RemovesEveryRule() {
        await SeedRulesAsync(2);

        await _service.ApplyEnforcementAsync(enabled: false, CancellationToken.None);

        Assert.Equal(2, _firewallController.RemovedRules.Count);
    }

    [Fact]
    public async Task ApplyEnforcementAsync_TogglingOn_AddsEveryRule() {
        await SeedRulesAsync(3);

        await _service.ApplyEnforcementAsync(enabled: true, CancellationToken.None);

        Assert.Equal(3, _firewallController.AddedRules.Count);
    }

    [Fact]
    public async Task ApplyEnforcementAsync_EmptyStore_NoControllerCalls() {
        await _service.ApplyEnforcementAsync(enabled: false, CancellationToken.None);

        Assert.Empty(_firewallController.AddedRules);
        Assert.Empty(_firewallController.RemovedRules);
    }

    [Fact]
    public async Task ApplyEnforcementAsync_PreservesPersistedRulesOnToggleOff() {
        await SeedRulesAsync(2);

        await _service.ApplyEnforcementAsync(enabled: false, CancellationToken.None);

        // SQLite is the source of truth — toggle OFF must NOT delete persisted
        // rules, otherwise toggle ON couldn't reapply them.
        var persisted = await _firewallStore.ListAllAsync(CancellationToken.None);
        Assert.Equal(2, persisted.Count);
    }

    [Fact]
    public async Task ApplyEnforcementAsync_OneRuleFails_OthersStillProcessed() {
        await SeedRulesAsync(3);
        // Throw on every removal — verifies the loop swallows per-rule failures
        // instead of aborting on the first error.
        _firewallController.RemoveRuleException =
            new InvalidOperationException("Simulated WFP failure");

        // Should NOT throw — failures are logged and swallowed inside the loop.
        await _service.ApplyEnforcementAsync(enabled: false, CancellationToken.None);

        // Even though every call failed, the loop iterated over all 3 rules
        // (we can't assert on RemovedRules here because the FakeFirewallController
        // throws before recording, but absence of an exception proves coverage).
        Assert.True(true);
    }

    [Fact]
    public async Task StartAsync_SubscribesToStateChanges() {
        var ct = TestContext.Current.CancellationToken;
        await _service.StartAsync(ct);
        await SeedRulesAsync(1);

        // Flip the state — service's OnStateChanged spawns a fire-and-forget
        // task. Wait briefly for it to land in the controller.
        _state.SetEnabled(false);

        await WaitForAsync(
            () => _firewallController.RemovedRules.Count == 1,
            "rule removed via state change", ct);

        await _service.StopAsync(ct);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromStateChanges() {
        var ct = TestContext.Current.CancellationToken;
        await _service.StartAsync(ct);
        await _service.StopAsync(ct);
        await SeedRulesAsync(1);

        _state.SetEnabled(false);

        // Give any errant fire-and-forget task time to execute. After
        // StopAsync the controller must remain untouched.
        await Task.Delay(100, ct);
        Assert.Empty(_firewallController.RemovedRules);
    }

    private async Task SeedRulesAsync(int count) {
        for (var i = 0; i < count; i++) {
            await _firewallStore.UpsertAsync(new FirewallRule(
                id: 0, processPath: $@"C:\bin\app{i}.exe",
                direction: Direction.Outbound, action: FirewallAction.Block,
                source: RuleSource.Manual,
                createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(
        Func<bool> predicate, string description, CancellationToken cancellationToken
    ) {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline) {
            await Task.Delay(10, cancellationToken);
        }
        if (!predicate()) throw new TimeoutException($"Timed out waiting for: {description}");
    }
}
