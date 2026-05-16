using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Beholder.Core;
using Beholder.Ui.Models;
using Beholder.Ui.Services;

namespace Beholder.Ui.Controls;

/// <summary>
/// Custom Canvas world heatmap. Mirrors <see cref="TrafficChartControl"/>'s
/// shape: data flows in via two <see cref="StyledProperty{T}"/>, theme
/// brushes are resolved lazily and cached, render allocations are kept off
/// the steady-state path. The map renders the embedded Natural Earth 110m
/// country polygons under an equirectangular projection, fills each
/// country from the <see cref="HeatmapPalette"/> 5-stop ramp according to
/// its share of <see cref="MaxBytes"/>, and overlays a hover tooltip with
/// the country name + bytes in/out.
/// </summary>
/// <remarks>
/// Per ADR 008 the Windows-platform UI code lives inline in this project;
/// this control is cross-platform Avalonia code with no platform guards.
/// See UI_DESIGN.md §5.11 for the design-language spec.
/// </remarks>
internal sealed class WorldMapControl : Control {
    public static readonly StyledProperty<IReadOnlyList<CountryTrafficSummary>?> CountriesProperty =
        AvaloniaProperty.Register<WorldMapControl, IReadOnlyList<CountryTrafficSummary>?>(nameof(Countries));

    public static readonly StyledProperty<long> MaxBytesProperty =
        AvaloniaProperty.Register<WorldMapControl, long>(nameof(MaxBytes));

    private string? _hoveredIso2;
    private IBrush? _oceanBrush;
    private IBrush? _tooltipFillBrush;
    private IBrush? _tooltipBorderBrush;
    private IPen? _borderPen;
    private IPen? _tooltipBorderPen;
    private HeatmapPalette? _palette;
    private Typeface? _tooltipTypeface;
    private IBrush? _tooltipForegroundBrush;

    static WorldMapControl() {
        AffectsRender<WorldMapControl>(CountriesProperty, MaxBytesProperty);
    }

