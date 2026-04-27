using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Beholder.Ui.Converters;

/// <summary>
/// Returns <c>true</c> when the bound string equals the
/// <c>ConverterParameter</c> ordinal-case-sensitive. Used by the firewall
/// activity strip's kind-badge styling to apply a CSS-style class only when
/// the row's badge class matches the bound class.
/// </summary>
internal sealed class StringEqualsConverter : IValueConverter {
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        var left = value as string ?? string.Empty;
        var right = parameter as string ?? string.Empty;
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
