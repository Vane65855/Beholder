using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Beholder.Ui.Models;

namespace Beholder.Ui.Controls;

public partial class TimeRangeDropdown : UserControl {
    public static readonly StyledProperty<TimeRangeSelection> SelectedRangeProperty =
        AvaloniaProperty.Register<TimeRangeDropdown, TimeRangeSelection>(
            nameof(SelectedRange),
            defaultValue: TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public TimeRangeSelection SelectedRange {
        get => GetValue(SelectedRangeProperty);
        set => SetValue(SelectedRangeProperty, value);
    }

    public TimeRangeDropdown() {
        InitializeComponent();
        UpdateButtonLabel();
        UpdateActiveStates();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedRangeProperty) {
            UpdateButtonLabel();
            UpdateActiveStates();
        }
    }

    private void OnPresetClick(object? sender, RoutedEventArgs e) {
        if (sender is Button btn && btn.Tag is string presetName
            && Enum.TryParse<TimeRangePreset>(presetName, out var preset)) {
            SelectedRange = TimeRangeSelection.FromPreset(preset);
            CloseFlyout();
        }
    }

    private void OnCustomClick(object? sender, RoutedEventArgs e) {
        // Initialize date pickers with sensible defaults
        var now = DateTimeOffset.Now;
        FromPicker.SelectedDate = now.AddDays(-7).DateTime;
        ToPicker.SelectedDate = now.DateTime;

        PresetPanel.IsVisible = false;
        CustomPanel.IsVisible = true;
    }

    private void OnCustomApply(object? sender, RoutedEventArgs e) {
        if (FromPicker.SelectedDate is DateTime from && ToPicker.SelectedDate is DateTime to) {
            try {
                SelectedRange = TimeRangeSelection.FromLocalCalendarDates(from, to);
            } catch (ArgumentException) {
                // User picked an inverted range (toDate < fromDate). Don't
                // update the selection; let the dropdown return to preset
                // mode. A future UX pass can surface inline error text; for
                // now, silent ignore matches the prior behavior.
            }
        }

        PresetPanel.IsVisible = true;
        CustomPanel.IsVisible = false;
        CloseFlyout();
    }

    private void OnCustomCancel(object? sender, RoutedEventArgs e) {
        PresetPanel.IsVisible = true;
        CustomPanel.IsVisible = false;
    }

    private void CloseFlyout() {
        if (DropdownButton?.Flyout is Avalonia.Controls.Flyout flyout)
            flyout.Hide();
    }

    private void UpdateButtonLabel() {
        if (ButtonLabel is not null)
            ButtonLabel.Text = SelectedRange.Label;
    }

    private void UpdateActiveStates() {
        var current = SelectedRange.Preset;
        SetActiveClass(Btn5Min, current == TimeRangePreset.Last5Minutes);
        SetActiveClass(Btn1Hour, current == TimeRangePreset.Last1Hour);
        SetActiveClass(Btn24Hours, current == TimeRangePreset.Last24Hours);
        SetActiveClass(Btn7Days, current == TimeRangePreset.Last7Days);
        SetActiveClass(Btn30Days, current == TimeRangePreset.Last30Days);
        SetActiveClass(BtnAllTime, current == TimeRangePreset.AllTime);
    }

    private static void SetActiveClass(Button? button, bool isActive) {
        if (button is null) return;
        if (isActive)
            button.Classes.Add("active");
        else
            button.Classes.Remove("active");
    }
}
