namespace Beholder.Ui.Controls;

/// <summary>
/// A user-selected time range on the traffic chart, expressed as normalized
/// fractions in [0,1] across the chart's plot width (left edge = oldest sample,
/// right edge = newest). The control reports the selection in plot-relative
/// terms; the view-model maps the fractions to absolute times against the
/// chart's current window. A null <see cref="ChartSelectionRange"/> means no
/// active selection.
/// </summary>
internal readonly record struct ChartSelectionRange(double StartFraction, double EndFraction);
