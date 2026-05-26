using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Beholder.Ui.Controls;

/// <summary>
/// Two-state ON/OFF pill used in the Settings tab to render runtime-mutable
/// boolean toggles. Click invokes <see cref="Command"/> with
/// <see cref="CommandParameter"/>; the view-model's toggle handler is
/// responsible for the optimistic-flip / RPC / revert dance. This control is
/// purely presentational.
/// </summary>
public partial class SettingsTogglePill : UserControl {
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<SettingsTogglePill, bool>(nameof(IsOn));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<SettingsTogglePill, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<SettingsTogglePill, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<bool> IsSavingProperty =
        AvaloniaProperty.Register<SettingsTogglePill, bool>(nameof(IsSaving));

    /// <summary>
    /// Current ON/OFF state. Bound by the parent view-model; the pill flips
    /// optimistically when the user clicks, then settles to the daemon's
    /// echoed response (or reverts on RPC failure).
    /// </summary>
    public bool IsOn {
        get => GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public ICommand? Command {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>
    /// When true, the pill is disabled (greyed out, no click) to prevent the
    /// user from queuing multiple in-flight Set RPCs for the same toggle.
    /// </summary>
    public bool IsSaving {
        get => GetValue(IsSavingProperty);
        set => SetValue(IsSavingProperty, value);
    }

    public SettingsTogglePill() {
        InitializeComponent();
        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsOnProperty || change.Property == IsSavingProperty) {
            UpdateVisuals();
        }
    }

    private void UpdateVisuals() {
        PillButton.Classes.Remove("on");
        PillButton.Classes.Remove("off");
        if (IsOn) {
            PillButton.Classes.Add("on");
            PillButton.Content = "ON";
        } else {
            PillButton.Classes.Add("off");
            PillButton.Content = "OFF";
        }
        PillButton.IsEnabled = !IsSaving;
    }

    private void OnClick(object? sender, RoutedEventArgs e) {
        if (Command is { } command && command.CanExecute(CommandParameter)) {
            command.Execute(CommandParameter);
        }
    }
}
