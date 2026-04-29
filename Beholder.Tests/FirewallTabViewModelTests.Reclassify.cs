using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;

namespace Beholder.Tests;

// The Firewall tab's pill Buttons (ALLOW/BLOCK) used to swallow the user's
// first click whenever the cursor was hovering during the daemon's 1Hz
// counter tick: Reclassify() called Clear() + re-Add on both observable
// collections, Avalonia's ItemsControl recycled containers on the Reset
// event, and PointerPressed/PointerReleased landed on different button
// instances across the recycle. The fix uses single-step Insert/Remove/
// Move so stable rows' container instances stay materialized. These tests
// pin the architectural goal (zero events on no-op ticks) and the diff
// semantics (correct sort-position for inserts, identity stability across
// ticks). All tests call ApplyProcessStates synchronously to bypass the
// dispatcher hop in OnProcessStatesUpdated — the existing suite has no
// Avalonia headless dispatcher and the new internal helper exists
// precisely so tests don't need one.

public partial class FirewallTabViewModelTests {
    /// <summary>
    /// Subscribes to <paramref name="collection"/>'s CollectionChanged event
    /// and accumulates the args. Returns the captured list and an unsubscriber.
    /// </summary>
    private static (List<NotifyCollectionChangedEventArgs> Events, Action Unsubscribe)
    RecordCollectionChanges<T>(ObservableCollection<T> collection) {
        var events = new List<NotifyCollectionChangedEventArgs>();
        NotifyCollectionChangedEventHandler handler = (_, e) => events.Add(e);
        collection.CollectionChanged += handler;
        return (events, () => collection.CollectionChanged -= handler);
    }

