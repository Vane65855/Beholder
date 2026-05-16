using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Beholder.Core;
using Beholder.Ui.Models;

namespace Beholder.Ui.Controls;

/// <summary>
/// Renders the world-map hover tooltip into a <see cref="DrawingContext"/>.
/// Extracted from <see cref="WorldMapControl"/> so the control stays under
/// the CLAUDE.md ~200 LOC class threshold after Phase 8 polish added the
/// top-3 destinations rows and the four distinct tooltip states (Loading
/// / Empty / Populated / Failed / NoFetchYet per UI_QUALITY_STANDARDS §3).
/// </summary>
/// <remarks>
/// <para>
/// Pure layout-and-draw; no state. Each <see cref="Draw"/> call resolves
/// the tooltip's box dimensions from the inputs and renders directly. The
/// font sizes are named constants here (per CODING_STANDARDS.md banned-
/// pattern table on magic numbers + UI_QUALITY_STANDARDS §5 #2 on
/// hardcoded FontSize). The <c>Top3DestinationsLimit</c> constant is the
/// other named magic this file owns.
/// </para>
/// <para>
/// Theme tokens are passed in (not resolved here) so the control owns the
/// brush cache + invalidation on <c>ResourcesChanged</c>; the renderer
/// stays pure and trivially callable.
/// </para>
/// </remarks>
internal static class WorldMapTooltipRenderer {
    public const int Top3DestinationsLimit = 3;

    private const double HeaderFontSize = 13;
    private const double RowFontSize = 11;
    private const double Padding = 8;
    private const double TooltipMargin = 12;
    private const double DividerThickness = 1;
    private const double RowGap = 2;
    private const int LabelMaxChars = 28;

    /// <summary>
    /// Renders the tooltip overlay for the currently hovered country.
    /// Five visually distinct states per UI_QUALITY_STANDARDS §3.1:
    /// (Populated) header + bytes + divider + 3 destination rows;
    /// (Loading) header + bytes + divider + "loading…";
    /// (Empty) header + bytes + divider + "no resolved destinations";
    /// (Failed) header + bytes + divider + "destinations unavailable";
    /// (NoFetchYet) header + bytes only — no divider.
    /// </summary>
    public static void Draw(
        DrawingContext context, Rect bounds,
        string countryName, CountryTrafficSummary? summary,
        IReadOnlyList<DestinationRow>? destinations,
        bool isLoading, bool isEmpty, bool isFailed,
        TooltipBrushes brushes, Typeface typeface
    ) {
        var headerText = BuildHeaderText(countryName, typeface, brushes.Foreground);
        var bytesText = BuildBytesText(summary, typeface, brushes.Foreground);
        var dividerState = ClassifyDividerState(destinations, isLoading, isEmpty, isFailed);
        var rowsText = BuildRowsText(destinations, dividerState, typeface, brushes.Foreground, brushes.Muted);

        var (box, contentTop) = ComputeBox(bounds, headerText, bytesText, rowsText, dividerState);
        DrawBackground(context, box, brushes);
        DrawHeader(context, box, headerText, bytesText);
        if (dividerState != DividerState.None) {
            DrawDivider(context, box, contentTop, headerText.Height, bytesText.Height, brushes.Divider);
        }
        DrawRows(context, box, headerText.Height, bytesText.Height, rowsText, dividerState);
    }

