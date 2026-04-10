using Beholder.Core;

namespace Beholder.Tests;

public class FirewallRuleTests {
    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updated = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var rule = new FirewallRule(
            id: 7,
            processPath: "/usr/bin/curl",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: created,
            updatedAt: updated
        );

        Assert.Equal(7, rule.Id);
        Assert.Equal("/usr/bin/curl", rule.ProcessPath);
        Assert.Equal(Direction.Outbound, rule.Direction);
        Assert.Equal(FirewallAction.Block, rule.Action);
        Assert.Equal(RuleSource.Manual, rule.Source);
        Assert.Equal(created, rule.CreatedAt);
        Assert.Equal(updated, rule.UpdatedAt);
    }

    [Fact]
    public void Constructor_NullProcessPath_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new FirewallRule(
            id: 0,
            processPath: null!,
            direction: Direction.Outbound,
            action: FirewallAction.Allow,
            source: RuleSource.Manual,
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch
        ));
    }

    [Fact]
    public void Constructor_WhitespaceProcessPath_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(() => new FirewallRule(
            id: 0,
            processPath: "   ",
            direction: Direction.Outbound,
            action: FirewallAction.Allow,
            source: RuleSource.Manual,
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch
        ));
    }

    [Fact]
    public void AllRuleSourceValues_AreDefined() {
        Assert.True(Enum.IsDefined(RuleSource.Manual));
        Assert.True(Enum.IsDefined(RuleSource.Default));
        Assert.True(Enum.IsDefined(RuleSource.Remote));
        Assert.Equal(3, Enum.GetValues<RuleSource>().Length);
    }
}
