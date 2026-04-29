using System.Linq;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public partial class FirewallTabViewModelTests {
    private static (FirewallTabViewModel Vm, FakeDaemonClient Client, DaemonStreamSubscriber Subscriber)
    CreateVm(GetSnapshotResponse? snapshot = null,
        ListFirewallRulesResponse? rules = null,
        GetProcessSummariesResponse? summaries = null,
        Func<string, bool>? fileExistsCheck = null) {
        var client = new FakeDaemonClient();
        if (snapshot is not null) client.SnapshotResponse = snapshot;
        if (rules is not null) client.ListFirewallRulesResponse = rules;
        if (summaries is not null) client.ProcessSummariesResponse = summaries;
        var subscriber = new DaemonStreamSubscriber(
            client, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        var processStateService = new ProcessStateService(subscriber, client, TimeProvider.System);
        // Default to "everything exists" for tests that don't care about the
        // executable-exists branching. Tests that DO care pass an explicit
        // predicate via fileExistsCheck.
        var vm = new FirewallTabViewModel(
            client, processStateService, subscriber,
            fileExistsCheck ?? (_ => true));
        return (vm, client, subscriber);
    }

    [Fact]
    public async Task ActivateAsync_NoRulesNoSummaries_ReportsEmpty() {
        var (vm, _, _) = CreateVm();

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.HasRows);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.ActiveRows);
        Assert.Empty(vm.InactiveRows);
    }

    [Fact]
    public async Task ActivateAsync_WithRulesOnly_ProducesInactiveRows() {
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasRows);
        var single = Assert.Single(vm.InactiveRows);
        Assert.Equal(@"C:\bin\curl.exe", single.ProcessPath);
        Assert.Equal(FirewallActionState.Block, single.OutAction);
        Assert.Equal(FirewallActionState.Default, single.InAction);
    }

    [Fact]
    public async Task ActivateAsync_BothInAndOutBlocked_OverallStatusBlocked() {
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Direction.Inbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(vm.InactiveRows);
        Assert.Equal(FirewallRowStatus.Blocked, row.OverallStatus);
        Assert.Equal(1, vm.BlockedProcessCount);
    }

    [Fact]
    public async Task ActivateAsync_PartialBlock_OverallStatusPartial() {
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(vm.InactiveRows);
        Assert.Equal(FirewallRowStatus.Partial, row.OverallStatus);
        Assert.Equal(1, vm.PartialProcessCount);
    }

    [Fact]
    public async Task ActivateAsync_PullsEnforcementStateFromSnapshot() {
        var snapshot = new GetSnapshotResponse { FirewallEnforcementEnabled = false };
        var (vm, _, _) = CreateVm(snapshot: snapshot);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsFirewallEnabled);
    }

    [Fact]
    public async Task ActivateAsync_IsIdempotent() {
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\a.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, client, _) = CreateVm(rules: rules);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        // Second call must not re-issue RPCs — counts should remain identical.
        var firstCount = vm.InactiveRows.Count;
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(firstCount, vm.InactiveRows.Count);
        Assert.Empty(client.ApplyFirewallRuleCalls);
    }

    [Fact]
    public async Task CycleOutPill_FromDefault_CallsApplyWithBlock() {
        // Status-indicator semantics: Default → click → Block. Default reads
        // as ALLOW visually (no rule = OS default allows), so a click is the
        // user saying "block this app's outbound traffic."
        var (vm, client, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = new FirewallRuleRow(@"C:\bin\curl.exe");
        await vm.CycleOutPillCommand.ExecuteAsync(row);

        var call = Assert.Single(client.ApplyFirewallRuleCalls);
        Assert.Equal(@"C:\bin\curl.exe", call.ProcessPath);
        Assert.Equal(Direction.Outbound, call.Direction);
        Assert.Equal(FirewallAction.Block, call.Action);
        Assert.Equal(FirewallActionState.Block, row.OutAction);
    }

    [Fact]
    public async Task CycleOutPill_FromAllow_CallsApplyWithBlock() {
        // Allow is unusual to start from in v1 (UI clicks never produce it),
        // but if a daemon-side or remote path created an explicit Allow rule,
        // a UI click should still flip it to Block — defensive coverage.
        var (vm, client, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = new FirewallRuleRow(@"C:\bin\curl.exe") { OutAction = FirewallActionState.Allow };
        await vm.CycleOutPillCommand.ExecuteAsync(row);

        var call = Assert.Single(client.ApplyFirewallRuleCalls);
        Assert.Equal(FirewallAction.Block, call.Action);
        Assert.Equal(FirewallActionState.Block, row.OutAction);
    }

    [Fact]
    public async Task CycleOutPill_FromBlock_CallsRemove() {
        var (vm, client, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = new FirewallRuleRow(@"C:\bin\curl.exe") { OutAction = FirewallActionState.Block };
        await vm.CycleOutPillCommand.ExecuteAsync(row);

        var call = Assert.Single(client.RemoveFirewallRuleCalls);
        Assert.Equal(@"C:\bin\curl.exe", call.ProcessPath);
        Assert.Equal(Direction.Outbound, call.Direction);
        Assert.Equal(FirewallActionState.Default, row.OutAction);
        Assert.Empty(client.ApplyFirewallRuleCalls);
    }

    [Fact]
    public async Task CycleOutPill_RpcThrows_RevertsState() {
        var (vm, client, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.ApplyFirewallRuleException = new InvalidOperationException("Simulated RPC failure");

        var row = new FirewallRuleRow(@"C:\bin\curl.exe");
        await vm.CycleOutPillCommand.ExecuteAsync(row);

        // Optimistic state was Block (binary toggle from Default); revert
        // puts it back to Default.
        Assert.Equal(FirewallActionState.Default, row.OutAction);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task CycleInPill_FromDefault_CallsApplyInboundWithBlock() {
        var (vm, client, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = new FirewallRuleRow(@"C:\bin\app.exe");
        await vm.CycleInPillCommand.ExecuteAsync(row);

        var call = Assert.Single(client.ApplyFirewallRuleCalls);
        Assert.Equal(Direction.Inbound, call.Direction);
        Assert.Equal(FirewallAction.Block, call.Action);
    }

    [Fact]
    public async Task ToggleEnforcement_FlipsFalseAndCallsRpc() {
        var snapshot = new GetSnapshotResponse { FirewallEnforcementEnabled = true };
        var (vm, client, _) = CreateVm(snapshot: snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        await vm.ToggleEnforcementCommand.ExecuteAsync(null);

        var call = Assert.Single(client.SetFirewallEnabledCalls);
        Assert.False(call.Enabled);
        Assert.False(vm.IsFirewallEnabled);
    }

    [Fact]
    public async Task ToggleEnforcement_RpcFails_RevertsLocalState() {
        var snapshot = new GetSnapshotResponse { FirewallEnforcementEnabled = true };
        var (vm, client, _) = CreateVm(snapshot: snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.SetFirewallEnabledException = new InvalidOperationException("Boom");

        await vm.ToggleEnforcementCommand.ExecuteAsync(null);

        Assert.True(vm.IsFirewallEnabled);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task SearchText_FiltersByDisplayName() {
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\firefox.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\chrome.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.SearchText = "fire";

        var filtered = vm.FilteredInactiveRows.ToList();
        Assert.Single(filtered);
        Assert.Equal("firefox.exe", filtered[0].DisplayName);
    }

    [Fact]
    public async Task SelectedFilter_BlockedExcludesPartial() {
        var rules = new ListFirewallRulesResponse();
        // Both directions blocked.
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\fully-blocked.exe",
            Direction = Direction.Inbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\fully-blocked.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        // One direction blocked.
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\half-blocked.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });

        var (vm, _, _) = CreateVm(rules: rules);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.SelectedFilter = FirewallFilter.Blocked;

        var filtered = vm.FilteredInactiveRows.ToList();
        Assert.Single(filtered);
        Assert.Equal("fully-blocked.exe", filtered[0].DisplayName);
    }

    [Fact]
    public async Task GetSnapshot_ErrorOnLoad_SetsErrorState() {
        var client = new FakeDaemonClient {
            SnapshotException = new InvalidOperationException("Boom"),
        };
        var subscriber = new DaemonStreamSubscriber(
            client, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        var processStateService = new ProcessStateService(subscriber, client, TimeProvider.System);
        var vm = new FirewallTabViewModel(client, processStateService, subscriber);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasError);
        Assert.False(vm.IsLoading);
    }
}
