using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Beholder.Ui.ViewModels;

namespace Beholder.Ui.Controls;

/// <summary>
/// Three-state action pill used in the Firewall tab's rule table. Visual
/// state is driven by <see cref="State"/>; click invokes <see cref="Command"/>
/// with <see cref="CommandParameter"/>. The view-model is responsible for
/// computing the next state and dispatching the corresponding RPC — this
/// control is purely presentational.
/// </summary>
public partial class FirewallActionPill : UserControl {
    public static readonly StyledProperty<object?> StateProperty =
        AvaloniaProperty.Register<FirewallActionPill, object?>(nameof(State));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<FirewallActionPill, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<FirewallActionPill, object?>(nameof(CommandParameter));

    /// <summary>
    /// Backing value for the visual state. Bound to
    /// <see cref="FirewallRuleRow.InAction"/> / <see cref="FirewallRuleRow.OutAction"/>;
    /// boxed as <see cref="object"/> to keep this control's public surface
    /// independent of the internal <see cref="FirewallActionState"/> enum.
    /// </summary>
    public object? State {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public ICommand? Command {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public FirewallActionPill() {
        InitializeComponent();
        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty) {
            UpdateVisuals();
        }
    }

    private void UpdateVisuals() {
        // Reset and re-apply class — Avalonia's Classes is a set so we can
        // toggle membership rather than reassigning the entire collection.
        PillButton.Classes.Remove("allow");
        PillButton.Classes.Remove("block");
        PillButton.Classes.Remove("default");

        // The bound value can come in as a boxed enum (live binding),
        // an int (literal in xaml), or null (unset). Coerce to the enum
        // and fall through to "default" for any unrecognized value.
        var state = State switch {
            FirewallActionState s => s,
            int i when Enum.IsDefined(typeof(FirewallActionState), i) => (FirewallActionState)i,
            _ => FirewallActionState.Default,
        };

        switch (state) {
            case FirewallActionState.Allow:
                PillButton.Classes.Add("allow");
                PillButton.Content = "ALLOW";
                break;
            case FirewallActionState.Block:
                PillButton.Classes.Add("block");
                PillButton.Content = "BLOCK";
                break;
            default:
                PillButton.Classes.Add("default");
                // "+" reads as "click to add a rule" — meaningfully more
                // affordant than the prior "—" which looked like "no value".
                PillButton.Content = "+";
                break;
        }
    }

    private void OnClick(object? sender, RoutedEventArgs e) {
        if (Command is { } command && command.CanExecute(CommandParameter)) {
            command.Execute(CommandParameter);
        }
    }
}
