using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Beholder.Ui.Views.Tabs;

/// <summary>
/// Code-behind for the Settings tab. Two behaviors that don't have clean
/// pure-XAML equivalents in Avalonia 12:
/// <list type="number">
/// <item>Responsive layout (#12) — toggles a <c>"wide"</c> CSS-style class
///   on the root <see cref="UserControl"/> when <see cref="Visual.Bounds"/>
///   crosses <see cref="WideLayoutThresholdPx"/>, which XAML styles
///   consume to rearrange the section cards into a two-column layout
///   above the threshold.</item>
/// <item>Sticky section headers (#13) — keeps the floating
///   <c>StickyHeader</c> border in sync with which section card is
///   currently at the top of the scroll viewport.</item>
/// </list>
/// </summary>
public partial class SettingsTabView : UserControl {
    /// <summary>Window width threshold (in DIPs) at which the section
    /// cards switch from a single-column stack to a two-column grid.
    /// Chosen for a typical desktop monitor where two cards side-by-side
    /// stay readable without each becoming overly narrow.</summary>
    private const double WideLayoutThresholdPx = 1400;

    /// <summary>Above this scroll offset, the sticky header overlay
    /// becomes visible. A small buffer (rather than zero) avoids the
    /// header flashing in and out as the user scrolls past the natural
    /// header position by a pixel or two.</summary>
    private const double StickyHeaderRevealOffsetPx = 80;

    private ScrollViewer? _scrollViewer;
    private Border? _stickyHeader;
    private TextBlock? _stickyHeaderText;
    private readonly List<(string Label, Border Card)> _sectionCards = new();

    public SettingsTabView() {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e) {
        base.OnLoaded(e);
        _scrollViewer = this.FindControl<ScrollViewer>("SettingsScrollViewer");
        _stickyHeader = this.FindControl<Border>("StickyHeader");
        _stickyHeaderText = this.FindControl<TextBlock>("StickyHeaderText");

        // Cache the three section cards in display order. The list order
        // matters for the sticky-header lookup — we walk the list and pick
        // the last card whose top is at-or-above the viewport top.
        _sectionCards.Clear();
        TryAddCard("DATA STORAGE", "DataStorageCard");
        TryAddCard("MAINTENANCE", "MaintenanceCard");
        TryAddCard("ABOUT", "AboutCard");

        if (_scrollViewer is not null) {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }
        UpdateLayoutClass();
    }

    protected override void OnUnloaded(RoutedEventArgs e) {
        if (_scrollViewer is not null) {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        }
        base.OnUnloaded(e);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        UpdateLayoutClass();
        return base.ArrangeOverride(finalSize);
    }

    private void TryAddCard(string label, string controlName) {
        var card = this.FindControl<Border>(controlName);
        if (card is not null) _sectionCards.Add((label, card));
    }

    /// <summary>
    /// Adds or removes the <c>"wide"</c> class based on the current
    /// <see cref="Visual.Bounds"/> width. XAML styles consume the class
    /// to flip the section-card layout between single-column (narrow)
    /// and two-column (wide) modes.
    /// </summary>
    private void UpdateLayoutClass() {
        var isWide = Bounds.Width >= WideLayoutThresholdPx;
        var hasWide = Classes.Contains("wide");
        if (isWide && !hasWide) Classes.Add("wide");
        else if (!isWide && hasWide) Classes.Remove("wide");
    }

    /// <summary>
    /// Updates the floating sticky-header overlay. Walks the section
    /// cards in display order; the "current" section is the last one
    /// whose top edge has scrolled at-or-above the viewport top. Hides
    /// the overlay entirely when scroll offset is small enough that
    /// each section's natural in-flow header is still visible.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_scrollViewer is null || _stickyHeader is null || _stickyHeaderText is null) return;

        var offsetY = _scrollViewer.Offset.Y;
        if (offsetY < StickyHeaderRevealOffsetPx) {
            _stickyHeader.IsVisible = false;
            return;
        }

        string? currentLabel = null;
        foreach (var (label, card) in _sectionCards) {
            // TranslatePoint returns the card's top-left in the ScrollViewer's
            // coordinate space — accounting for the scroll offset. A negative
            // Y means the card's top has scrolled above the viewport top.
            var topInViewport = card.TranslatePoint(default, _scrollViewer)?.Y;
            if (topInViewport is null) continue;
            if (topInViewport <= 0) currentLabel = label;
            else break;
        }

        if (currentLabel is null) {
            _stickyHeader.IsVisible = false;
            return;
        }

        _stickyHeader.IsVisible = true;
        if (_stickyHeaderText.Text != currentLabel) {
            _stickyHeaderText.Text = currentLabel;
        }
    }
}
