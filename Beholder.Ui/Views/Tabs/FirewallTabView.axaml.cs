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
    /// Maximum number of LayoutUpdated ticks we'll wait for a freshly-
    /// revealed ItemsControl's panel to materialize before giving up.
    /// Each tick is one layout pass (~ms scale); 10 covers the worst-case
    /// cascade where the IsVisible binding flips, the StackPanel re-lays
    /// out, and only then the ItemsControl realizes its ItemsPanelRoot.
    /// Bounded so a misconfigured panel doesn't leak the handler.
    /// </summary>
    private const int ScrollIntoViewMaxLayoutTicks = 10;

    /// <summary>
    /// Scrolls the row into view. Picks the target ItemsControl from
    /// <c>row.IsActive</c> (the VM has already toggled
    /// <c>IsActiveExpanded</c> / <c>IsInactiveExpanded</c> to match) and
    /// delegates to <c>ItemsControl.ScrollIntoView</c>, which forces panel
    /// materialization for unrealized rows in a VirtualizingStackPanel and
    /// then bubbles the BringIntoView request up to the outer ScrollViewer.
    /// If the group was just expanded, the panel may not exist yet; defer
    /// the call until LayoutUpdated reports a realized ItemsPanelRoot.
    /// </summary>
    private void OnRowScrollRequested(FirewallRuleRow row) {
        var target = row.IsActive ? ActiveRulesItems : InactiveRulesItems;

        if (TryScrollIntoView(target, row)) return;

        var attempts = 0;
        EventHandler? handler = null;
        handler = (_, _) => {
            attempts++;
            if (TryScrollIntoView(target, row)) {
                target.LayoutUpdated -= handler!;
                return;
            }
            if (attempts >= ScrollIntoViewMaxLayoutTicks) {
                target.LayoutUpdated -= handler!;
            }
        };
        target.LayoutUpdated += handler;
    }

    private static bool TryScrollIntoView(ItemsControl items, FirewallRuleRow row) {
        // ItemsPanelRoot is null until the ItemsControl's first layout pass
        // after becoming visible. Once non-null, ScrollIntoView is safe to
        // call even for virtualized (off-viewport) items — it forces the
        // panel to realize the container before raising BringIntoView.
        if (items.ItemsPanelRoot is null) return false;
        items.ScrollIntoView(row);
        return true;
    }
}
