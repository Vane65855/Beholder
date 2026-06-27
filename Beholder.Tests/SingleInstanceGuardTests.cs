#if PLATFORM_WINDOWS
using System;
using System.Threading;
using Beholder.Ui;

namespace Beholder.Tests;

/// <summary>
/// Covers the single-instance guard's detection + activation signaling in-process.
/// A unique mutex/event scope per test avoids colliding with a running Beholder UI
/// or with other tests. The window-surfacing itself is a manual smoke test.
/// </summary>
public class SingleInstanceGuardTests {
    private static string UniqueScope() => "Beholder.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Acquire_FirstInstance_IsPrimary() {
        using var first = SingleInstanceGuard.Acquire(UniqueScope());
        Assert.True(first.IsPrimary);
    }

    [Fact]
    public void Acquire_SecondInstanceWhileFirstHeld_IsNotPrimary() {
        var scope = UniqueScope();
        using var first = SingleInstanceGuard.Acquire(scope);
        using var second = SingleInstanceGuard.Acquire(scope);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void SignalActivation_RaisesPrimaryActivated() {
        var scope = UniqueScope();
        using var first = SingleInstanceGuard.Acquire(scope);
        using var second = SingleInstanceGuard.Acquire(scope);
        using var activated = new ManualResetEventSlim(false);
        first.Activated += () => activated.Set();

        second.SignalActivation();

        Assert.True(activated.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Acquire_AfterPrimaryDisposed_IsPrimaryAgain() {
        var scope = UniqueScope();
        using (var first = SingleInstanceGuard.Acquire(scope))
            Assert.True(first.IsPrimary);

        using var next = SingleInstanceGuard.Acquire(scope);
        Assert.True(next.IsPrimary);
    }
}
#endif
