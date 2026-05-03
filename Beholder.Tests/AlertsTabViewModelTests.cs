using System.Reflection;
using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class AlertsTabViewModelTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Constructs an <see cref="AlertsTabViewModel"/> wired to the standard
    /// fake stack: <see cref="FakeDaemonClient"/>, real
    /// <see cref="DaemonStreamSubscriber"/> (which the VM listens to for
    /// live broadcasts), and <see cref="SyncDispatcher"/> so the live-update
    /// dispatcher hop runs synchronously on the calling thread. Captured
    /// navigation paths flow into <paramref name="navigateCaptures"/> so
    /// AddRule tests can assert the deep-link target.
    /// </summary>
    private static (AlertsTabViewModel Vm, FakeDaemonClient Client, DaemonStreamSubscriber Subscriber, List<string> NavigateCaptures, FakeNotificationService Notifications)
    CreateVm(
        GetSnapshotResponse? snapshot = null,
        Func<string, bool>? fileExistsCheck = null
    ) {
        var client = new FakeDaemonClient();
        if (snapshot is not null) client.SnapshotResponse = snapshot;
        var subscriber = new DaemonStreamSubscriber(
            client, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        var captures = new List<string>();
        var notifications = new FakeNotificationService();
        var vm = new AlertsTabViewModel(
            client, subscriber, new SyncDispatcher(), notifications,
            // Delegate is now Func<string, Task> (was Action<string>) so the
            // production NavigateToFirewallRuleAsync can await the Firewall
            // tab's activation before highlighting. Capture synchronously
            // before returning the completed task — the test still sees the
            // path immediately on AddRule invocation.
            navigateToFirewallRule: path => { captures.Add(path); return Task.CompletedTask; },
            fileExistsCheck: fileExistsCheck);
        return (vm, client, subscriber, captures, notifications);
    }

    private static Alert MakeAlert(
        long seq,
        AlertKind kind = AlertKind.NewProcess,
        string processPath = @"C:\bin\app.exe",
        string summary = "first network connection",
        DateTimeOffset? timestamp = null,
        bool isRead = false
    ) {
        var ts = timestamp ?? FixedTimestamp.AddSeconds(seq);
        return new Alert {
            Seq = seq,
            Kind = kind,
            ProcessPath = processPath,
            Summary = summary,
            TimestampUnixNs = ts.ToUnixTimeMilliseconds() * 1_000_000L,
            FirstViewedAtUnixNs = isRead ? ts.ToUnixTimeMilliseconds() * 1_000_000L : 0L,
        };
    }

    private static GetSnapshotResponse SnapshotWith(params Alert[] alerts) {
        var response = new GetSnapshotResponse();
        foreach (var alert in alerts) response.RecentAlerts.Add(alert);
        return response;
    }

    /// <summary>
    /// Raises <see cref="DaemonStreamSubscriber.AlertReceived"/> with
    /// <paramref name="ev"/> via reflection. The compiler generates a
    /// private backing field with the same name as the event; with
    /// <see cref="SyncDispatcher"/> injected into the VM, the marshaled
    /// handler runs synchronously on the calling thread. Mirrors the
    /// <c>RaiseProcessStatesUpdated</c> helper in
    /// <c>FirewallTabViewModelTests.Reclassify.cs</c>.
    /// </summary>
    private static void RaiseAlertReceived(DaemonStreamSubscriber subscriber, AlertEvent ev) {
        var eventField = typeof(DaemonStreamSubscriber)
            .GetField("AlertReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var del = (Action<AlertEvent>?)eventField.GetValue(subscriber);
        del?.Invoke(ev);
    }

    /// <summary>
    /// Sibling helper: raises <see cref="DaemonStreamSubscriber.RuleChangeReceived"/>
    /// via reflection so tests can drive the Alerts tab's outbound-block
    /// state machine without spinning up a real gRPC stream.
    /// </summary>
    private static void RaiseRuleChangeReceived(DaemonStreamSubscriber subscriber, FirewallRuleChange change) {
        var eventField = typeof(DaemonStreamSubscriber)
            .GetField("RuleChangeReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var del = (Action<FirewallRuleChange>?)eventField.GetValue(subscriber);
        del?.Invoke(change);
    }

    private static FirewallRule MakeRule(
        string processPath = @"C:\bin\firefox.exe",
        Direction direction = Direction.Outbound,
        FirewallAction action = FirewallAction.Block
    ) => new() {
        Id = 1,
        ProcessPath = processPath,
        Direction = direction,
        Action = action,
        Source = RuleSource.Manual,
        CreatedAtUnixNs = FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
        UpdatedAtUnixNs = FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L,
    };

    [Fact]
    public async Task ActivateAsync_NoAlerts_ShowsEmptyState() {
        var (vm, _, _, _, _) = CreateVm();

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(vm.Alerts);
        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasAlerts);
        Assert.Null(vm.SelectedAlert);
    }

    [Fact]
    public async Task ActivateAsync_WithAlerts_PopulatesListInResponseOrder() {
        // RecentAlerts on GetSnapshotResponse arrives newest-first from the
        // daemon (BeholderLocalService line 90–111 ORDER BY seq DESC). The VM
        // preserves that order verbatim — no client-side resort.
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe"),
            MakeAlert(seq: 99, processPath: @"C:\bin\chrome.exe"),
            MakeAlert(seq: 98, processPath: @"C:\bin\app.exe"));
        var (vm, _, _, _, _) = CreateVm(snapshot);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasAlerts);
        Assert.Equal(3, vm.Alerts.Count);
        Assert.Equal(100, vm.Alerts[0].Seq);
        Assert.Equal(99, vm.Alerts[1].Seq);
        Assert.Equal(98, vm.Alerts[2].Seq);
    }

    [Fact]
    public async Task ActivateAsync_WithAlerts_AutoSelectsFirstAlert() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, isRead: true));  // pre-read so auto-mark doesn't fire
        var (vm, _, _, _, _) = CreateVm(snapshot);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.SelectedAlert);
        Assert.Equal(100, vm.SelectedAlert.Seq);
        Assert.True(vm.ShowDetailPane);
    }

    [Fact]
    public async Task ActivateAsync_RpcFailure_SetsErrorState() {
        var (vm, client, _, _, _) = CreateVm();
        client.SnapshotException = new InvalidOperationException("boom");

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasError);
        Assert.Contains("boom", vm.ErrorMessage);
        Assert.False(vm.IsLoading);
        Assert.False(vm.ShowEmptyState);  // empty-state hides while error is shown
    }

    [Fact]
    public async Task ActivateAsync_IsIdempotent() {
        var snapshot = SnapshotWith(MakeAlert(seq: 1, isRead: true));
        var (vm, _, _, _, _) = CreateVm(snapshot);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        var firstCount = vm.Alerts.Count;
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(firstCount, vm.Alerts.Count);
    }

    [Fact]
    public async Task LiveAlertReceived_PrependsToList() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, isRead: true));
        var (vm, _, subscriber, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        // Daemon broadcasts a newer alert with seq 101.
        var newAlert = MakeAlert(seq: 101, processPath: @"C:\bin\new.exe", isRead: true);
        RaiseAlertReceived(subscriber, new AlertEvent { Alert = newAlert });

        Assert.Equal(2, vm.Alerts.Count);
        Assert.Equal(101, vm.Alerts[0].Seq);  // newest at index 0
        Assert.Equal(100, vm.Alerts[1].Seq);
    }

    [Fact]
    public async Task LiveAlertReceived_DuplicateSeq_IgnoredSilently() {
        // Initial fetch returned seq=100. A live broadcast for the same seq
        // (e.g., subscribe arrived before the snapshot RPC completed) must
        // be deduplicated against the in-memory _seenSeqs set.
        var snapshot = SnapshotWith(MakeAlert(seq: 100, isRead: true));
        var (vm, _, subscriber, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        RaiseAlertReceived(subscriber, new AlertEvent { Alert = MakeAlert(seq: 100, isRead: true) });

        Assert.Single(vm.Alerts);
    }

    [Fact]
    public async Task LiveAlertReceived_AutoSelectsWhenNothingSelected() {
        // Tab opened on empty state (no alerts at activation). Live alert
        // arrives → auto-select so the detail pane has content immediately.
        var (vm, _, subscriber, _, _) = CreateVm();
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Null(vm.SelectedAlert);

        RaiseAlertReceived(subscriber, new AlertEvent { Alert = MakeAlert(seq: 1, isRead: true) });

        Assert.NotNull(vm.SelectedAlert);
        Assert.Equal(1, vm.SelectedAlert.Seq);
    }

    [Fact]
    public async Task SelectingUnreadAlert_OptimisticIsReadFlip_AndCallsRpc() {
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, isRead: true),    // auto-selected on activation; pre-read so no mark-read fires
            MakeAlert(seq: 99, isRead: false));   // unread; user selects this next
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(client.MarkAlertReadCalls);

        // User selects the unread alert at index 1.
        vm.SelectedAlert = vm.Alerts[1];

        Assert.True(vm.SelectedAlert.IsRead);  // optimistic flip
        var call = Assert.Single(client.MarkAlertReadCalls);
        Assert.Equal(99, call.Seq);
    }

    [Fact]
    public async Task SelectingUnreadAlert_RpcFailure_RevertsIsRead() {
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, isRead: true),
            MakeAlert(seq: 99, isRead: false));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.MarkAlertReadException = new InvalidOperationException("daemon down");

        vm.SelectedAlert = vm.Alerts[1];

        Assert.False(vm.SelectedAlert.IsRead);  // reverted on RPC failure
        Assert.True(vm.HasError);
        Assert.Contains("daemon down", vm.ErrorMessage);
    }

    [Fact]
    public async Task BlockProcessOut_CallsApplyFirewallRuleWithOutboundBlock() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        await vm.BlockProcessOutCommand.ExecuteAsync(vm.SelectedAlert);

        var call = Assert.Single(client.ApplyFirewallRuleCalls);
        Assert.Equal(@"C:\bin\firefox.exe", call.ProcessPath);
        Assert.Equal(Direction.Outbound, call.Direction);
        Assert.Equal(FirewallAction.Block, call.Action);
        Assert.Equal(RuleSource.Manual, call.Source);
    }

    [Fact]
    public async Task BlockProcessOut_RpcFailure_SetsErrorState() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.ApplyFirewallRuleException = new InvalidOperationException("apply failed");

        await vm.BlockProcessOutCommand.ExecuteAsync(vm.SelectedAlert);

        Assert.True(vm.HasError);
        Assert.Contains("app.exe", vm.ErrorMessage);
    }

    [Fact]
    public async Task ActivateAsync_SeedsOutboundBlockedFromSnapshotRules() {
        // Snapshot carries an active Outbound+Block rule for firefox.exe; the
        // matching alert row must land with IsOutboundBlocked=true so the
        // detail-pane footer renders UNBLOCK on first paint.
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true),
            MakeAlert(seq: 99, processPath: @"C:\bin\app.exe", isRead: true));
        snapshot.FirewallRules.Add(MakeRule(processPath: @"C:\bin\firefox.exe"));
        var (vm, _, _, _, _) = CreateVm(snapshot);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        var firefox = Assert.Single(vm.Alerts, r => r.ProcessPath == @"C:\bin\firefox.exe");
        var app = Assert.Single(vm.Alerts, r => r.ProcessPath == @"C:\bin\app.exe");
        Assert.True(firefox.IsOutboundBlocked);
        Assert.False(app.IsOutboundBlocked);
    }

    [Fact]
    public async Task LiveRuleChangeBlocked_FlipsIsOutboundBlocked() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true));
        var (vm, _, subscriber, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(vm.Alerts);
        Assert.False(row.IsOutboundBlocked);

        RaiseRuleChangeReceived(subscriber, new FirewallRuleChange {
            Change = FirewallRuleChange.Types.ChangeKind.Created,
            Rule = MakeRule(processPath: @"C:\bin\firefox.exe"),
        });

        Assert.True(row.IsOutboundBlocked);
    }

    [Fact]
    public async Task LiveRuleChangeRemoved_FlipsIsOutboundBlockedFalse() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true));
        snapshot.FirewallRules.Add(MakeRule(processPath: @"C:\bin\firefox.exe"));
        var (vm, _, subscriber, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(vm.Alerts);
        Assert.True(row.IsOutboundBlocked);  // seeded from snapshot

        RaiseRuleChangeReceived(subscriber, new FirewallRuleChange {
            Change = FirewallRuleChange.Types.ChangeKind.Removed,
            Rule = MakeRule(processPath: @"C:\bin\firefox.exe"),
        });

        Assert.False(row.IsOutboundBlocked);
    }

    [Fact]
    public async Task LiveRuleChange_InboundDirection_DoesNotAffectOutboundState() {
        // The Alerts tab only surfaces outbound state. An Inbound Block rule
        // for the same process must not flip IsOutboundBlocked.
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true));
        var (vm, _, subscriber, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(vm.Alerts);

        RaiseRuleChangeReceived(subscriber, new FirewallRuleChange {
            Change = FirewallRuleChange.Types.ChangeKind.Created,
            Rule = MakeRule(processPath: @"C:\bin\firefox.exe", direction: Direction.Inbound),
        });

        Assert.False(row.IsOutboundBlocked);
    }

    [Fact]
    public async Task UnblockProcessOut_CallsRemoveFirewallRuleWithOutbound() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true));
        snapshot.FirewallRules.Add(MakeRule(processPath: @"C:\bin\firefox.exe"));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(client.RemoveFirewallRuleCalls);

        await vm.UnblockProcessOutCommand.ExecuteAsync(vm.SelectedAlert);

        var call = Assert.Single(client.RemoveFirewallRuleCalls);
        Assert.Equal(@"C:\bin\firefox.exe", call.ProcessPath);
        Assert.Equal(Direction.Outbound, call.Direction);
    }

    [Fact]
    public async Task UnblockProcessOut_RpcFailure_SetsErrorState() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true));
        snapshot.FirewallRules.Add(MakeRule(processPath: @"C:\bin\app.exe"));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.RemoveFirewallRuleException = new InvalidOperationException("remove failed");

        await vm.UnblockProcessOutCommand.ExecuteAsync(vm.SelectedAlert);

        Assert.True(vm.HasError);
        Assert.Contains("app.exe", vm.ErrorMessage);
    }

    [Fact]
    public async Task AddRule_InvokesNavigationDelegate() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true));
        var (vm, _, _, captures, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.AddRuleCommand.Execute(vm.SelectedAlert);

        var captured = Assert.Single(captures);
        Assert.Equal(@"C:\bin\app.exe", captured);
    }

    [Fact]
    public async Task ActivateAsync_ConcurrentCallers_ShareSameUnderlyingLoad() {
        // Regression for the cold-start race the toast → NavigateToAlertAsync
        // deep-link hits. Pre-fix, the bool _activated guard let the second
        // concurrent caller see _activated=true and return Task.CompletedTask
        // while the first caller's snapshot RPC was still in flight, so
        // SelectBySeq would run against an empty Alerts collection.
        // Post-fix (Task? _activationTask), the second call returns the
        // in-flight Task so the awaited caller doesn't return until the load
        // actually completes.
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\app.exe", isRead: true));
        var client = new FakeDaemonClient();
        var release = new TaskCompletionSource<GetSnapshotResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.SnapshotResponder = _ => release.Task;
        var subscriber = new DaemonStreamSubscriber(
            client, TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance);
        var vm = new AlertsTabViewModel(
            client, subscriber, new SyncDispatcher(), new FakeNotificationService());

        // Caller A (the OnActiveTabChanged fire-and-forget): kicks off the
        // load, doesn't await. Snapshot is gated on `release` so the load
        // can't possibly have completed yet.
        var firstCall = vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.False(firstCall.IsCompleted);

        // Caller B (the deep-link's awaited call): MUST receive the same
        // in-flight task, not a synthetic Task.CompletedTask.
        var secondCall = vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.False(secondCall.IsCompleted);
        Assert.Same(firstCall, secondCall);

        // Release the snapshot RPC; both callers should complete and Alerts
        // should be populated by the time `await secondCall` returns.
        release.SetResult(snapshot);
        await secondCall;

        Assert.Single(vm.Alerts);
        Assert.Equal(@"C:\app.exe", vm.Alerts[0].ProcessPath);
    }

    [Fact]
    public void Ctor_NullDaemonClient_Throws() =>
        Assert.Throws<ArgumentNullException>("daemonClient", () => new AlertsTabViewModel(
            null!,
            new DaemonStreamSubscriber(new FakeDaemonClient(), TimeProvider.System, NullLogger<DaemonStreamSubscriber>.Instance),
            new SyncDispatcher(),
            new FakeNotificationService()));

    [Fact]
    public async Task Dispose_UnsubscribesFromAlertReceivedEvent() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, isRead: true));
        var (vm, _, subscriber, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.Alerts);

        vm.Dispose();

        // Post-dispose, raising the event must not mutate Alerts.
        RaiseAlertReceived(subscriber, new AlertEvent { Alert = MakeAlert(seq: 101, isRead: true) });
        Assert.Single(vm.Alerts);  // still 1, the new event was ignored
    }

    [Fact]
    public async Task LiveAlertReceived_FiresNotificationOnce() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, isRead: true));
        var (vm, _, subscriber, _, notifications) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(notifications.Calls);  // historic alert from snapshot didn't fire

        RaiseAlertReceived(subscriber, new AlertEvent {
            Alert = MakeAlert(seq: 101, processPath: @"C:\bin\firefox.exe", isRead: true),
        });

        var call = Assert.Single(notifications.Calls);
        Assert.Equal(101, call.Seq);
        Assert.Equal(Beholder.Core.AlertKind.NewProcess, call.Kind);
        Assert.Equal("NEW PROCESS", call.Title);
        Assert.Contains("firefox.exe", call.Body);
    }

    [Fact]
    public async Task LiveAlertReceived_DuplicateSeq_NoSecondNotification() {
        // Subscribe arrived before snapshot RPC; the duplicate seq must be
        // deduped both for the in-memory list (existing behavior) AND for
        // the notification fire (new behavior). One toast per alert, ever.
        var snapshot = SnapshotWith(MakeAlert(seq: 100, isRead: true));
        var (vm, _, subscriber, _, notifications) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        RaiseAlertReceived(subscriber, new AlertEvent { Alert = MakeAlert(seq: 100, isRead: true) });

        Assert.Empty(notifications.Calls);
    }

    [Fact]
    public async Task ActivateAsync_LoadingHistoricAlerts_DoesNotFireNotifications() {
        // Snapshot returns 5 historic alerts; opening the tab must not
        // produce 5 toasts. The notification path is gated to the live
        // OnAlertReceived event only.
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, isRead: true),
            MakeAlert(seq: 99, isRead: true),
            MakeAlert(seq: 98, isRead: true),
            MakeAlert(seq: 97, isRead: true),
            MakeAlert(seq: 96, isRead: true));
        var (vm, _, _, _, notifications) = CreateVm(snapshot);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(notifications.Calls);
    }

    [Fact]
    public async Task SelectBySeq_KnownSeq_SetsSelectedAlert() {
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, isRead: true),
            MakeAlert(seq: 99, isRead: true),
            MakeAlert(seq: 98, isRead: true));
        var (vm, _, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        vm.SelectBySeq(99);

        Assert.NotNull(vm.SelectedAlert);
        Assert.Equal(99, vm.SelectedAlert.Seq);
    }

    [Fact]
    public async Task SelectBySeq_UnknownSeq_NoOp() {
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, isRead: true),
            MakeAlert(seq: 99, isRead: true));
        var (vm, _, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        var initialSelection = vm.SelectedAlert;

        vm.SelectBySeq(404);

        Assert.Same(initialSelection, vm.SelectedAlert);  // unchanged
    }

    // ---- Phase 6.9: dismiss-X + auto-clear-on-action-entry ----

    [Fact]
    public async Task DismissErrorCommand_ClearsErrorState() {
        // Trigger a real failure path so HasError + ErrorMessage are set
        // through production code, then verify the command clears both.
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        client.ApplyFirewallRuleException = new InvalidOperationException("apply failed");
        await vm.BlockProcessOutCommand.ExecuteAsync(vm.SelectedAlert);
        Assert.True(vm.HasError);
        Assert.NotEmpty(vm.ErrorMessage);

        vm.DismissErrorCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task BlockProcessOut_ClearsStaleErrorAtEntry_OnSuccess() {
        // First Block fails (sticky banner); second Block on a different
        // alert succeeds and must clear the stale error from the first.
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true),
            MakeAlert(seq: 99, processPath: @"C:\bin\app.exe", isRead: true));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        // First call fails.
        client.ApplyFirewallRuleException = new InvalidOperationException("first fail");
        await vm.BlockProcessOutCommand.ExecuteAsync(vm.Alerts[0]);
        Assert.True(vm.HasError);

        // Clear the throw and call again on a different alert. Auto-clear
        // at entry means the stale error is gone before the new RPC fires.
        client.ApplyFirewallRuleException = null;
        await vm.BlockProcessOutCommand.ExecuteAsync(vm.Alerts[1]);

        Assert.False(vm.HasError);
        Assert.Empty(vm.ErrorMessage);
    }

    // ---- Phase 6.10: orphaned-binary disable ----

    [Fact]
    public async Task SelectingAlertWithMissingExecutable_FlipsIsExecutableMissing() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true));
        // Inject a checker that says "missing" for this path. Selection must
        // flip the row's IsExecutableMissing to true so the view's IsEnabled
        // bindings disable the action buttons.
        var (vm, _, _, _, _) = CreateVm(snapshot, fileExistsCheck: _ => false);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.SelectedAlert);
        Assert.True(vm.SelectedAlert.IsExecutableMissing);
    }

    [Fact]
    public async Task SelectingAlertWithExistingExecutable_KeepsIsExecutableMissingFalse() {
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true));
        var (vm, _, _, _, _) = CreateVm(snapshot, fileExistsCheck: _ => true);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.SelectedAlert);
        Assert.False(vm.SelectedAlert.IsExecutableMissing);
    }

    [Fact]
    public async Task ReSelectingAlert_ReChecksFileExistence() {
        // Mutable checker — file "exists" on first selection, "missing" on
        // re-selection. Pins the "re-check on every OnSelectedAlertChanged"
        // semantics: a binary deleted between selections must reflect on the
        // next click.
        var fileExists = true;
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true),
            MakeAlert(seq: 99, processPath: @"C:\bin\other.exe", isRead: true));
        var (vm, _, _, _, _) = CreateVm(snapshot, fileExistsCheck: _ => fileExists);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);
        var primary = vm.SelectedAlert!;
        Assert.False(primary.IsExecutableMissing);

        // Simulate a deletion between user clicks. Switch selection away
        // and back so OnSelectedAlertChanged re-fires for the original row.
        fileExists = false;
        vm.SelectedAlert = vm.Alerts[1];
        vm.SelectedAlert = primary;

        Assert.True(primary.IsExecutableMissing);
    }

    [Fact]
    public async Task ChainErrorAlert_EmptyProcessPath_NeverSetsIsExecutableMissing() {
        // ChainError alerts have empty ProcessPath by convention. The check
        // must short-circuit on empty path so a checker that interprets ""
        // as "missing" doesn't set a stale flag (the buttons are already
        // hidden via IsVisible for ChainError alerts anyway, but the flag
        // shouldn't lie).
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, kind: AlertKind.ChainError, processPath: "",
                      summary: "chain failed at seq 5", isRead: true));
        // Aggressive checker: even calls with empty path return "missing".
        // Verifies the short-circuit in OnSelectedAlertChanged.
        var (vm, _, _, _, _) = CreateVm(snapshot, fileExistsCheck: _ => false);

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.SelectedAlert);
        Assert.False(vm.SelectedAlert.IsExecutableMissing);
    }

    [Fact]
    public async Task FileExistsCheckThrowing_DefaultsToNotMissing() {
        // SafeFileExists wraps the injected checker in a try/catch and
        // defaults to "exists" on exception — defensive so a transient
        // I/O error (network drive timeout, locked file, malformed path)
        // doesn't lock the user out of the action buttons.
        var snapshot = SnapshotWith(MakeAlert(seq: 100, processPath: @"C:\bin\app.exe", isRead: true));
        var (vm, _, _, _, _) = CreateVm(snapshot,
            fileExistsCheck: _ => throw new UnauthorizedAccessException("permission denied"));

        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.SelectedAlert);
        Assert.False(vm.SelectedAlert.IsExecutableMissing);
    }

    [Fact]
    public async Task BlockProcessOut_ReplacesStaleErrorMessage_OnFreshFailure() {
        // First Block fails with one message; second Block fails with a
        // different message. The new message must replace the old (not be
        // suppressed by the auto-clear-on-entry).
        var snapshot = SnapshotWith(
            MakeAlert(seq: 100, processPath: @"C:\bin\firefox.exe", isRead: true),
            MakeAlert(seq: 99, processPath: @"C:\bin\app.exe", isRead: true));
        var (vm, client, _, _, _) = CreateVm(snapshot);
        await vm.ActivateAsync(TestContext.Current.CancellationToken);

        client.ApplyFirewallRuleException = new InvalidOperationException("FIRST_ERROR_MESSAGE");
        await vm.BlockProcessOutCommand.ExecuteAsync(vm.Alerts[0]);
        Assert.Contains("FIRST_ERROR_MESSAGE", vm.ErrorMessage);

        client.ApplyFirewallRuleException = new InvalidOperationException("SECOND_ERROR_MESSAGE");
        await vm.BlockProcessOutCommand.ExecuteAsync(vm.Alerts[1]);

        Assert.True(vm.HasError);
        Assert.Contains("SECOND_ERROR_MESSAGE", vm.ErrorMessage);
        Assert.DoesNotContain("FIRST_ERROR_MESSAGE", vm.ErrorMessage);
    }
}
