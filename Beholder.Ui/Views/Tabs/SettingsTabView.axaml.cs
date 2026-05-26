using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Beholder.Ui.Views.Tabs;

/// <summary>
/// Code-behind for the Settings tab. Implements the sticky section
/// header overlay (#13 from the Phase 13.1.1 brainstorm) — a single
/// concern that doesn't have a clean pure-XAML equivalent in Avalonia 12.
/// The responsive 2-column layout (#12) was dropped in Phase 13.1.2 in
/// favor of a single-column full-width layout; the corresponding
/// <c>ArrangeOverride</c> / class-toggling logic was removed at the
/// same time.
/// </summary>
public partial class SettingsTabView : UserControl {
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
    }

    protected override void OnUnloaded(RoutedEventArgs e) {
        if (_scrollViewer is not null) {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        }
        base.OnUnloaded(e);
    }

    private void TryAddCard(string label, string controlName) {
        var card = this.FindControl<Border>(controlName);
        if (card is not null) _sectionCards.Add((label, card));
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