    public static void DrawCenteredCaption(
        DrawingContext context, Rect bounds, string text,
        IBrush foreground, Typeface typeface
    ) {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, HeaderFontSize + 1, foreground);
        context.DrawText(ft, new Point(
            (bounds.Width - ft.Width) / 2,
            (bounds.Height - ft.Height) / 2));
    }

    private static FormattedText BuildHeaderText(string countryName, Typeface typeface, IBrush foreground) =>
        new(countryName, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, HeaderFontSize, foreground);

    private static FormattedText BuildBytesText(CountryTrafficSummary? summary, Typeface typeface, IBrush foreground) {
        var line = summary is null
            ? "no traffic"
            : $"▼ {FormatBytes(summary.TotalBytesIn)}   ▲ {FormatBytes(summary.TotalBytesOut)}";
        return new FormattedText(line, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, RowFontSize, foreground);
    }

    private static DividerState ClassifyDividerState(
        IReadOnlyList<DestinationRow>? destinations, bool isLoading, bool isEmpty, bool isFailed
    ) {
        if (isFailed) return DividerState.Failed;
        if (isLoading) return DividerState.Loading;
        if (isEmpty) return DividerState.Empty;
        if (destinations is { Count: > 0 }) return DividerState.Populated;
        return DividerState.None;   // nothing fetched yet for this country
    }

    private static FormattedText[] BuildRowsText(
        IReadOnlyList<DestinationRow>? destinations, DividerState state,
        Typeface typeface, IBrush foreground, IBrush muted
    ) {
        return state switch {
            DividerState.Loading => new[] { Caption("loading…", typeface, muted) },
            DividerState.Empty => new[] { Caption("no resolved destinations", typeface, muted) },
            DividerState.Failed => new[] { Caption("destinations unavailable", typeface, muted) },
            DividerState.Populated => BuildDestinationRows(destinations!, typeface, foreground),
            _ => System.Array.Empty<FormattedText>(),
        };
    }

    private static FormattedText[] BuildDestinationRows(
        IReadOnlyList<DestinationRow> destinations, Typeface typeface, IBrush foreground
    ) {
        var n = System.Math.Min(destinations.Count, Top3DestinationsLimit);
        var rows = new FormattedText[n];
        for (var i = 0; i < n; i++) {
            var d = destinations[i];
            var label = d.Label.Length > LabelMaxChars
                ? d.Label[..(LabelMaxChars - 1)] + "…"
                : d.Label;
            rows[i] = new FormattedText(
                $"{label}    {FormatBytes(d.TotalBytes)}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, RowFontSize, foreground);
        }
        return rows;
    }

    private static FormattedText Caption(string text, Typeface typeface, IBrush muted) =>
        new(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, RowFontSize, muted);

    private static (Rect Box, double ContentTop) ComputeBox(
        Rect bounds, FormattedText header, FormattedText bytes, FormattedText[] rows, DividerState dividerState
    ) {
        var w = System.Math.Max(header.Width, bytes.Width);
        foreach (var r in rows) if (r.Width > w) w = r.Width;
        w += Padding * 2;

        var h = header.Height + bytes.Height + Padding * 2;
        if (dividerState != DividerState.None) {
            h += DividerThickness + Padding;     // divider line + padding above the rows
            foreach (var r in rows) h += r.Height + RowGap;
        }

        var origin = new Point(
            System.Math.Min(bounds.Width - w - TooltipMargin, TooltipMargin),
            TooltipMargin);
        return (new Rect(origin.X, origin.Y, w, h), origin.Y + Padding);
    }

    private static void DrawBackground(DrawingContext context, Rect box, TooltipBrushes brushes) {
        context.FillRectangle(brushes.Fill, box);
        context.DrawRectangle(null, brushes.BorderPen, box);
    }

    private static void DrawHeader(DrawingContext context, Rect box, FormattedText header, FormattedText bytes) {
        context.DrawText(header, new Point(box.X + Padding, box.Y + Padding));
        context.DrawText(bytes, new Point(box.X + Padding, box.Y + Padding + header.Height));
    }

    private static void DrawDivider(
        DrawingContext context, Rect box, double contentTop,
        double headerH, double bytesH, IBrush dividerBrush
    ) {
        var y = box.Y + Padding + headerH + bytesH + Padding / 2;
        var pen = new Pen(dividerBrush, DividerThickness);
        context.DrawLine(pen,
            new Point(box.X + Padding, y),
            new Point(box.Right - Padding, y));
    }

    private static void DrawRows(
        DrawingContext context, Rect box, double headerH, double bytesH,
        FormattedText[] rows, DividerState dividerState
    ) {
        if (dividerState == DividerState.None) return;
        var y = box.Y + Padding + headerH + bytesH + Padding;
        foreach (var r in rows) {
            context.DrawText(r, new Point(box.X + Padding, y));
            y += r.Height + RowGap;
        }
    }

    private static string FormatBytes(long bytes) {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F2} MB";
        if (bytes >= KB) return $"{bytes / KB:F1} KB";
        return $"{bytes} B";
    }

    private enum DividerState {
        None, Loading, Empty, Failed, Populated,
    }

    /// <summary>
    /// Theme-resolved brushes passed in by the control; the renderer stays
    /// pure (no static state, no resource resolution).
    /// </summary>
    internal readonly record struct TooltipBrushes(
        IBrush Fill,
        IBrush Foreground,
        IBrush Muted,
        IBrush Divider,
        IPen BorderPen);
}
