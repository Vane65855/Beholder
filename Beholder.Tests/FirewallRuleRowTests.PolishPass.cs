using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

public partial class FirewallRuleRowTests {
    [Fact]
    public void SourceLabel_DefaultRow_NoRule_ReturnsDefault() {
        // No Beholder rule means the system default applies (Windows allows
        // by default). SOURCE shows "default" to convey that effective state.
        // The em-dash is reserved for the defensive fallback below (genuinely
        // unrecognized RuleSource enum values).
        var row = new FirewallRuleRow(@"C:\app.exe");

        Assert.False(row.HasRule);
        Assert.Equal("DEFAULT", row.SourceLabel);
    }

    [Fact]
    public void SourceLabel_HasRuleTrue_ReturnsSourceText() {
        var row = new FirewallRuleRow(@"C:\app.exe") {
            HasRule = true,
            Source = Beholder.Protocol.Local.RuleSource.Manual,
        };

        Assert.Equal("MANUAL", row.SourceLabel);
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

        Assert.Equal("DEFAULT", row.SourceLabel);
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
    public void IsSourceDefault_CoversAllStatePermutations() {
        // Pins the matrix that drives the SOURCE column's foreground hierarchy:
        // muted (TextMuted) when the source is effectively "default" — either
        // no rule exists, or the rule's source is explicitly Default. Manual
        // and Remote rules render in the brighter TextSecondary. The view
        // binds Classes.muted="{Binding IsSourceDefault}" so the visual class
        // tracks this bool 1:1.

        // No rule → default applies regardless of Source enum value.
        var noRule = new FirewallRuleRow(@"C:\app.exe") { HasRule = false };
        Assert.True(noRule.IsSourceDefault);

        // Explicit RuleSource.Default → also default.
        var explicitDefault = new FirewallRuleRow(@"C:\app.exe") {
            HasRule = true,
            Source = Beholder.Protocol.Local.RuleSource.Default,
        };
        Assert.True(explicitDefault.IsSourceDefault);

        // Manual rule → not default (lift to brighter foreground).
        var manual = new FirewallRuleRow(@"C:\app.exe") {
            HasRule = true,
            Source = Beholder.Protocol.Local.RuleSource.Manual,
        };
        Assert.False(manual.IsSourceDefault);

        // Remote rule → not default (lift to brighter foreground).
        var remote = new FirewallRuleRow(@"C:\app.exe") {
            HasRule = true,
            Source = Beholder.Protocol.Local.RuleSource.Remote,
        };
        Assert.False(remote.IsSourceDefault);
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
