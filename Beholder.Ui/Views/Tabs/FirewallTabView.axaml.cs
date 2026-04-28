using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Beholder.Ui.ViewModels;

namespace Beholder.Ui.Views.Tabs;

public partial class FirewallTabView : UserControl {
    public FirewallTabView() {
        InitializeComponent();
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
}
