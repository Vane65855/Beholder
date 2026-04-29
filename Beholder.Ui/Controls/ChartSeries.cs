using System.Collections.Generic;
using Avalonia.Media;

namespace Beholder.Ui.Controls;

/// <summary>
/// A single data series for the traffic chart.
/// </summary>
internal sealed record ChartSeries(string Name, IReadOnlyList<long> Values, Color Color);
