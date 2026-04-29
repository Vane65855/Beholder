namespace Beholder.Tests;

public partial class FirewallTabViewModelTests {
    [Fact]
    public void TransientMessage_DefaultsToEmpty() {
        var (vm, _, _) = CreateVm();

        Assert.False(vm.HasTransientMessage);
        Assert.Empty(vm.TransientMessage);
    }

    [Fact]
    public void NotifyPathCopied_SetsTransientMessageAndFlag() {
        var (vm, _, _) = CreateVm();

        vm.NotifyPathCopied(@"C:\Program Files\Mozilla Firefox");

        Assert.True(vm.HasTransientMessage);
        Assert.Contains(@"C:\Program Files\Mozilla Firefox", vm.TransientMessage);
    }

    [Fact]
    public void NotifyPathCopied_EmptyOrWhitespace_IgnoresSilently() {
        // Defensive: the view code-behind already filters empty paths via
        // Path.GetDirectoryName(...) string-empty check, but the VM's API
        // shouldn't trust that and shouldn't surface a banner with no content.
        var (vm, _, _) = CreateVm();

        vm.NotifyPathCopied("");
        Assert.False(vm.HasTransientMessage);

        vm.NotifyPathCopied("   ");
        Assert.False(vm.HasTransientMessage);

        vm.NotifyPathCopied(null!);
        Assert.False(vm.HasTransientMessage);
    }

    [Fact]
    public void NotifyPathCopied_SecondCallWithinWindow_KeepsLatestState() {
        // The first call's pending 2-second auto-clear must not race with a
        // second call's banner update. The CancellationTokenSource pattern
        // ensures the second call cancels the first's timer; this test pins
        // that the message is the second call's payload, not the first's.
        var (vm, _, _) = CreateVm();

        vm.NotifyPathCopied(@"C:\first");
        vm.NotifyPathCopied(@"C:\second");

        Assert.True(vm.HasTransientMessage);
        Assert.Contains("second", vm.TransientMessage);
        Assert.DoesNotContain("first", vm.TransientMessage);
    }
}
