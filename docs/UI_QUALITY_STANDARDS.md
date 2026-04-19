# Beholder NMT — UI Quality Standards

## 1. Purpose and Scope

This document defines non-negotiable quality requirements for every UI phase in the Beholder NMT project. Any phase that produces Avalonia XAML, Views, ViewModels, styles, theme resources, or converters must comply with every section of this document. Backend-only phases (daemon, storage, protocol, platform providers) are exempt. The requirements exist because Phases 5.1 through 5.3 repeatedly produced elements that worked at one resolution with one data set but broke or looked cheap under real-world variation. The quality bar going forward is the standard set by GlassWire, Windows Task Manager, and Resource Monitor — applications where every surface a user touches in the first five minutes feels intentional. Beholder NMT competes with commercial tools; any UI element that feels like a demo prototype represents a brand failure.

---

## 2. Responsive Sizing Rules

### 2.1. Pixel Widths Are a Smell

An explicit `Width="N"` or `Height="N"` on a content element is almost always wrong. Content elements are buttons, list items, text blocks, chart segments, table columns, rectangles, and any container whose size should respond to its parent or its content.

**Acceptable uses of fixed pixel dimensions:**

- Icons at a fixed visual size (a 16px status dot, a 10px path glyph)
- Margins and paddings (`Margin="16,4"`, `Padding="12,6"`)
- Border thicknesses (`BorderThickness="0,1,0,0"`)
- Fixed-ratio decorations (a 10px-high ratio bar, a 2px bottom border indicator)

**Unacceptable uses:**

- Tab button widths
- Chart bar widths or chart container widths
- Table column widths (use `MinWidth`/`MaxWidth` instead)
- Any content that should flex with its container

```xml
<!-- WRONG: content rectangle with fixed width -->
<Rectangle Width="140" Fill="{DynamicResource ChartOutboundStroke}" />

<!-- CORRECT: ratio bar using star-unit columns that flex with the window -->
<!-- See Beholder.Ui/Views/StatusStrip.axaml lines 49-52 -->
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="{Binding InboundRatio,
        Converter={StaticResource RatioToGridLength}}" />
    <ColumnDefinition Width="{Binding OutboundRatio,
        Converter={StaticResource RatioToGridLength}}" />
</Grid.ColumnDefinitions>
```

### 2.2. MinWidth and MaxWidth as Floors and Ceilings

Content sizes itself to fit naturally, but never collapses below a readable minimum and never balloons past a useful maximum. Use `MinWidth` and `MaxWidth` to express these boundaries.

```xml
<!-- CORRECT: nav tab button has a minimum width so short labels don't produce
     tiny click targets, but no MaxWidth — the button can grow if needed -->
<!-- See Beholder.Ui/Views/TopNavBar.axaml line 100 -->
<Setter Property="MinWidth" Value="116" />
<Setter Property="Padding" Value="12,6" />

<!-- CORRECT: ratio bar container has a MinWidth so it remains visible
     even when side columns consume most of the row -->
<!-- See Beholder.Ui/Views/StatusStrip.axaml line 46 -->
<Border MinWidth="100" HorizontalAlignment="Stretch" ... />
```

### 2.3. Grid Star Columns for Proportional Space

When elements must share space proportionally, use Grid columns with `*` (star) units. Star columns divide available space by ratio, automatically adjusting on resize without any code-behind.

```xml
<!-- CORRECT: main window layout — nav and status strip take what they need,
     content area fills the rest -->
<!-- See Beholder.Ui/Views/MainWindow.axaml line 22 -->
<Grid RowDefinitions="Auto,*,Auto">
    <views:TopNavBar Grid.Row="0" />
    <ContentControl Grid.Row="1" Content="{Binding ActiveTabContent}" />
    <views:StatusStrip Grid.Row="2" DataContext="{Binding StatusStripVm}" />
</Grid>

<!-- CORRECT: status strip — metrics take Auto width, ratio bar fills the star column -->
<!-- See Beholder.Ui/Views/StatusStrip.axaml line 13 -->
<Grid ColumnDefinitions="Auto,Auto,*,Auto,Auto" Margin="16,4">
```

For dynamic proportional splits where the ratio changes at runtime, bind column widths to ViewModel properties through a converter that returns `GridLength` with `GridUnitType.Star`. See `Beholder.Ui/Converters/DoubleToGridLengthConverter.cs` for the implementation and `StatusStrip.axaml` lines 49–52 for its use.

