using Beholder.Protocol.Local;

namespace Beholder.Tests;

// ProcessPathResolver in Beholder.Daemon.Windows emits ("unknown",
// "unknown") when Process.GetProcessById throws — typically because the
// process exited between the ETW kernel callback and the resolver's PID
// lookup. Such rows are non-actionable (Windows Firewall rules require an
// absolute path) and the bytes total under this key is the meaningless
// aggregate of every unresolvable PID's traffic. IsExcludedProcess
// filters them at every entry point.

public partial class FirewallTabViewModelTests {
    [Fact]
    public async Task ActivateAsync_FiltersUnknownProcessFromRules() {
        // Mirrors ActivateAsync_FiltersSystemPseudoProcessFromRules. Defensive:
        // INetFwPolicy2 won't accept a rule against the literal path "unknown"
        // anyway, but the filter is robust regardless of how the rule got into
        // the daemon's rule store.
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = "unknown",
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

        // Only the real process — "unknown" is filtered.
        var single = Assert.Single(vm.InactiveRows);
        Assert.Equal(@"C:\bin\app.exe", single.ProcessPath);
    }

    [Fact]
    public async Task ActivateAsync_FiltersUnknownFromSummaries() {
        // Mirrors ActivateAsync_FiltersSystemFromSummaries. This is the
        // GetProcessSummaries entry path — historical totals from
        // ProcessTrafficSummaryProto. The daemon will emit summaries keyed by
        // "unknown" if ETW counter aggregation ever attributed bytes to PIDs
        // the resolver couldn't map.
        var summaries = new GetProcessSummariesResponse();
        summaries.Summaries.Add(new ProcessTrafficSummaryProto {
            ProcessPath = "unknown",
            ProcessName = "unknown",
            TotalBytesIn = 500_000,
            TotalBytesOut = 400_000,
        });
        var (vm, _, _) = CreateVm(summaries: summaries);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(vm.InactiveRows);
        Assert.Empty(vm.ActiveRows);
    }

    [Fact]
    public async Task ActivateAsync_DoesNotFilterPathContainingUnknown() {
        // Scope-pinning test: only the literal sentinel "unknown" is filtered.
        // Real applications whose filename happens to contain the substring —
        // e.g., a deliberately-named "unknown.exe", a publisher folder named
        // "unknown-publisher", etc. — must appear normally. The filter
        // compares the full path with Ordinal equality (string.Equals, not
        // Contains/EndsWith) precisely to make this distinction; this test
        // guards against an accidental refactor that broadens the match.
        //
        // Substituted in for the planned OnProcessStatesUpdated_FiltersUnknownLivePath:
        // driving Dispatcher.UIThread.Post in xunit without an Avalonia
        // dispatcher loop is infeasible (the existing
        // ActivateAsync_ActiveProcessWithMissingFile_StillShownAsActive test
        // acknowledges the same constraint). Pinning the filter's *scope* is
        // more valuable regression coverage than re-asserting the same
        // string-equality outcome through a third entry path.
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\unknown.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\unknown-publisher\app.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        // Both paths contain the substring "unknown" but neither is the
        // sentinel itself; both must survive the filter.
        Assert.Equal(2, vm.InactiveRows.Count);
        Assert.Contains(vm.InactiveRows, r => r.ProcessPath == @"C:\bin\unknown.exe");
        Assert.Contains(vm.InactiveRows, r => r.ProcessPath == @"C:\unknown-publisher\app.exe");
    }
}
