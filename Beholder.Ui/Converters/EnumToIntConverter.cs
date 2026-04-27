using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Beholder.Ui.Converters;

/// <summary>
/// Two-way enum↔int converter for binding <c>ComboBox.SelectedIndex</c>
/// against a typed enum property in a view-model. Necessary because
/// Avalonia's binding system doesn't auto-coerce <c>int</c> to a specific
/// enum without an explicit converter even when the enum's underlying
/// type is <c>int</c>.
/// </summary>
internal sealed class EnumToIntConverter : IValueConverter {
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is null) return 0;
        return System.Convert.ToInt32(value, culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is null) return Activator.CreateInstance(targetType);
        if (targetType.IsEnum) {
            var asInt = System.Convert.ToInt32(value, culture);
            return Enum.IsDefined(targetType, asInt) ? Enum.ToObject(targetType, asInt) : Activator.CreateInstance(targetType);
        }
        return value;
    }
}
