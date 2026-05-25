using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Beholder.Ui.Converters;

/// <summary>
/// Resolves a theme resource key (passed as the bound string value) into
/// its <see cref="IBrush"/>. Lets a ViewModel keep "the brush this pill
/// should use" as a plain string (no Avalonia dependency in the VM) while
/// XAML still gets a live brush that respects theme swaps.
/// </summary>
/// <remarks>
/// One-way only. Returns a neutral gray fallback when the key isn't a
/// resolvable resource — defensive against typos in the VM's key strings
/// without crashing the binding.
/// </remarks>
internal sealed class BrushKeyConverter : IValueConverter {
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is not string key || string.IsNullOrEmpty(key)) return Brushes.Gray;
        var app = Application.Current;
        if (app is not null && ResourceNodeExtensions.TryFindResource(app, key, out var brush)
            && brush is IBrush resolvedBrush) {
            return resolvedBrush;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
