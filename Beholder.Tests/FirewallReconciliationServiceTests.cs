using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

/// <summary>
/// Covers the firewall reconciliation diff (Phase 12.5): the OS Beholder rules are
/// made to match the store (or emptied when enforcement is off), and only the rules
/// actually changed are appended to the chain. The diff is exercised directly via
/// <see cref="FirewallReconciliationService.ReconcileAsync"/>; the periodic timer
/// that drives it is exercised with a <see cref="FakeTimeProvider"/>. The live COM
/// reconciliation is a manual smoke test, like the WFP/ETW work.
/// </summary>
public class FirewallReconciliationServiceTests {
    private static FirewallRule Rule(string path, Direction direction, FirewallAction action) =>
        new(id: 0, processPath: path, direction: direction, action: action,
            source: RuleSource.Manual,
            createdAt: DateTimeOffset.UnixEpoch, updatedAt: DateTimeOffset.UnixEpoch);

    private static (FirewallReconciliationService Service, FakeFirewallController Controller,
        FakeFirewallRuleStore Store, FakeEventStore Events, FakeTimeProvider Time) Build(
        bool enabled, int intervalMinutes = 5) {
        var controller = new FakeFirewallController();
        var store = new FakeFirewallRuleStore();
        var events = new FakeEventStore();
        var time = new FakeTimeProvider();
        var options = Options.Create(new FirewallOptions { ReconcileIntervalMinutes = intervalMinutes });
        var service = new FirewallReconciliationService(
            store, controller, new FakeFirewallEnforcementState(enabled), events,
            options, time, NullLogger<FirewallReconciliationService>.Instance);
        return (service, controller, store, events, time);
    }

    private static async Task WaitForAsync(Func<bool> condition, string because) {
        for (var attempt = 0; attempt < 200; attempt++) {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {because}");
    }

    [Fact]
    public async Task Reconcile_OsAlreadyInSync_MakesNoChangesAndLogsNoEvents() {
        var (service, controller, store, events, _) = Build(enabled: true);
        var rule = Rule(@"C:\app.exe", Direction.Outbound, FirewallAction.Block);
        await store.UpsertAsync(rule, default);
        controller.OsRules.Add(rule);

        await service.ReconcileAsync(default);

        Assert.Empty(controller.AddedRules);
        Assert.Empty(controller.RemovedRules);
        Assert.Empty(events.Appended);
    }

    [Fact]
    public async Task Reconcile_DbRuleMissingFromOs_ReAppliesAndChainLogsCreated() {
        var (service, controller, store, events, _) = Build(enabled: true);
        await store.UpsertAsync(Rule(@"C:\app.exe", Direction.Outbound, FirewallAction.Block), default);

        await service.ReconcileAsync(default);

        var added = Assert.Single(controller.AddedRules);
        Assert.Equal(@"C:\app.exe", added.ProcessPath);
        Assert.Single(events.Appended, e => e.Kind == EventKind.FirewallRuleCreated);
    }

    [Fact]
    public async Task Reconcile_OrphanOsRule_RemovesAndChainLogsRemoved() {
        var (service, controller, store, events, _) = Build(enabled: true);
        controller.OsRules.Add(Rule(@"C:\stray.exe", Direction.Inbound, FirewallAction.Allow));

        await service.ReconcileAsync(default);

        var removed = Assert.Single(controller.RemovedRules);
        Assert.Equal(@"C:\stray.exe", removed.ProcessPath);
        Assert.Single(events.Appended, e => e.Kind == EventKind.FirewallRuleRemoved);
    }

    [Fact]
    public async Task Reconcile_ActionMismatch_ReAppliesAndChainLogsChanged() {
        var (service, controller, store, events, _) = Build(enabled: true);
        await store.UpsertAsync(Rule(@"C:\app.exe", Direction.Outbound, FirewallAction.Block), default);
        controller.OsRules.Add(Rule(@"C:\app.exe", Direction.Outbound, FirewallAction.Allow));

        await service.ReconcileAsync(default);

        var added = Assert.Single(controller.AddedRules);
        Assert.Equal(FirewallAction.Block, added.Action);
        Assert.Empty(controller.RemovedRules);
        Assert.Single(events.Appended, e => e.Kind == EventKind.FirewallRuleChanged);
    }

    [Fact]
    public async Task Reconcile_EnforcementDisabled_RemovesOsRulesButKeepsStore() {
        var (service, controller, store, events, _) = Build(enabled: false);
        var rule = Rule(@"C:\app.exe", Direction.Outbound, FirewallAction.Block);
        await store.UpsertAsync(rule, default);
        controller.OsRules.Add(rule);

        await service.ReconcileAsync(default);

        Assert.Single(controller.RemovedRules);
        Assert.Empty(controller.AddedRules);
        Assert.Single(events.Appended, e => e.Kind == EventKind.FirewallRuleRemoved);
        Assert.Single(await store.ListAllAsync(default));
    }

    [Fact]
    public async Task Reconcile_OsEnumerationFails_DoesNotThrowOrChange() {
        var (service, controller, store, events, _) = Build(enabled: true);
        await store.UpsertAsync(Rule(@"C:\app.exe", Direction.Outbound, FirewallAction.Block), default);
        controller.ListRulesException = new InvalidOperationException("COM boom");

        await service.ReconcileAsync(default);

        Assert.Empty(controller.AddedRules);
        Assert.Empty(controller.RemovedRules);
        Assert.Empty(events.Appended);
    }

    [Fact]
    public async Task ExecuteAsync_AfterOneInterval_ReconcilesAgain() {
        var (service, controller, _, _, time) = Build(enabled: true, intervalMinutes: 5);
        controller.OsRules.Add(Rule(@"C:\stray.exe", Direction.Inbound, FirewallAction.Allow));

        await service.StartAsync(default);
        await WaitForAsync(() => controller.ListCallCount >= 1, "the startup reconciliation pass");
        await Task.Delay(50);

        time.Advance(TimeSpan.FromMinutes(5));

        await WaitForAsync(() => controller.ListCallCount >= 2, "a reconciliation pass after one interval");
        await service.StopAsync(default);
    }

    [Fact]
    public async Task ExecuteAsync_IntervalZero_ReconcilesOnceThenStaysIdle() {
        var (service, controller, _, _, time) = Build(enabled: true, intervalMinutes: 0);

        await service.StartAsync(default);
        await WaitForAsync(() => controller.ListCallCount >= 1, "the startup reconciliation pass");

        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100);

        Assert.Equal(1, controller.ListCallCount);
        await service.StopAsync(default);
    }
}
