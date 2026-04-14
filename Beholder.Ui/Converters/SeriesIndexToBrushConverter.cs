using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Beholder.Ui.Helpers;

namespace Beholder.Ui.Converters;

/// <summary>
/// Converts a series index (1-12) to the corresponding Series{NN} SolidColorBrush
/// from the application's theme resources.
/// </summary>
internal sealed class SeriesIndexToBrushConverter : IValueConverter {
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is int index and >= 1 and <= 12) {
            var key = SeriesColorHelper.GetBrushResourceKey(index);
            var app = Application.Current;
            if (app is not null && ResourceNodeExtensions.TryFindResource(app, key, out var brush)
                && brush is ISolidColorBrush)
                return brush;
        }
        return Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