    /// <summary>
    /// Builds the synthetic state map the daemon would emit for a set of
    /// active processes at one counter tick. Mirrors the wire-side
    /// <c>CounterSnapshot</c> shape that <see cref="ProcessStateService"/>
    /// translates into <see cref="ProcessState"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, ProcessState> StateMap(params string[] activePaths) {
        var dict = new Dictionary<string, ProcessState>(StringComparer.Ordinal);
        foreach (var path in activePaths) {
            dict[path] = new ProcessState {
                ProcessPath = path,
                DisplayName = System.IO.Path.GetFileName(path),
                TotalBytesIn = 100,
                TotalBytesOut = 100,
                ActiveConnectionCount = 1,
            };
        }
        return dict;
    }

    [Fact]
    public async Task Reclassify_NoMembershipChange_FiresZeroCollectionChangedEvents() {
        // Headline test: the steady-state 1Hz tick where every row's
        // membership is unchanged must emit zero CollectionChanged events on
        // either ObservableCollection. This is the architectural guarantee
        // that keeps Avalonia's ItemsControl from recycling pill containers
        // and swallowing clicks.
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        // Seed two active rows.
        vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));
        Assert.Equal(2, vm.ActiveRows.Count);

        var active = RecordCollectionChanges(vm.ActiveRows);
        var inactive = RecordCollectionChanges(vm.InactiveRows);
        try {
            // Tick again with the SAME state map — properties may update,
            // but membership and sort position are identical.
            vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

            Assert.Empty(active.Events);
            Assert.Empty(inactive.Events);
        } finally {
            active.Unsubscribe();
            inactive.Unsubscribe();
        }
    }

    [Fact]
    public async Task Reclassify_NoMembershipChange_DoesNotFireFilteredPropertyChanged() {
        // Sister test to the headline one. The view binds ItemsSource to the
        // computed FilteredActiveRows / FilteredInactiveRows properties; an
        // unconditional OnPropertyChanged would force Avalonia to drop and
        // re-evaluate the IEnumerable, which is treated as a wholesale source
        // replacement (same Reset-class container churn). The fix guards the
        // four Filtered* + HasRows notifications behind an "any mutation"
        // bool. A no-op tick must emit none of them.
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

        var fired = new List<string>();
        PropertyChangedEventHandler handler = (_, e) => fired.Add(e.PropertyName ?? "");
        vm.PropertyChanged += handler;
        try {
            vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

            Assert.DoesNotContain(nameof(vm.FilteredActiveRows), fired);
            Assert.DoesNotContain(nameof(vm.FilteredInactiveRows), fired);
            Assert.DoesNotContain(nameof(vm.HasFilteredActiveRows), fired);
            Assert.DoesNotContain(nameof(vm.HasFilteredInactiveRows), fired);
            Assert.DoesNotContain(nameof(vm.HasRows), fired);
        } finally {
            vm.PropertyChanged -= handler;
        }
    }

    [Fact]
    public async Task Reclassify_RowFlipsActiveToInactive_FiresExactlyOneRemoveAndOneInsert() {
        // A row's IsActive flag flipping true→false should produce one Remove
        // event on ActiveRows and one Insert/Add event on InactiveRows — not
        // a wholesale rebuild of either. (When IsActive flips off,
        // ApplyProcessStates resets ExecutableExists to default true, so the
        // row enters Inactive's tier-1 ExecutableExists bucket.)
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe"));
        Assert.Single(vm.ActiveRows);

        var active = RecordCollectionChanges(vm.ActiveRows);
        var inactive = RecordCollectionChanges(vm.InactiveRows);
        try {
            // No active processes this tick — the row's IsActive flips false.
            vm.ApplyProcessStates(StateMap());

            var removed = Assert.Single(active.Events);
            Assert.Equal(NotifyCollectionChangedAction.Remove, removed.Action);
            var added = Assert.Single(inactive.Events);
            Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
            Assert.Equal(0, added.NewStartingIndex);
        } finally {
            active.Unsubscribe();
            inactive.Unsubscribe();
        }
    }

    [Fact]
    public async Task Reclassify_RowFlipsInactiveToActive_FiresExactlyOneRemoveAndOneInsert() {
        // Mirror of the previous test. A row that exists only via a rule
        // (Inactive at activation) gets reported live in the next tick.
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\bin\a.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(rules: rules);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.InactiveRows);

        var active = RecordCollectionChanges(vm.ActiveRows);
        var inactive = RecordCollectionChanges(vm.InactiveRows);
        try {
            vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe"));

            var added = Assert.Single(active.Events);
            Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
            Assert.Equal(0, added.NewStartingIndex);
            var removed = Assert.Single(inactive.Events);
            Assert.Equal(NotifyCollectionChangedAction.Remove, removed.Action);
        } finally {
            active.Unsubscribe();
            inactive.Unsubscribe();
        }
    }

    [Fact]
    public async Task Reclassify_NewRowSortsAtIndexZero_InsertedNotAppended() {
        // Sort-correctness pin: a row whose DisplayName comes first
        // alphabetically must be inserted at index 0, not appended. This is
        // the property that lets the diff-and-mutate algorithm be correct
        // (a naive "append-and-resort" wouldn't preserve container stability
        // for the rows that didn't change position).
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        vm.ApplyProcessStates(StateMap(@"C:\bin\m.exe", @"C:\bin\z.exe"));

        var active = RecordCollectionChanges(vm.ActiveRows);
        try {
            vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\m.exe", @"C:\bin\z.exe"));

            var added = Assert.Single(active.Events);
            Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
            Assert.Equal(0, added.NewStartingIndex);
            Assert.Equal(@"C:\bin\a.exe", vm.ActiveRows[0].ProcessPath);
        } finally {
            active.Unsubscribe();
        }
    }

    [Fact]
    public async Task Reclassify_NewRowSortsInMiddle_InsertedAtCorrectPosition() {
        // Companion to the index-zero test: middle insertion. The algorithm
        // walks desired in lockstep, so a new row whose DisplayName lands
        // between two existing rows must produce a single Insert at the
        // correct index — neither append nor full reshuffle.
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\z.exe"));

        var active = RecordCollectionChanges(vm.ActiveRows);
        try {
            vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\m.exe", @"C:\bin\z.exe"));

            var added = Assert.Single(active.Events);
            Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
            Assert.Equal(1, added.NewStartingIndex);
        } finally {
            active.Unsubscribe();
        }
    }

    [Fact]
    public async Task Reclassify_StableRowsKeepSameInstance_AcrossRepeatedTicks() {
        // The model-level guarantee that backs the "container survives" goal:
        // a row that's already in the right collection at the right index
        // must remain the same object reference across many consecutive
        // ticks. If this test passes, Avalonia's ItemsControl will see the
        // same item on each evaluation and reuse the materialized container,
        // which preserves the pill Button's pointer state.
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

        var rowA = vm.ActiveRows[0];
        var rowB = vm.ActiveRows[1];

        // Ten ticks with the same membership.
        for (var i = 0; i < 10; i++) {
            vm.ApplyProcessStates(StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));
        }

        Assert.Same(rowA, vm.ActiveRows[0]);
        Assert.Same(rowB, vm.ActiveRows[1]);
    }

    [Fact]
    public async Task Reclassify_PreservesOrphanedRulesSortToBottomOfInactive() {
        // The diff-and-mutate must preserve the existing two-tier Inactive
        // sort: ExecutableExists rows first (alphabetical), then orphaned
        // rows (alphabetical). Pinned by the existing
        // ActivateAsync_OrphanedRulesSortToBottomOfInactive test for the
        // initial activation path; this test verifies the same invariant
        // holds after a no-op live tick (Reclassify is called from
        // ApplyProcessStates and must not reorder the tiers).
        var rules = new ListFirewallRulesResponse();
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\aaa-orphaned.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        rules.Rules.Add(new FirewallRule {
            ProcessPath = @"C:\zzz-existing.exe",
            Direction = Direction.Outbound,
            Action = FirewallAction.Block,
            Source = RuleSource.Manual,
        });
        var (vm, _, _) = CreateVm(
            rules: rules,
            fileExistsCheck: path => path.Contains("zzz-existing"));
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        // Trigger a no-op tick — no live processes. The Inactive rows must
        // remain in the same two-tier order they had after activation.
        vm.ApplyProcessStates(StateMap());

        Assert.Equal(2, vm.InactiveRows.Count);
        Assert.Equal(@"C:\zzz-existing.exe", vm.InactiveRows[0].ProcessPath);
        Assert.False(vm.InactiveRows[0].IsOrphanedRule);
        Assert.Equal(@"C:\aaa-orphaned.exe", vm.InactiveRows[1].ProcessPath);
        Assert.True(vm.InactiveRows[1].IsOrphanedRule);
    }
}