    public WorldMapControl() {
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

    public IReadOnlyList<CountryTrafficSummary>? Countries {
        get => GetValue(CountriesProperty);
        set => SetValue(CountriesProperty, value);
    }

    public long MaxBytes {
        get => GetValue(MaxBytesProperty);
        set => SetValue(MaxBytesProperty, value);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        ResourcesChanged += OnResourcesChanged;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        ResourcesChanged -= OnResourcesChanged;
    }

    private void OnResourcesChanged(object? sender, Avalonia.Controls.ResourcesChangedEventArgs e) {
        // Theme swap (Dark ↔ Light): drop cached brushes so the next
        // render resolves from the new token dictionary. Mirrors
        // TrafficChartControl.OnResourcesChanged.
        ClearResourceCache();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context) {
        var bounds = new Rect(Bounds.Size);
        EnsureResourcesResolved();
        context.FillRectangle(_oceanBrush!, bounds);

        var shapes = WorldGeometryLoader.LoadOnce();
        if (shapes.Count == 0) {
            // Asset corrupt or missing — render the empty ocean and a
            // small unavailability caption rather than crashing.
            DrawCenteredCaption(context, bounds, "World map unavailable");
            return;
        }

        var byIso = BuildLookup(Countries);
        foreach (var shape in shapes) {
            var totalBytes = byIso.TryGetValue(shape.Iso2, out var s)
                ? s.TotalBytesIn + s.TotalBytesOut
                : 0L;
            var fill = _palette!.BrushFor(totalBytes, MaxBytes);
            DrawShape(context, shape, bounds, fill);
        }

        if (_hoveredIso2 is not null) {
            DrawHoverTooltip(context, bounds, shapes, byIso);
        }
    }

    private void EnsureResourcesResolved() {
        _oceanBrush ??= ResolveBrush("BackgroundPanel");
        _borderPen ??= new Pen(ResolveBrush("BorderSubtle"), 0.5);
        _tooltipFillBrush ??= ResolveBrush("BackgroundElevated");
        _tooltipBorderBrush ??= ResolveBrush("BorderStrong");
        _tooltipBorderPen ??= new Pen(_tooltipBorderBrush, 1.0);
        _tooltipForegroundBrush ??= ResolveBrush("TextPrimary");
        _tooltipTypeface ??= new Typeface("Inter");
        _palette ??= HeatmapPalette.Resolve();
    }

    private void ClearResourceCache() {
        _oceanBrush = null;
        _borderPen = null;
        _tooltipFillBrush = null;
        _tooltipBorderBrush = null;
        _tooltipBorderPen = null;
        _tooltipForegroundBrush = null;
        _palette = null;
    }

    private static Dictionary<string, CountryTrafficSummary> BuildLookup(
        IReadOnlyList<CountryTrafficSummary>? countries
    ) {
        var dict = new Dictionary<string, CountryTrafficSummary>(System.StringComparer.Ordinal);
        if (countries is null) return dict;
        foreach (var c in countries) dict[c.Country.Value] = c;
        return dict;
    }

    private static void DrawShape(DrawingContext context, CountryShape shape, Rect bounds, IBrush fill) {
        foreach (var ring in shape.Rings) {
            if (ring.Count < 3) continue;
            var geom = new StreamGeometry();
            using (var ctx = geom.Open()) {
                ctx.BeginFigure(WorldMapProjection.Project(ring[0], bounds), isFilled: true);
                for (var i = 1; i < ring.Count; i++) {
                    ctx.LineTo(WorldMapProjection.Project(ring[i], bounds));
                }
                ctx.EndFigure(isClosed: true);
            }
            context.DrawGeometry(fill, null, geom);
        }
    }

    private void DrawHoverTooltip(
        DrawingContext context, Rect bounds, IReadOnlyList<CountryShape> shapes,
        IReadOnlyDictionary<string, CountryTrafficSummary> byIso
    ) {
        string name = _hoveredIso2!;
        foreach (var s in shapes) {
            if (s.Iso2 == _hoveredIso2) { name = s.Name; break; }
        }
        byIso.TryGetValue(_hoveredIso2!, out var summary);
        var line2 = summary is null
            ? "no traffic"
            : $"▼ {FormatBytes(summary.TotalBytesIn)}   ▲ {FormatBytes(summary.TotalBytesOut)}";

        var ft1 = new FormattedText(name, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _tooltipTypeface!.Value, 13, _tooltipForegroundBrush);
        var ft2 = new FormattedText(line2, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _tooltipTypeface!.Value, 11, _tooltipForegroundBrush);

        const double pad = 8;
        var w = System.Math.Max(ft1.Width, ft2.Width) + pad * 2;
        var h = ft1.Height + ft2.Height + pad * 2;
        var origin = new Point(
            System.Math.Min(bounds.Width - w - 12, 12),
            12);
        var box = new Rect(origin.X, origin.Y, w, h);
        context.FillRectangle(_tooltipFillBrush!, box);
        context.DrawRectangle(null, _tooltipBorderPen, box);
        context.DrawText(ft1, new Point(box.X + pad, box.Y + pad));
        context.DrawText(ft2, new Point(box.X + pad, box.Y + pad + ft1.Height));
    }

    private void DrawCenteredCaption(DrawingContext context, Rect bounds, string text) {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _tooltipTypeface!.Value, 14, _tooltipForegroundBrush);
        context.DrawText(ft, new Point(
            (bounds.Width - ft.Width) / 2,
            (bounds.Height - ft.Height) / 2));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) {
        var bounds = new Rect(Bounds.Size);
        var screenPoint = e.GetPosition(this);
        var geo = WorldMapProjection.Unproject(screenPoint, bounds);
        var shapes = WorldGeometryLoader.LoadOnce();
        var iso = WorldMapHitTester.FindCountryAt(geo, shapes);
        if (iso != _hoveredIso2) {
            _hoveredIso2 = iso;
            InvalidateVisual();
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e) {
        if (_hoveredIso2 is not null) {
            _hoveredIso2 = null;
            InvalidateVisual();
        }
    }

    private static string FormatBytes(long bytes) {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F2} MB";
        if (bytes >= KB) return $"{bytes / KB:F1} KB";
        return $"{bytes} B";
    }

    private static IBrush ResolveBrush(string tokenName) {
        var app = Application.Current;
        if (app is not null
            && ResourceNodeExtensions.TryFindResource(app, tokenName, out var res)
            && res is ISolidColorBrush brush) {
            return brush;
        }
        return Brushes.Gray;
    }
}
