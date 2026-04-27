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
    public void OverallStatus_BothDefault_ReturnsDefault() {
        var row = new FirewallRuleRow(@"C:\app.exe");

        Assert.Equal(FirewallRowStatus.Default, row.OverallStatus);
    }

    // [Theory] parameters can't reference the internal enum directly because
    // the test class is public — InlineData uses ints and the test body
    // casts back to FirewallActionState.
    [Theory]
    [InlineData((int)FirewallActionState.Allow, (int)FirewallActionState.Block)]
    [InlineData((int)FirewallActionState.Block, (int)FirewallActionState.Allow)]
    [InlineData((int)FirewallActionState.Block, (int)FirewallActionState.Default)]
    [InlineData((int)FirewallActionState.Default, (int)FirewallActionState.Block)]
    [InlineData((int)FirewallActionState.Allow, (int)FirewallActionState.Default)]
    [InlineData((int)FirewallActionState.Default, (int)FirewallActionState.Allow)]
    public void OverallStatus_MixedDirections_ReturnsPartial(int inActionInt, int outActionInt) {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            InAction = (FirewallActionState)inActionInt,
            OutAction = (FirewallActionState)outActionInt,
        };

        Assert.Equal(FirewallRowStatus.Partial, row.OverallStatus);
    }

    [Theory]
    [InlineData((int)FirewallActionState.Allow, (int)FirewallActionState.Block)]
    [InlineData((int)FirewallActionState.Block, (int)FirewallActionState.Default)]
    [InlineData((int)FirewallActionState.Default, (int)FirewallActionState.Allow)]
    public void NextState_FollowsAllowBlockDefaultCycle(int currentInt, int expectedInt) {
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
    public void SourceLabel_DefaultRow_NoRule_ReturnsDash() {
        // The proto default for RuleSource is Manual (zero value), so without
        // the HasRule guard the SourceLabel would erroneously read "manual"
        // for every row in the table, even ones with no actual rule.
        var row = new FirewallRuleRow(@"C:\app.exe");

        Assert.False(row.HasRule);
        Assert.Equal("—", row.SourceLabel);
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
}
