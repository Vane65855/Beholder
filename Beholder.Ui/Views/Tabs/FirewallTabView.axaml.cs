using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Beholder.Ui.ViewModels;

namespace Beholder.Ui.Views.Tabs;

public partial class FirewallTabView : UserControl {
    /// <summary>
    /// The view-model whose <c>RowScrollRequested</c> event we're currently
    /// subscribed to. Tracked so <see cref="OnDataContextChanged"/> can
    /// unsubscribe before re-wiring against a new VM and avoid leaking the
    /// handler on DataContext swaps. Single-VM lifetime today, but defensive
    /// for the theoretical case where the shell re-assigns the tab.
    /// </summary>
    private FirewallTabViewModel? _wiredVm;

    public FirewallTabView() {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Handles a double-tap on a firewall rule row's outer Border. Copies the
    /// row's parent directory (not the executable's full path) to the system
    /// clipboard and notifies the view-model so it can render a transient
    /// confirmation banner. Avalonia's clipboard requires a <c>TopLevel</c>
    /// reference, so this lives in code-behind rather than the view-model.
    /// </summary>
    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e) {
        if (sender is not Control control) return;
        if (control.DataContext is not FirewallRuleRow row) return;
        if (DataContext is not FirewallTabViewModel vm) return;

        var directory = Path.GetDirectoryName(row.ProcessPath);
        if (string.IsNullOrEmpty(directory)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try {
            // Avalonia 12 moved SetTextAsync from a method on IClipboard onto
            // an extension in Avalonia.Input.Platform.ClipboardExtensions
            // (see Avalonia.Base.xml:26552). Same call site, just an
            // additional `using` brings the extension into scope.
            await clipboard.SetTextAsync(directory);
            vm.NotifyPathCopied(directory);
        } catch {
            // Clipboard access can fail (focus changes, OS paste-board
            // contention, etc.) — silently swallow so a failed copy doesn't
            // crash the UI. The lack of the transient banner is itself the
            // failure indicator.
        }
    }

    /// <summary>
    /// Re-wires the <c>RowScrollRequested</c> handler when the tab's
    /// view-model is assigned (or replaced). Always unsubscribes from the
    /// previous VM (if any) before subscribing to the new one to keep the
    /// event-handler reference count at exactly 1 per live VM instance.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e) {
        if (_wiredVm is not null) {
            _wiredVm.RowScrollRequested -= OnRowScrollRequested;
        }
        _wiredVm = DataContext as FirewallTabViewModel;
        if (_wiredVm is not null) {
            _wiredVm.RowScrollRequested += OnRowScrollRequested;
        }
    }

    /// <summary>
    /// Brings the row's container into view inside the rule list's
    /// ScrollViewer. Tries both ItemsControls — the VM has already expanded
    /// the relevant group, so the matching ItemsControl returns a non-null
    /// container if the row's already realized. For VirtualizingStackPanel
    /// rows that haven't been materialized yet (the panel doesn't think
    /// they're visible at the current scroll position), we subscribe to a
    /// single LayoutUpdated tick on both controls and retry; if still null
    /// after that pass, we give up — likely filtered out by the active
    /// search/filter combination.
    /// </summary>
    private void OnRowScrollRequested(FirewallRuleRow row) {
        if (TryBringIntoView(row)) return;

        EventHandler? handler = null;
        handler = (_, _) => {
            // Unsubscribe first so a single tick doesn't fire the handler
            // twice (LayoutUpdated raises on every layout pass; without
            // unsubscribing, the second subscriber would still be live).
            ActiveRulesItems.LayoutUpdated -= handler!;
            InactiveRulesItems.LayoutUpdated -= handler!;
            // Best-effort second attempt; intentionally no third retry to
            // avoid leaking the handler if the row is filtered out of view.
            TryBringIntoView(row);
        };
        ActiveRulesItems.LayoutUpdated += handler;
        InactiveRulesItems.LayoutUpdated += handler;
    }

    private bool TryBringIntoView(FirewallRuleRow row) {
        if (ActiveRulesItems.ContainerFromItem(row) is Control activeContainer) {
            activeContainer.BringIntoView();
            return true;
        }
        if (InactiveRulesItems.ContainerFromItem(row) is Control inactiveContainer) {
            inactiveContainer.BringIntoView();
            return true;
        }
        return false;
    }
}
