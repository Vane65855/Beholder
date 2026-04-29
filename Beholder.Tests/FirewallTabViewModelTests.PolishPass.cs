using Beholder.Protocol.Local;
using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

public partial class FirewallTabViewModelTests {
    [Fact]
    public void Constructor_DefaultsActiveExpandedTrue_InactiveExpandedFalse() {
        var (vm, _, _) = CreateVm();

        Assert.True(vm.IsActiveExpanded);
        Assert.False(vm.IsInactiveExpanded);
    }

    [Fact]
    public async Task ToggleInactiveExpanded_FlipsFlag() {
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.ToggleInactiveExpandedCommand.Execute(null);

        Assert.True(vm.IsInactiveExpanded);

        vm.ToggleInactiveExpandedCommand.Execute(null);
        Assert.False(vm.IsInactiveExpanded);
    }

    [Fact]
    public async Task ToggleActiveExpanded_FlipsFlag() {
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.ToggleActiveExpandedCommand.Execute(null);

        Assert.False(vm.IsActiveExpanded);
    }

    [Fact]
    public async Task ActivateAsync_FiltersSystemPseudoProcessFromRules() {
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = "System",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\app.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        // Only the real process — System is filtered.
        var single = Assert.Single(vm.InactiveRows);
        Assert.Equal(@"C:\bin\app.exe", single.ProcessPath);
    }

    [Fact]
    public async Task ActivateAsync_FiltersSystemFromSummaries() {
        var summaries = new GetProcessSummariesResponse();
        summaries.Summaries.Add(new ProcessTrafficSummaryProto {
            ProcessPath = "System",
            ProcessName = "System",
            TotalBytesIn = 100,
            TotalBytesOut = 100,
        });
        var (vm, _, _) = CreateVm(summaries: summaries);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(vm.InactiveRows);
        Assert.Empty(vm.ActiveRows);
    }

    [Fact]
    public async Task ApplyRuleToRow_SetsHasRuleTrue() {
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\app.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(vm.InactiveRows);
        Assert.True(row.HasRule);
        Assert.Equal("MANUAL", row.SourceLabel);
    }

    [Fact]
    public async Task ActivateAsync_NoRules_RowsHaveHasRuleFalse() {
        var summaries = new GetProcessSummariesResponse();
        summaries.Summaries.Add(new ProcessTrafficSummaryProto {
            ProcessPath = @"C:\bin\app.exe",
            ProcessName = "app.exe",
            TotalBytesIn = 100,
            TotalBytesOut = 100,
        });
        var (vm, _, _) = CreateVm(summaries: summaries);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(vm.InactiveRows);
        Assert.False(row.HasRule);
        // No Beholder rule = system default applies = SOURCE shows "DEFAULT".
        Assert.Equal("DEFAULT", row.SourceLabel);
    }
}