### 2.4. StackPanel vs Grid Decision Rule

**Use StackPanel** when items should naturally size to their content and flow in a single direction. The brand block in the top nav bar is a StackPanel — each text element takes only the space its text requires, and they stack horizontally with uniform spacing.

```xml
<!-- StackPanel: items size to content, no proportional splitting needed -->
<!-- See Beholder.Ui/Views/TopNavBar.axaml lines 20-41 -->
<StackPanel Orientation="Horizontal" VerticalAlignment="Center" Spacing="6">
    <TextBlock Text="BEHOLDER NMT" ... />
    <TextBlock Text="v0.1.0" ... />
</StackPanel>
```

**Use Grid** when columns or rows need controlled proportions, when elements must align across rows, or when star-unit space distribution is required. The top nav bar itself is a Grid — the brand block, tab buttons, and window controls occupy defined columns with star spacers centering the tabs.

```xml
<!-- Grid: controlled proportions with star spacers -->
<!-- See Beholder.Ui/Views/TopNavBar.axaml line 17 -->
<Grid ColumnDefinitions="Auto,*,Auto,*,Auto" Margin="16,8,0,8">
```

**Decision rule:** If you need proportional space distribution, alignment across multiple rows, or star-unit columns — use Grid. If items simply flow sequentially and size to their content — use StackPanel. When in doubt, use Grid.

### 2.5. Avalonia-Specific Gotchas

These are platform-specific pitfalls that have caused bugs in this project. Each is documented here because the fix is non-obvious.

**CharacterSpacing is measured in 1/1000 em units in WPF, but Avalonia uses `LetterSpacing` with direct em-like values.** A value of `200` in WPF CharacterSpacing is roughly equivalent to `LetterSpacing="2"` in Avalonia. Using WPF-scale values in Avalonia produces catastrophically wide text. Always test letter-spaced text visually.

```xml
<!-- CORRECT: Avalonia LetterSpacing uses direct values, not 1/1000 em -->
<!-- See Beholder.Ui/Views/TopNavBar.axaml line 25 -->
<TextBlock Text="BEHOLDER NMT" LetterSpacing="2" ... />

<!-- WRONG: WPF-scale value produces enormous letter spacing in Avalonia -->
<TextBlock Text="BEHOLDER NMT" LetterSpacing="200" ... />
```

**TopLevel.GetTopLevel(this) is required when ExtendClientAreaToDecorationsHint is enabled.** With custom window chrome, `this.VisualRoot` returns a `TopLevelHost`, not the `Window`. Casting it to `Window` fails silently. Always use `TopLevel.GetTopLevel(this) as Window` to obtain the actual window reference for operations like minimize, maximize, and close.

**Button with fixed Width + HorizontalContentAlignment="Center" prevents text-change layout shift.** When a button's text changes (e.g., a toggle label), the button width changes if it sizes to content, causing sibling elements to reflow. Setting a fixed `Width` on the button (not its container) with centered content absorbs the text change without layout jitter. This is an acceptable use of fixed pixel width — the button is a fixed-size interactive target, not flexing content.

**ClipToBounds="True" on containers with rounded corners.** When inner content may fill or overflow a rounded-corner container, the content renders outside the border radius unless `ClipToBounds="True"` is set. This is especially visible during animations or when content transitions from empty to populated.

```xml
<!-- CORRECT: ratio bar container clips inner rectangles to its rounded border -->
<!-- See Beholder.Ui/Views/StatusStrip.axaml line 42 -->
<Border Height="10" CornerRadius="3" ClipToBounds="True" ... >
    <Grid IsVisible="{Binding HasTraffic}">
        <Rectangle Grid.Column="0" Fill="..." />
        <Rectangle Grid.Column="1" Fill="..." />
    </Grid>
</Border>
```

### 2.6. List Virtualization

Any `ItemsControl`, `ListBox`, or `DataGrid` that may display 100 or more items must use virtualization. An unvirtualized list with real process data (a typical machine has 200–500 processes over a session) will produce visible lag on scroll and increased memory consumption.

For `ItemsControl`, set `ItemsPanel` to a `VirtualizingStackPanel`:

```xml
<ItemsControl ItemsSource="{Binding Processes}">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel />
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
</ItemsControl>
```

`ListBox` and `DataGrid` use virtualizing panels by default in Avalonia, but verify this has not been overridden.

---

## 3. Required States for Every UI Element

