using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

public partial class FirewallRuleRowTests {
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
}
