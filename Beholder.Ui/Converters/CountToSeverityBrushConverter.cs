using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Beholder.Ui.Converters;

/// <summary>
/// Returns the theme brush named by <see cref="ConverterParameter"/> (a string
/// resource key) when the bound integer is greater than zero, else returns
/// <c>TextMuted</c>. Used by the Firewall tab's header counts so a zero count
/// doesn't render in an alarming color when there's nothing to alarm about.
/// </summary>
/// <remarks>
/// <para>
/// Example usage in xaml:
/// <code>
/// &lt;Run Foreground="{Binding BlockedProcessCount,
///         Converter={StaticResource CountToSeverityBrushConverter},
///         ConverterParameter=SeverityDanger}" /&gt;
/// </code>
/// renders the count in the danger color when &gt; 0, muted when zero.
/// </para>
/// <para>
/// One-way only — header count is read-only by definition.
/// </para>
/// </remarks>
internal sealed class CountToSeverityBrushConverter : IValueConverter {
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        var count = value switch {
            int i => i,
            long l => l > int.MaxValue ? int.MaxValue : (int)l,
            _ => 0,
        };
        var resourceKey = count > 0 && parameter is string key ? key : "TextMuted";
        var app = Application.Current;
        if (app is not null && ResourceNodeExtensions.TryFindResource(app, resourceKey, out var brush)
            && brush is ISolidColorBrush solid) {
            return solid;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