Every non-trivial UI element — any view, panel, list, chart, or data display — must explicitly implement the following states. The phase prompt must list which states it implements. "Implemented populated state only" is a partial delivery with known gaps, not a completed work item.

### 3.1. Loading State

What renders during the first 500ms–2s after the view opens, before data arrives from the daemon. A skeleton placeholder, a spinner, or a text indicator. The loading state must be visually distinct from both the empty state and the populated state. NEVER leave the view blank-but-empty during loading, because the user cannot distinguish "loading" from "broken."

### 3.2. Empty State

What renders when the data source has zero rows matching the current filter, time range, or view mode. An empty process list needs a message like "No processes with network traffic in this time range," not a blank scroll area. The empty state should include:

- A short explanation of why the view is empty
- Where applicable, a hint about what will cause data to appear

### 3.3. Populated State

The normal happy path with real data. This is the state most developers build first and often the only state they build. It is necessary but not sufficient.

### 3.4. Error State

What renders when the daemon disconnects mid-session, the gRPC stream fails, or a request times out. Usually a muted inline message within the affected view, not a modal popup. The rest of the application remains functional — only the affected panel shows the error state.

### 3.5. Extreme State

What the element does with 10x the expected data volume. Does the process list remain usable at 500 rows? Does the chart remain readable with 50 overlaid events? Does the timeline scroll smoothly with 200 entries? Extreme-state testing catches virtualization omissions, layout overflow, and label collision issues that do not appear with toy data.

---

## 4. Interaction Affordances

### 4.1. Hover States

Every clickable element must have a hover state. The default pattern is a subtle background tint using the `BackgroundHover` theme token. The active tab button in `TopNavBar.axaml` demonstrates the pattern:

```xml
<!-- See Beholder.Ui/Views/TopNavBar.axaml lines 109-111 -->
<Style Selector="Button.navTab:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="{DynamicResource TextPrimary}" />
</Style>
```

Elements without hover feedback feel dead. Users cannot tell what is interactive.

### 4.2. Focus States

Keyboard navigation must work for all interactive elements. Avalonia provides a default focus rectangle that is adequate for the initial release. Document any views where keyboard navigation is incomplete as an acknowledged deferral (see Section 9).

### 4.3. Cursor Changes

- Buttons and clickable elements: `Cursor="Hand"`
- Draggable or resizable elements: appropriate resize/move cursor (`SizeWE`, `SizeNS`, `SizeAll`)
- Text-input-adjacent elements: `Cursor="IBeam"`

```xml
<!-- CORRECT: nav tab buttons use Hand cursor -->
<!-- See Beholder.Ui/Views/TopNavBar.axaml line 102 -->
<Setter Property="Cursor" Value="Hand" />
```

### 4.4. Tooltip Discipline

Every non-obvious element must have a tooltip. Tooltip copy is 1–2 short sentences explaining what the element shows and any non-obvious semantics. The status strip demonstrates proper tooltip usage on every metric group and the ratio bar:

```xml
<!-- See Beholder.Ui/Views/StatusStrip.axaml line 17 -->
ToolTip.Tip="Download traffic. Σ = total bytes received since daemon started.
    Rate = bytes received in the last second."

<!-- See Beholder.Ui/Views/StatusStrip.axaml line 47 -->
ToolTip.Tip="Download/upload ratio. Teal = download share, purple = upload share."
```

Tooltips must not be paragraphs. If an element needs a paragraph of explanation, the element itself is too complex or under-labeled.

### 4.5. Animation and Transitions

Value changes should update smoothly, not jerkily. The status strip ratio bar demonstrates the pattern — a LERP smoothing factor of 0.3 per update tick reaches the target value in approximately 3–4 ticks, preventing visual jitter during bursty traffic:

```csharp
// See Beholder.Ui/ViewModels/StatusStripViewModel.cs lines 13-15, 96-103
private const double SmoothingFactor = 0.3;

private void UpdateRatioBar(long outRate, long inRate) {
    var total = outRate + inRate;
    HasTraffic = total > 0;
    if (!HasTraffic) return;

    var targetOutRatio = outRate / (double)total;
    OutboundRatio = OutboundRatio * (1 - SmoothingFactor)
        + targetOutRatio * SmoothingFactor;
}
```

Apply this pattern wherever a numeric value animates in the UI: chart values, progress indicators, gauge fills. Sudden jumps signal missed frames. Overdone easing or spring physics signal misplaced effort.

---

