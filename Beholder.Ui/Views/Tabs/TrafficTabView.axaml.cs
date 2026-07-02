using Avalonia.Controls;
using Avalonia.Input;
using Beholder.Ui.Models;
using Beholder.Ui.ViewModels;

namespace Beholder.Ui.Views.Tabs;

public partial class TrafficTabView : UserControl {
    public TrafficTabView() {
        InitializeComponent();
    }

    /// <summary>
    /// Captures which process row a context menu is opening on — the row is
    /// the DataContext of whatever element inside the ListBox was
    /// right-clicked — and hands it to the VM's gating. Suppresses the menu
    /// entirely when the click wasn't on an actionable row (the pinned
    /// all-row, or empty space below the rows, whose inherited DataContext
    /// is the tab VM rather than a <see cref="ProcessListItem"/>).
    /// The commands act on this captured target, not on the ListBox
    /// selection, so they're immune to the 1 Hz live re-sort racing the
    /// click. (Avalonia's ListBox also selects the row on right-click —
    /// kept: it focuses the chart on the process being acted on.)
    /// </summary>
    private void OnProcessListContextRequested(object? sender, ContextRequestedEventArgs e) {
        if (DataContext is not TrafficTabViewModel viewModel) {
            e.Handled = true;
            return;
        }
        var item = (e.Source as Control)?.DataContext as ProcessListItem;
        if (!viewModel.SetProcessMenuTarget(item)) e.Handled = true;
    }
}
