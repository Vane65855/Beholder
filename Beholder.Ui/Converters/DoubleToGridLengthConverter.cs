using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Beholder.Ui.Converters;

internal sealed class DoubleToGridLengthConverter : IValueConverter {
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is double ratio)
            return new GridLength(Math.Max(ratio, 0.0001), GridUnitType.Star);
        return new GridLength(0, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