## 5. Banned Patterns

The following patterns automatically fail review. No exceptions, no justifications.

1. **`Width="N"` or `Height="N"` on content containers.** Use `MinWidth`/`MaxWidth`, star columns, or `Auto` sizing. Fixed dimensions on content elements produce layouts that break at every resolution except the one the developer tested.

2. **`FontSize` hardcoded to a raw number on individual elements.** Use theme tokens, style selectors, or inherit from a parent style. Scattered font sizes become unmaintainable and drift from the type scale.

3. **Color values hardcoded as hex in XAML outside theme files.** All colors come from `{DynamicResource TokenName}`. Hardcoded hex values bypass the theme system, break light-theme support, and create invisible coupling between files.

4. **Empty-but-blank states.** If a view can be empty or loading and renders nothing in that state, it is a bug. The user cannot distinguish "loading" from "crashed." See Section 3.

5. **`ItemsControl` with 100+ potential items without virtualization.** Unvirtualized lists with real process data (200–500 processes over a session) produce visible lag and excessive memory consumption. See Section 2.6.

6. **Clickable elements without hover feedback.** A button, toggle, link, or selectable row without a `:pointerover` style change feels dead. Users cannot determine what is interactive. See Section 4.1.

7. **Popups for routine operations.** Modal interruption is reserved for destructive or irreversible actions ("Are you sure you want to delete this firewall rule?"). Informational messages ("Daemon reconnected," "New process detected") must use inline indicators, status bar updates, or notification toasts — never modal dialogs.

8. **Hardcoded text that should be theme-configurable or ViewModel-bound.** Tab labels, empty state messages, tooltip copy, and status messages must be either XAML literal strings (acceptable for v1) or bound to ViewModel properties (for future i18n). They must never be constructed via string concatenation in code-behind with embedded English fragments that cannot be found by a search.

---

## 6. Verification Requirements

Every UI phase must include verification at four levels. Each phase's verification section must explicitly state which checks were performed and at what values. Omitting a check is acknowledged-partial — the phase is incomplete without all four.

### 6.1. Three Window Sizes

Capture screenshots and verify correctness at:

- **Minimum supported size:** 1100x720 (the `MinWidth`/`MinHeight` from `MainWindow.axaml`)
- **Default size:** 1280x800 (the initial `Width`/`Height` from `MainWindow.axaml`)
- **Maximized:** at the developer's actual monitor resolution

At each size, verify: nothing clips, nothing overlaps, nothing becomes unreadable, no element looks stretched or cramped or misaligned relative to its siblings.

### 6.2. Thirty-Second Minimum Daemon Uptime

Screenshots must be taken after the daemon has been running for at least 30 seconds with real network activity, not immediately after startup when data is sparse. Startup screenshots show a near-empty UI that looks acceptable; 30-second screenshots show the UI under real load with populated process lists, non-zero counters, and active chart data.

### 6.3. Real Data

All verification must use real data generated by actual network activity (opening a browser, starting a download, running a build). Fabricated test data with conveniently sized values and perfectly distributed categories will not surface layout issues caused by long process paths, extreme byte counts, or unevenly distributed country codes.

### 6.4. At Least One Extreme Scenario

For each element type introduced in the phase, test with at least:

- **Lists:** 50+ rows
- **Charts:** 5+ minutes of continuous data
- **Timelines:** 20+ events
- **Tables:** enough rows to require scrolling, plus at least one row with an unusually long value in every text column

---

## 7. Reference Comparison

Before marking a UI phase complete, the implementation must be visually compared to at least one reference application that solves a similar problem well. The purpose is to calibrate quality expectations against shipping software, not to copy another product's design.

**Reference applications by element type:**

- Network traffic charts, per-process lists: **GlassWire**, **Windows Task Manager** (Performance tab), **Resource Monitor**
- Firewall rules tables: **Windows Firewall with Advanced Security**, **UFW GUI frontends**
- Alert/event timelines: **Windows Event Viewer**, **Splunk** search results
- Geographic traffic maps: **GlassWire** (Things tab map view)

The comparison must be written in prose in the phase's manual test document. Example:

> Compared against GlassWire's Usage tab at 1920x1080. Beholder's per-process list matches density and selectability. Differences: Beholder shows fewer columns by default (no host column yet), Beholder's chart uses stacked areas where GlassWire uses overlaid lines. The chart re-lays axis labels correctly on resize; GlassWire does the same but also adjusts time-axis granularity, which Beholder does not yet do (deferred).

