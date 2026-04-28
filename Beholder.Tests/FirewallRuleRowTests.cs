using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

public class FirewallRuleRowTests {
    [Fact]
    public void Constructor_ExtractsDisplayNameFromPath() {
        var row = new FirewallRuleRow(@"C:\Program Files\Mozilla Firefox\firefox.exe");

        Assert.Equal("firefox.exe", row.DisplayName);
    }

    [Fact]
    public void Constructor_NullOrWhitespacePath_Throws() {
        Assert.Throws<ArgumentException>(() => new FirewallRuleRow(""));
        Assert.Throws<ArgumentException>(() => new FirewallRuleRow("   "));
    }

    [Fact]
    public void OverallStatus_BothAllow_ReturnsAllowed() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            InAction = FirewallActionState.Allow,
            OutAction = FirewallActionState.Allow,
        };

        Assert.Equal(FirewallRowStatus.Allowed, row.OverallStatus);
    }

    [Fact]
    public void OverallStatus_BothBlock_ReturnsBlocked() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            InAction = FirewallActionState.Block,
            OutAction = FirewallActionState.Block,
        };

        Assert.Equal(FirewallRowStatus.Blocked, row.OverallStatus);
    }

    [Fact]
    public void OverallStatus_BothDefault_ReturnsAllowed() {
        // Status-indicator semantics: Default (no rule) and Allow both fold
        // into Allowed because the user-visible outcome is identical
        // (the app can connect).
        var row = new FirewallRuleRow(@"C:\app.exe");

        Assert.Equal(FirewallRowStatus.Allowed, row.OverallStatus);
    }

    // [Theory] parameters can't reference the internal enum directly because
    // the test class is public — InlineData uses ints and the test body
    // casts back to FirewallActionState.
    [Theory]
    [InlineData((int)FirewallActionState.Allow, (int)FirewallActionState.Block)]
    [InlineData((int)FirewallActionState.Block, (int)FirewallActionState.Allow)]
    [InlineData((int)FirewallActionState.Block, (int)FirewallActionState.Default)]
    [InlineData((int)FirewallActionState.Default, (int)FirewallActionState.Block)]
    public void OverallStatus_OneDirectionBlocked_ReturnsPartial(int inActionInt, int outActionInt) {
        // Only mixed states where exactly one direction is Block remain
        // Partial. Allow+Default and Default+Allow now fold into Allowed
        // (no Block anywhere = effective full-allow).
        var row = new FirewallRuleRow(@"C:\app.exe") {
            InAction = (FirewallActionState)inActionInt,
            OutAction = (FirewallActionState)outActionInt,
        };

        Assert.Equal(FirewallRowStatus.Partial, row.OverallStatus);
    }

    [Theory]
    [InlineData((int)FirewallActionState.Allow, (int)FirewallActionState.Default)]
    [InlineData((int)FirewallActionState.Default, (int)FirewallActionState.Allow)]
    public void OverallStatus_NoBlock_ReturnsAllowed(int inActionInt, int outActionInt) {
        // Default+Allow combinations have no Block anywhere; the app can
        // connect freely, so the status indicator reads Allowed.
        var row = new FirewallRuleRow(@"C:\app.exe") {
            InAction = (FirewallActionState)inActionInt,
            OutAction = (FirewallActionState)outActionInt,
        };

        Assert.Equal(FirewallRowStatus.Allowed, row.OverallStatus);
    }

    [Theory]
    [InlineData((int)FirewallActionState.Default, (int)FirewallActionState.Block)]
    [InlineData((int)FirewallActionState.Allow, (int)FirewallActionState.Block)]
    [InlineData((int)FirewallActionState.Block, (int)FirewallActionState.Default)]
    public void NextState_BinaryToggle_NonBlockGoesToBlock_BlockGoesToDefault(int currentInt, int expectedInt) {
        // Binary toggle: any non-Block state advances to Block; Block goes
        // back to Default (rule removed). The previous three-state cycle
        // (Allow → Block → Default → Allow) was deprecated when the pill
        // became a status indicator rather than a rule editor.
        Assert.Equal(
            (FirewallActionState)expectedInt,
            FirewallRuleRow.NextState((FirewallActionState)currentInt));
    }

    [Fact]
    public void RecentBytesLabel_Zero_ReturnsDash() {
        var row = new FirewallRuleRow(@"C:\app.exe") { RecentBytesTotal = 0 };

        Assert.Equal("—", row.RecentBytesLabel);
    }

    [Fact]
    public void RecentBytesLabel_NonZero_FormatsAsBytes() {
        var row = new FirewallRuleRow(@"C:\app.exe") { RecentBytesTotal = 2048 };

        Assert.Contains("KB", row.RecentBytesLabel);
    }

    [Fact]
    public void OverallStatus_RaisedWhenInActionChanges() {
        var row = new FirewallRuleRow(@"C:\app.exe");
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        row.InAction = FirewallActionState.Block;

        Assert.Contains(nameof(FirewallRuleRow.OverallStatus), seen);
    }

    [Fact]
    public void OverallStatus_RaisedWhenOutActionChanges() {
        var row = new FirewallRuleRow(@"C:\app.exe");
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        row.OutAction = FirewallActionState.Allow;

        Assert.Contains(nameof(FirewallRuleRow.OverallStatus), seen);
    }

    // ─── Polish-pass tests (B1, B2 from luminous-wishing-map.md) ───

    [Fact]
    public void SourceLabel_DefaultRow_NoRule_ReturnsDefault() {
        // No Beholder rule means the system default applies (Windows allows
        // by default). SOURCE shows "default" to convey that effective state.
        // The em-dash is reserved for the defensive fallback below (genuinely
        // unrecognized RuleSource enum values).
        var row = new FirewallRuleRow(@"C:\app.exe");

        Assert.False(row.HasRule);
        Assert.Equal("default", row.SourceLabel);
    }

    [Fact]
    public void SourceLabel_HasRuleTrue_ReturnsSourceText() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            HasRule = true,
            Source = Beholder.Protocol.Local.RuleSource.Manual,
        };

        Assert.Equal("manual", row.SourceLabel);
    }

    [Fact]
    public void SourceLabel_HasRuleWithDefaultSource_ReturnsDefault() {
        // Future-feature coverage: if/when the daemon produces an explicit
        // RuleSource.Default rule (reserved today), the UI should treat it
        // semantically the same as "no rule" — both mean "system default
        // applies." Pin the contract now so a future change doesn't regress.
        var row = new FirewallRuleRow(@"C:\app.exe") {
            HasRule = true,
            Source = Beholder.Protocol.Local.RuleSource.Default,
        };

        Assert.Equal("default", row.SourceLabel);
    }

    [Fact]
    public void SourceLabel_NotifiesWhenHasRuleChanges() {
        var row = new FirewallRuleRow(@"C:\app.exe");
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        row.HasRule = true;

        Assert.Contains(nameof(FirewallRuleRow.SourceLabel), seen);
    }

    [Fact]
    public void HostsLabel_InactiveRow_ReturnsDash() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            IsActive = false,
            ActiveConnectionCount = 5,  // ignored when inactive
        };

        Assert.Equal("—", row.HostsLabel);
    }

    [Fact]
    public void HostsLabel_ActiveRowZeroCount_ReturnsDash() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            IsActive = true,
            ActiveConnectionCount = 0,
        };

        Assert.Equal("—", row.HostsLabel);
    }

    [Fact]
    public void HostsLabel_ActiveRowWithCount_ReturnsCountString() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            IsActive = true,
            ActiveConnectionCount = 12,
        };

        Assert.Equal("12", row.HostsLabel);
    }

    [Fact]
    public void HostsLabel_NotifiesWhenIsActiveOrCountChanges() {
        var row = new FirewallRuleRow(@"C:\app.exe");
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        row.IsActive = true;
        row.ActiveConnectionCount = 3;

        Assert.Contains(nameof(FirewallRuleRow.HostsLabel), seen);
        // Both transitions should fire — count this distinctly.
        Assert.True(seen.FindAll(n => n == nameof(FirewallRuleRow.HostsLabel)).Count >= 2);
    }

    // ─── Orphaned-rule tests (uninstalled apps with stale rules) ───

    [Fact]
    public void IsOrphanedRule_HasRuleAndExecutableMissing_ReturnsTrue() {
        var row = new FirewallRuleRow(@"C:\uninstalled\app.exe") {
            HasRule = true,
            ExecutableExists = false,
        };

        Assert.True(row.IsOrphanedRule);
    }

    [Fact]
    public void IsOrphanedRule_HasRuleAndExecutableExists_ReturnsFalse() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            HasRule = true,
            ExecutableExists = true,
        };

        Assert.False(row.IsOrphanedRule);
    }

    [Fact]
    public void IsOrphanedRule_NoRuleEvenIfExecutableMissing_ReturnsFalse() {
        // Without a rule the row isn't orphaned — it's just an inactive process
        // that should be filtered out of the visible list entirely (not surfaced
        // with a warning icon). The view-model handles the filter; this test
        // pins the row-level semantics.
        var row = new FirewallRuleRow(@"C:\uninstalled\app.exe") {
            HasRule = false,
            ExecutableExists = false,
        };

        Assert.False(row.IsOrphanedRule);
    }

    [Fact]
    public void IsOrphanedRule_NotifiesWhenHasRuleChanges() {
        var row = new FirewallRuleRow(@"C:\app.exe") { ExecutableExists = false };
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        row.HasRule = true;

        Assert.Contains(nameof(FirewallRuleRow.IsOrphanedRule), seen);
    }

    [Fact]
    public void IsOrphanedRule_NotifiesWhenExecutableExistsChanges() {
        var row = new FirewallRuleRow(@"C:\app.exe") { HasRule = true };
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        row.ExecutableExists = false;

        Assert.Contains(nameof(FirewallRuleRow.IsOrphanedRule), seen);
    }

    [Fact]
    public void ExecutableExists_DefaultsToTrue() {
        // Optimistic default — we don't want a freshly-created row to flash
        // as "missing" before the existence check completes.
        var row = new FirewallRuleRow(@"C:\app.exe");

        Assert.True(row.ExecutableExists);
    }
}
