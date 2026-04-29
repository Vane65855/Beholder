using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

public partial class FirewallRuleRowTests {
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
