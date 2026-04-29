using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

// Pin: FirewallTabViewModel.Reclassify uses diff-and-mutate so a no-op tick
// emits zero CollectionChanged events on the bound ObservableCollections
// and zero Filtered* PropertyChanged on the VM. See Reclassify and
// ReconcileSorted XML docs for the full rationale.
//
// Tests drive the VM through its real ProcessStatesUpdated event-handler
// path (see RaiseProcessStatesUpdated below). The injected SyncDispatcher
// makes IDispatcher.Post run synchronously, so by the time
// RaiseProcessStatesUpdated returns, OnProcessStatesUpdated → Reclassify
// has already mutated ActiveRows / InactiveRows.

public partial class FirewallTabViewModelTests {
    /// <summary>
    /// Subscribes to <paramref name="collection"/>'s CollectionChanged event
    /// and accumulates the args until disposed. Returned recorder is meant
    /// to be used with a <c>using</c> statement so the unsubscribe happens
    /// automatically when the test method exits.
    /// </summary>
    private static CollectionChangeRecorder<T> RecordCollectionChanges<T>(
        ObservableCollection<T> collection) => new(collection);

    private sealed class CollectionChangeRecorder<T> : IDisposable {
        private readonly ObservableCollection<T> _collection;
        private readonly NotifyCollectionChangedEventHandler _handler;

        public List<NotifyCollectionChangedEventArgs> Events { get; } = new();

        public CollectionChangeRecorder(ObservableCollection<T> collection) {
            _collection = collection;
            _handler = (_, e) => Events.Add(e);
            _collection.CollectionChanged += _handler;
        }

        public void Dispose() => _collection.CollectionChanged -= _handler;
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

    /// <summary>
    /// Raises <c>ProcessStatesUpdated</c> on the VM's wired
    /// <see cref="ProcessStateService"/> with <paramref name="states"/> —
    /// drives the same event path the daemon's live counter batch would,
    /// so the VM's <c>OnProcessStatesUpdated → IDispatcher.Post →
    /// ApplyProcessStates → Reclassify</c> chain runs identically to
    /// production. With <see cref="TestDoubles.SyncDispatcher"/> injected
    /// (see <c>CreateVm</c>), the chain runs synchronously on the calling
    /// thread.
    /// </summary>
    /// <remarks>
    /// Reflection is needed because <c>ProcessStatesUpdated</c> is a
    /// <c>public event</c> exposing only <c>+=</c>/<c>-=</c> to subscribers;
    /// only the owning class can <c>Invoke</c> the backing delegate.
    /// Keeping this contained to the test layer (rather than adding an
    /// <c>RaiseProcessStatesUpdated</c> public method to the production
    /// service) preserves the production API surface.
    /// </remarks>
    private static void RaiseProcessStatesUpdated(
        FirewallTabViewModel vm, IReadOnlyDictionary<string, ProcessState> states) {
        var serviceField = typeof(FirewallTabViewModel)
            .GetField("_processStateService", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var service = (ProcessStateService)serviceField.GetValue(vm)!;

        var eventField = typeof(ProcessStateService)
            .GetField("ProcessStatesUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var del = (Action<IReadOnlyDictionary<string, ProcessState>>?)eventField.GetValue(service);
        del?.Invoke(states);
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
        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));
        Assert.Equal(2, vm.ActiveRows.Count);

        using var active = RecordCollectionChanges(vm.ActiveRows);
        using var inactive = RecordCollectionChanges(vm.InactiveRows);

        // Tick again with the SAME state map — properties may update,
        // but membership and sort position are identical.
        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

        Assert.Empty(active.Events);
        Assert.Empty(inactive.Events);
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
        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

        var fired = new List<string>();
        PropertyChangedEventHandler handler = (_, e) => fired.Add(e.PropertyName ?? "");
        vm.PropertyChanged += handler;
        try {
            RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

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
        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe"));
        Assert.Single(vm.ActiveRows);

        using var active = RecordCollectionChanges(vm.ActiveRows);
        using var inactive = RecordCollectionChanges(vm.InactiveRows);

        // No active processes this tick — the row's IsActive flips false.
        RaiseProcessStatesUpdated(vm,StateMap());

        var removed = Assert.Single(active.Events);
        Assert.Equal(NotifyCollectionChangedAction.Remove, removed.Action);
        var added = Assert.Single(inactive.Events);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(0, added.NewStartingIndex);
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

        using var active = RecordCollectionChanges(vm.ActiveRows);
        using var inactive = RecordCollectionChanges(vm.InactiveRows);

        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe"));

        var added = Assert.Single(active.Events);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(0, added.NewStartingIndex);
        var removed = Assert.Single(inactive.Events);
        Assert.Equal(NotifyCollectionChangedAction.Remove, removed.Action);
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
        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\m.exe", @"C:\bin\z.exe"));

        using var active = RecordCollectionChanges(vm.ActiveRows);

        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\m.exe", @"C:\bin\z.exe"));

        var added = Assert.Single(active.Events);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(0, added.NewStartingIndex);
        Assert.Equal(@"C:\bin\a.exe", vm.ActiveRows[0].ProcessPath);
    }

    [Fact]
    public async Task Reclassify_NewRowSortsInMiddle_InsertedAtCorrectPosition() {
        // Companion to the index-zero test: middle insertion. The algorithm
        // walks desired in lockstep, so a new row whose DisplayName lands
        // between two existing rows must produce a single Insert at the
        // correct index — neither append nor full reshuffle.
        var (vm, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\z.exe"));

        using var active = RecordCollectionChanges(vm.ActiveRows);

        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\m.exe", @"C:\bin\z.exe"));

        var added = Assert.Single(active.Events);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(1, added.NewStartingIndex);
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
        RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));

        var rowA = vm.ActiveRows[0];
        var rowB = vm.ActiveRows[1];

        // Ten ticks with the same membership.
        for (var i = 0; i < 10; i++) {
            RaiseProcessStatesUpdated(vm,StateMap(@"C:\bin\a.exe", @"C:\bin\b.exe"));
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
        RaiseProcessStatesUpdated(vm,StateMap());

        Assert.Equal(2, vm.InactiveRows.Count);
        Assert.Equal(@"C:\zzz-existing.exe", vm.InactiveRows[0].ProcessPath);
        Assert.False(vm.InactiveRows[0].IsOrphanedRule);
        Assert.Equal(@"C:\aaa-orphaned.exe", vm.InactiveRows[1].ProcessPath);
        Assert.True(vm.InactiveRows[1].IsOrphanedRule);
    }
}