If the implementation feels noticeably inferior to the reference — less responsive, sparser, uglier, jerkier — the phase is not complete. Specific inferiorities are either fixed or explicitly deferred with tracking in the phase summary.

---

## 8. Phase Prompt Template Requirements

Every UI phase prompt must include the following structure. Phases that omit these sections should be rejected at plan review.

### 8.1. Quality Standards Reference

The prompt must begin with an explicit instruction:

> Read `docs/UI_QUALITY_STANDARDS.md` before starting.

This ensures the quality bar is loaded into context before any code is written.

### 8.2. States Implemented

A section titled "States implemented" listing which of the five required states from Section 3 this phase delivers for each UI element. Example:

> **Process list:** Loading (skeleton rows), Empty ("No processes with traffic"), Populated, Error ("Daemon disconnected — last data shown is stale"), Extreme (tested with 200+ processes).
>
> **Traffic chart:** Loading (empty axes with "Waiting for data" label), Populated, Extreme (tested with 10 minutes of data). Empty and Error states deferred — chart area shows the loading state for both cases in this phase.

### 8.3. Verification

A section titled "Verification" listing the three-window-size screenshots, daemon uptime duration, data source (real or generated), and extreme scenario performed.

### 8.4. Reference Comparison

A section titled "Reference comparison" stating which reference application was compared, at what resolution, and a prose summary of similarities and differences.

---

## 9. Allowed Deferrals

Not every requirement in this document must ship in every phase. Acknowledged deferrals are acceptable when they are documented in the phase summary with a clear statement of what is deferred and when it will be addressed.

### Deferrable

- **Keyboard navigation polish** may be deferred beyond Phase 5. Avalonia's default focus handling provides basic keyboard support; refinement (custom focus order, arrow-key navigation within lists) is a later concern. Document in the phase summary.
- **Animation polish** beyond LERP smoothing on value updates may be deferred. Micro-interactions (subtle entry animations, hover transitions with easing) are desirable but not gating. Document.
- **Accessibility** (screen reader compatibility, high-contrast themes, ARIA-equivalent properties) is explicitly out of scope for Phase 5 and deferred to a dedicated accessibility-focused phase.
- **Internationalization** is deferred. All strings are English until a dedicated i18n phase. Strings may be XAML literals or ViewModel properties — either is acceptable for now.

### Not Deferrable

These requirements must be met in every UI phase. They are never deferred.

- **Responsive sizing.** All elements must flex correctly from 1024x720 to maximized at 4K from day one. Fixing layout after the fact requires reworking XAML structure, not just adjusting numbers.
- **Empty states and loading states.** A blank view is a bug at any phase. The loading and empty states can be simple (a text message, a single icon) but they must exist.
- **Error states.** When the daemon disconnects, every data-bound view must show something other than stale data with no indication that it is stale.
- **Hover states on clickable elements.** If it is clickable, it has a `:pointerover` style. No exceptions.
- **Theme-token-driven colors.** No hardcoded hex values in component XAML. All colors flow through `{DynamicResource TokenName}`.

---

## 10. Precedent and Evolution

The rules in this document are derived from specific quality issues encountered during Phases 5.1 through 5.3:

- **Phase 5.1:** Tab button widths used fixed pixels, causing layout wiggle when text changed. Fixed via `MinWidth` on the button style.
- **Phase 5.1:** Nav bar tabs were centered in a star column, drifting orphaned at wide resolutions. Fixed via column restructure using `Auto,*,Auto,*,Auto`.
- **Phase 5.2:** The status strip ratio bar used pixel-based widths (`Width="140"`), failing to resize with the window. Fixed via Grid star-unit columns bound through `DoubleToGridLengthConverter`.
- **Phase 5.1:** Window controls were clipped at minimum window sizes. Fixed via MinWidth calculation on the main window.
- **Phase 5.1:** `CharacterSpacing` values were set using WPF 1/1000-em-unit assumptions, producing catastrophically wide text in Avalonia. Fixed after visual testing revealed the rendering failure.

Each issue was caused by the same underlying pattern: choosing the path of least resistance (fixed pixels, StackPanel over Grid, hardcoded values) and verifying only at the developer's current resolution with the current data set.

This document evolves. As additional phases surface new categories of quality issues, new rules should be added to the appropriate section. Existing rules are durable — do not edit them unless they are factually wrong. Add, do not replace.
