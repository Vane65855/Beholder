using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Beholder.Ui.Views;

public partial class WindowControls : UserControl {
    public WindowControls() {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        // VisualRoot returns TopLevelHost (not Window) when ExtendClientAreaToDecorationsHint is enabled.
        if (TopLevel.GetTopLevel(this) is Window window) {
            UpdateGlyphs(window.WindowState);
            window.PropertyChanged += OnWindowPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.PropertyChanged -= OnWindowPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState state)
            UpdateGlyphs(state);
    }

    private void UpdateGlyphs(WindowState state) {
        MaximizePath.IsVisible = state != WindowState.Maximized;
        RestorePath.IsVisible = state == WindowState.Maximized;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is Window window) {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }
}
