# Beholder NMT — UI Design Language

**Last updated:** 2026-04-12
**Revision:** 1.0 (initial — Phase 4.5 checkpoint)
**Change log:** Initial creation from mockups `traffic-v1.png`, `firewall-v1.png`, `alerts-v1.png`.

---

## Table of Contents

1. [Design Philosophy](#1-design-philosophy)
2. [Design Tokens](#2-design-tokens)
   - [Backgrounds](#backgrounds)
   - [Text](#text)
   - [Borders & Dividers](#borders--dividers)
   - [Accent](#accent)
   - [Severity](#severity)
   - [Status](#status)
   - [Data Visualization](#data-visualization)
   - [Per-Process Series](#per-process-series)
3. [Typography](#3-typography)
4. [Layout Grammar](#4-layout-grammar)
5. [Component Patterns](#5-component-patterns)
6. [Tab-Specific Notes](#6-tab-specific-notes)
7. [Iconography and Visual Elements](#7-iconography-and-visual-elements)
8. [Explicit Non-Goals](#8-explicit-non-goals)
9. [Usage Instructions for UI Prompts](#9-usage-instructions-for-ui-prompts)
10. [Light Theme Roadmap](#10-light-theme-roadmap)

---

## 1. Design Philosophy

Beholder NMT is a control-surface tool for network monitoring and firewall management. The UI prioritizes information density and dark-environment readability over consumer aesthetics. Its visual DNA draws from network-engineering consoles and security-analyst dashboards — utilitarian interfaces where every pixel earns its place by communicating state. The design is compact, technical, and structured around rapid comprehension: a user glancing at the screen should immediately know what's flowing, what's blocked, and what needs attention. Color is semantic, not decorative — it encodes severity, state, and data series, never branding or embellishment.

For implementation-level quality requirements — responsive sizing, required UI states, verification procedures, and banned patterns — see [UI_QUALITY_STANDARDS.md](UI_QUALITY_STANDARDS.md). That document governs how designs in this file are built. This file governs what they look like.

---

## 2. Design Tokens

Every color in the UI is referenced by its semantic token name, never by a literal hex value. Views and styles bind to these tokens via Avalonia `ThemeDictionary` entries that swap at runtime based on `RequestedThemeVariant`. This enables a future light theme without touching any view code.

Token names describe intent, not color. `AccentPrimary` is the active-state indicator color; the fact that it is currently teal is an implementation detail of the dark theme.

### Backgrounds

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `BackgroundRoot` | `#0B0E14` | TBD | Outermost window background, visible behind all panels |
| `BackgroundPanel` | `#0D1117` | TBD | Primary content panels (process list, timeline, tables) |
| `BackgroundElevated` | `#111820` | TBD | Raised surfaces: detail panes, popovers, dropdowns |
| `BackgroundHover` | `#141D26` | TBD | Row hover state in lists and tables |
| `BackgroundSelected` | `#0C2A2E` | TBD | Selected row in lists and tables (tinted toward accent) |
| `BackgroundNavBar` | `#080B10` | TBD | Top navigation bar and bottom status strip |

### Text

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `TextPrimary` | `#D4D8DE` | TBD | Default body text, process names, values |
| `TextSecondary` | `#8B949E` | TBD | Column headers, labels, less prominent text |
| `TextMuted` | `#5A6370` | TBD | Timestamps, file paths, tertiary information |
| `TextOnAccent` | `#E6EDF3` | TBD | Text rendered on accent-colored backgrounds or next to highlighted values |

### Borders & Dividers

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `BorderSubtle` | `#1C2530` | TBD | Panel edges, faint structural dividers |
| `BorderStrong` | `#2D3640` | TBD | Button outlines, input field borders, toggle pill borders |
| `DividerHorizontal` | `#1C2530` | TBD | Horizontal rules separating content sections |

### Accent

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `AccentPrimary` | `#00BCD4` | TBD | Active tab indicator, primary action buttons, logo tint, chart outbound stroke |
| `AccentHover` | `#26D9EC` | TBD | Hover state on accent-colored interactive elements |

### Severity

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `SeveritySuccess` | `#2EA043` | TBD | "Allow" pill state, "verified" badge, chain-valid indicators |
| `SeverityWarn` | `#D4A017` | TBD | Warning-level alerts, partial states, caution indicators |
| `SeverityDanger` | `#DA3633` | TBD | "Block" pill state, critical alerts, "BLOCKED" activity entries |
| `SeverityInfo` | `#5A6370` | TBD | Informational alerts, neutral state indicators |

### Status

Distinct from severity. These tokens represent daemon connection state, which may diverge visually from severity colors in future iterations.

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `StatusOnline` | `#2EA043` | TBD | "daemon: online" indicator in top nav bar |
| `StatusOffline` | `#DA3633` | TBD | "daemon: offline" indicator in top nav bar |
| `StatusConnecting` | `#D4A017` | TBD | "connecting…" / "reconnecting…" in top nav bar |

### Data Visualization

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `ChartOutboundFill` | `#00BCD4` at 30% opacity | TBD | Area fill under the outbound traffic curve |
| `ChartOutboundStroke` | `#00BCD4` | TBD | Outbound traffic curve line |
| `ChartInboundFill` | `#A855F7` at 30% opacity | TBD | Area fill under the upload traffic curve |
| `ChartInboundStroke` | `#A855F7` | TBD | Upload traffic curve line (token name is legacy; see Traffic Direction Color Semantics) |
| `ChartGridline` | `#1C2530` | TBD | Background grid lines on chart axes |
| `ChartAxisLabel` | `#5A6370` | TBD | Axis tick labels (time, byte values) |
| `ChartSparklineFill` | `#00BCD4` at 20% opacity | TBD | WAN throughput bar fill in bottom status strip. Currently matches `AccentPrimary` but tokenized independently for future flexibility. |
| `ChartSparklineStroke` | `#00BCD4` | TBD | WAN throughput bar stroke. Same independence note as fill. |
| `HeatmapCold` | `#1C2530` | TBD | Phase 8 world map: country with 0 bytes; outline-only fill against the ocean. |
| `HeatmapLow` | `#00BCD4` at 25% opacity | TBD | Phase 8 world map: 1–20% of MaxBytes. |
| `HeatmapMedium` | `#00BCD4` at 60% opacity | TBD | Phase 8 world map: 20–50% of MaxBytes. |
| `HeatmapHigh` | `#D4A017` at 80% opacity | TBD | Phase 8 world map: 50–80% of MaxBytes. |
| `HeatmapPeak` | `#DA3633` | TBD | Phase 8 world map: 80–100% of MaxBytes — the loudest country. |

*Heatmap stops are five **deliberate** colors, not opacity overlays of a single base, so a future light-theme reviewer must contrast-balance five new values rather than copy one opacity ramp. The dark-theme alphas are baked into the token's color value (`#4000BCD4` etc.) so the resolved on-screen color always comes from a single token lookup per §10's "every effective color is a token" rule.*

### Traffic Direction Color Semantics

Beholder uses two primary colors for directional traffic indicators:

- **Download (inbound)** uses teal (`ChartOutboundStroke`, `#00BCD4`).
  Downloads are typically expected (web browsing, software updates, video
  streaming) and teal is calm, cool, non-alarming.

- **Upload (outbound)** uses purple/violet (`ChartInboundStroke`, `#A855F7`).
  Unexpected outbound traffic is a security-relevant signal (data
  exfiltration, unwanted cloud sync, telemetry). Purple is visually
  distinct from both the neutral text colors AND the severity-danger
  red, giving upload its own clear semantic space.

This is intentionally contrarian to consumer-grade network UIs that
optimize for performance framing (green for download, single-tone
ratio bars). Beholder's framing is security-first, not
performance-first.

**Historical note:** An earlier design used coral/orange (`#E8734A`) for
upload. This was changed to purple (`#A855F7`) because coral read as
"warning/alarm" to some users, overlapping semantically with
`SeverityWarn` and `SeverityDanger`. Purple carves out a distinct
visual space for "noteworthy but not an emergency."

**Token naming note:** The tokens `ChartOutboundStroke` and
`ChartInboundStroke` have names that no longer match their semantic
use (teal is used for inbound/download, purple is used for
outbound/upload). Names are preserved for stability; a future rename
pass may align names with semantics.

`SeverityDanger` (red) remains reserved for hard error states
(daemon offline, connection failures, rule conflicts) and is NOT
used for traffic direction indicators.

**Do not swap these back to convention without a deliberate UX
conversation.**

### Per-Process Series

The process list assigns colors from a fixed palette. Colors are assigned deterministically by index — the mapping between process and color is not hardcoded. The "All processes" summary row always uses `Series01`.

| Token | Dark Theme | Light Theme | Usage |
|-------|-----------|-------------|-------|
| `Series01` | `#00BCD4` | TBD | First series (aggregate/all processes) |
| `Series02` | `#E8734A` | TBD | Second series |
| `Series03` | `#D4A017` | TBD | Third series |
| `Series04` | `#8B5CF6` | TBD | Fourth series |
| `Series05` | `#DA3633` | TBD | Fifth series |
| `Series06` | `#2EA043` | TBD | Sixth series |
| `Series07` | `#5A6370` | TBD | Seventh series |
| `Series08` | `#E06C9F` | TBD | Eighth series |
| `Series09` | `#3B82F6` | TBD | Ninth series |
| `Series10` | `#A78BFA` | TBD | Tenth series |
| `Series11` | `#F59E0B` | TBD | Eleventh series |
| `Series12` | `#10B981` | TBD | Twelfth series |

---

## 3. Typography

### Font Family

**Inter** is the designated typeface. It is open-source (SIL Open Font License 1.1), offers excellent support for tabular figures (`tnum` OpenType feature), and renders consistently across Windows and Linux.

**Bundling:** Inter font files will be embedded as resources in `Beholder.Ui/Assets/Fonts/` and registered via Avalonia's `FontManagerOptions`. Downloading and committing the font files is a **Phase 5.1 setup task**, not part of this document's scope.

**Fallback stack:** `Inter, 'Segoe UI', Cantarell, sans-serif`. The visual target is Inter's metrics; platform defaults serve as fallback when Inter is unavailable.

### Type Scale

Approximate size ratios — exact pixel sizes will be tuned during implementation:

| Role | Weight | Size Ratio | Treatment |
|------|--------|------------|-----------|
| Section header | SemiBold (600) | 0.75x body | ALL CAPS, 1.5–2px letter-spacing, `TextSecondary` color |
| Body text | Regular (400) | 1x (base) | Default for process names, descriptions, values |
| Caption / timestamp | Regular (400) | 0.85x body | `TextMuted` color, used for timestamps, paths, secondary labels |
| Numeric values | Regular (400) | 1x body | Tabular figures enabled (`font-feature-settings: "tnum"`) for vertical column alignment |
| Tab labels | Medium (500) | 0.9x body | ALL CAPS, ~2px letter-spacing |

### Tab Label Treatment

Tab labels use a distinctive bracket-and-underline treatment that is a signature element of the UI:

- Labels are ALL CAPS with noticeable letter-spacing
- The active tab is wrapped in brackets: `[ TRAFFIC ]`
- The active tab has an underline in `AccentPrimary`
- Both bracket decoration AND underline appear together on the active tab
- Inactive tabs show the label only, in `TextSecondary`, with no brackets or underline

### Tabular Figures

All numeric display columns (byte counts, throughput rates, timestamps, sequence numbers) must use tabular figures so digits align vertically without requiring a monospace font. Inter supports this via the `tnum` OpenType feature. In Avalonia, apply via `Typography.NumeralAlignment="Tabular"` or equivalent style.

---

## 4. Layout Grammar

### Top Navigation Bar

Persistent across all tabs. Left-to-right:

1. **Brand block** — Beholder logo icon + "BEHOLDER NMT" in `TextPrimary`, followed by version string and daemon status indicator (e.g., "v0.1.0 · daemon: online"). The status text uses `StatusOnline` or `StatusOffline` color.
2. **Tab bar** — center-aligned. Tab labels in ALL CAPS with the active tab decorated with `[ BRACKETS ]` and underline in `AccentPrimary`. Tabs: TRAFFIC, FIREWALL, ALERTS, MAP, SCANNER.

The time-range selector is **not** in the global nav. It lives inside the Traffic tab's own top bar, left of the GRAPH/COLS toggle, so it stays visible when switching between GRAPH/COLS/MAP sub-views within Traffic but doesn't take up horizontal space on tabs that don't need it. The control is a dropdown button (`TimeRangeDropdown`) labeled with the active selection (e.g., `1 Hour ▾`) and a flyout containing seven options grouped as `5 Minutes / 1 Hour / 24 Hours` · `Last 7 Days / Last 30 Days / All Time` · `Custom...`. The `5 Minutes` option streams live from the circular buffers; all others trigger a historical query and render a point-in-time snapshot.

### Bottom Status Strip

Persistent across all tabs. Left-to-right:

1. **Download metrics** — `▼ DOWNLOAD` with Σ cumulative total and current rate.
   Arrow in teal (`ChartOutboundStroke`), values in `TextPrimary`.
2. **Upload metrics** — `▲ UPLOAD` with Σ cumulative total and current rate.
   Arrow in purple (`ChartInboundStroke`), values in `TextPrimary`.
3. **Ratio bar** — flexible-width bar centered in the middle column. Teal
   fill (left) = download share, purple fill (right) = upload share.
4. **WAN total** — "WAN Σ" label with cumulative in+out total.
5. **Device identifier** — "DEV" label with device ID.

Background uses `BackgroundPanel`.

### Main Content Area

Varies by tab but follows one of three established patterns:

- **List + Chart** (Traffic) — narrow scrollable list on the left, wide chart/data area on the right
- **Table + Feed** (Firewall) — full-width data table on top, activity feed below
- **Master + Detail** (Alerts) — scrollable timeline on the left, detail pane on the right

---

## 5. Component Patterns

### 5.1 Row Severity Rail

A thin vertical color bar on the left edge of a list row indicating severity at a glance. Visible in the Alerts timeline where each alert row has a colored left border:

- `SeverityDanger` for critical/blocked events
- `SeverityWarn` for warnings
- `SeveritySuccess` for informational/positive
- `SeverityInfo` for neutral

Width: ~3–4px. Reusable wherever row-level severity is a first-class attribute (Alerts timeline, Firewall activity feed).

### 5.2 Action Button Row

A horizontal row of evenly spaced outlined buttons for contextual actions. Visible in the Alerts detail pane: BLOCK HOST, BLOCK PROCESS OUT, ADD RULE, DISMISS.

- Outlined style: `BorderStrong` border, transparent background, `TextPrimary` label
- ALL CAPS labels, same letter-spacing as section headers
- On hover: `BackgroundHover` fill
- Semantic variants: danger actions may use `SeverityDanger` border tint

### 5.3 Inline Toggle Pills

Compact bordered pill-shaped buttons that serve as both status indicators AND interactive tap targets. Visible in the Firewall table's IN/OUT columns:

- "ALLOW" state: `SeveritySuccess` border, `SeveritySuccess` text
- "BLOCK" state: `SeverityDanger` border, `SeverityDanger` text
- Background: transparent
- On tap: triggers the `ApplyFirewallRule` RPC

Distinct from status badges (section 5.8): pills are bordered, interactive, and represent toggleable state.

### 5.4 Filter Chip Row

A segmented control for filtering list views. Visible in the Alerts tab: ALL, CRITICAL, WARN, INFO.

- Active chip: `AccentPrimary` border and text, `BackgroundSelected` fill
- Inactive chips: `BorderSubtle` border, `TextSecondary` text, transparent fill
- On hover: `BackgroundHover` fill

### 5.5 Detail Pane

A right-side panel showing expanded information for a selected item. Visible in the Alerts tab with subsections:

- **Header:** item type + timestamp
- **Description:** one-line summary
- **Subsections** organized under ALL CAPS headers (PROCESS, REMOTE, CONNECTION, ACTIONS)
- Each subsection contains label-value pairs in `TextMuted` labels and `TextPrimary` values
- Background uses `BackgroundElevated` to visually separate from the master list

This pattern may recur in other tabs (e.g., clicking a process in Traffic could open a detail pane).

### 5.6 Event Pins on Charts

Small labeled markers anchored to specific time points on the time-series chart. Visible in the Traffic tab:

- Pin types: "NEW" (new process detected), "ALERT" (alert fired)
- Rendered as small upward-pointing markers with a text label
- Use `AccentPrimary` for the pin and label
- Positioned above the chart area at the corresponding x-axis time

Reusable chart-overlay pattern for surfacing discrete events in continuous time-series data.

### 5.7 Timeline with Date Headers

Grouping list items under date separators. Visible in the Alerts tab:

- Separator format: `-- TODAY`, `-- YESTERDAY`, or `-- 2026-04-10`
- Separator text in `TextMuted`, horizontally centered or left-aligned with rules extending to edges
- Items within each group are ordered reverse-chronologically (newest first)

### 5.8 Status Badges

Inline colored text labels indicating verification or state. Visible in the Alerts detail pane: "verified" next to a SHA-256 hash.

- No border, no background — color alone conveys meaning
- Semantic color from `SeveritySuccess` / `SeverityWarn` / `SeverityDanger`
- Same font size as surrounding body text
- Inline with adjacent text, not block-level

Distinct from toggle pills (5.3: bordered, interactive) and severity rails (5.1: thin edge bars on rows).

### 5.9 OS Notification Toasts

OS-rendered notifications fired when a new alert arrives via the live broadcast stream. Visual appearance is OS-controlled (Windows: bottom-right toast + Action Center; Linux: D-Bus notification, DE-themed); this subsection governs CONTENT, not appearance.

- **Title:** the alert kind label in ALL CAPS — "NEW PROCESS", "HASH CHANGED", "CHAIN ERROR". Matches the in-app kind-badge label so the user mentally maps the toast to the row in the Alerts tab.
- **Body:** `{display name} — {summary}` on a single line. Display name is the executable file name; summary is the same string the in-app detail pane shows.
- **Severity mapping** (informational on Windows; sets D-Bus urgency on future Linux impl):
  - `AlertKind.NewProcess` → `SeverityInfo`
  - `AlertKind.HashChanged` → `SeverityWarn`
  - `AlertKind.ChainError` → `SeverityDanger`
- **Click activation:** restores the main window from minimized state, brings it to the foreground, switches to the Alerts tab, and selects the specific alert by its chain seq. The seq is encoded as a toast argument (`seq=N`) and parsed in the activation callback.
- **Suppression:** none. Toasts fire on every live alert. Users mute via OS Focus Assist / Do Not Disturb. Notification rate-limiting (e.g., "+N more" coalescing) is a deferred polish item.
- **Failure handling:** notification failure is non-critical — the alert is durably persisted in `event_log` and visible in the in-app list regardless. The Windows implementation catches and logs at Warning level; never throws.

Distinct from in-app banners (none defined yet — `WindowNotificationManager` is unused) and modal dialogs (banned for routine notifications per UI_QUALITY_STANDARDS §5 #7).

### 5.10 Error Banner

Inline error/warning banner shown above the affected view's content area. Two severity variants:

- **Danger** — full-width, red bottom-border, no corner radius. Used for action failures (RPC errors, validation failures, "Failed to apply firewall rule"). Sits at the top of the content panel like a system alert. Background `BackgroundElevated`, border + text `SeverityDanger`.
- **Warn** — centered overlay, yellow border with corner radius, smaller padding. Used for transient soft-warning states (daemon disconnected showing stale data, historical-load failure). Background `BackgroundElevated`, border + text `SeverityWarn`.

**Dismiss affordance:** every banner has a close-X button (right-aligned, `Path` glyph mirroring `WindowControls.axaml`). Click invokes the bound `DismissCommand`, which sets `HasError = false` and clears `ErrorMessage`. Cursor `Hand`; hover tints `BackgroundHover`; tooltip "Dismiss".

**Auto-clear on action entry:** when the user takes another action (e.g., clicks BLOCK on a different alert), the action method clears `HasError` at entry before attempting the new operation. A successful retry implicitly dismisses the stale error; a fresh failure shows its own message. The X exists for cases where the user doesn't want to retry.

**Reusable control:** `Beholder.Ui/Controls/ErrorBanner.axaml`. Bound properties: `Severity` (`Danger`/`Warn` enum), `Message` (string), `DismissCommand` (ICommand?). When `DismissCommand` is null the X button is hidden — the banner reverts to read-only display, used for cases where the banner clears via another mechanism (e.g., daemon-reconnect auto-clears the "Daemon disconnected" banner in `TrafficTabViewModel.OnDaemonStateChanged`).

Distinct from in-app toasts (none defined yet) and OS notification toasts (§5.9).

### 5.11 World Map (Phase 8)

Custom `Canvas`-drawn world heatmap rendered by `Beholder.Ui/Controls/WorldMapControl.cs`. Mirrors `TrafficChartControl`'s shape: data flows in via `StyledProperty<IReadOnlyList<CountryTrafficSummary>?> Countries` + `StyledProperty<long> MaxBytes`; theme brushes resolved lazily and cached; `Render(DrawingContext)` override; resource invalidation on theme swap. No LiveCharts2 / Mapsui / XAML.MapControl dependency (all three either don't support Avalonia 12 or require external map-tile servers).

**Geometry source:** `Beholder.Ui/Assets/world-countries-110m.geojson` — Natural Earth 110m Admin 0 Countries (public domain / CC0), trimmed to `{iso_a2, name, geometry}` per feature with 2-decimal coordinate precision (~170 KB asset, 177 countries). Parsed once at first render via `Beholder.Ui/Services/WorldGeometryLoader.cs` (`Lazy<>`-wrapped, thread-safe one-shot init, returns empty list on malformed JSON for graceful degradation).

**Projection:** equirectangular (plate carrée). `WorldMapProjection.Project(GeoPoint, Rect)` is `(lon + 180) / 360 × width`, `(90 − lat) / 180 × height`. No trigonometry. Polar distortion is mild and irrelevant (polar traffic ≈ 0). Mercator / Equal Earth are projection-agnostic at the data layer; future polish can swap by replacing the single helper.

**Heatmap palette:** 5 stops, picked by `HeatmapPalette.BrushFor(bytes, maxBytes)` with named-constant thresholds (`StopLowFraction = 0.20`, `StopMediumFraction = 0.50`, `StopHighFraction = 0.80`). Cold for 0 bytes, then a teal-amber-red ramp. Tokens are the five new heatmap entries in §2 Data Visualization.

**Hover interactivity:** `PointerMoved` → `WorldMapProjection.Unproject` to lat/lon → `WorldMapHitTester.FindCountryAt` (bounding-box prefilter + point-in-polygon ray-cast) → re-render with a tooltip overlay anchored top-left. Backed by `BackgroundElevated` fill + `BorderStrong` border, rendered via the dedicated `WorldMapTooltipRenderer` (extracted from `WorldMapControl` to keep the control under the CLAUDE.md ~200 LOC class threshold). No click handling.

**Tooltip layout — header is always shown; the rest depends on the per-country top-N fetch state:**

| Section | When shown | Content |
|---|---|---|
| Header (large, `TextPrimary`) | Always when hovering a country | Country name |
| Bytes line (small, `TextPrimary`) | Always | `▼ {bytesIn}   ▲ {bytesOut}` or `no traffic` |
| `BorderSubtle` divider | When any of the rows-section states below applies | 1-pixel horizontal line |
| Destination rows (small, `TextPrimary`) | **Populated state** (fetch returned ≥ 1 row) | Up to 3 rows of `{label}    {bytes}`. Label = hostname when resolved (Phase 5.4.4 ladder), raw IP otherwise. Truncated with ellipsis at 28 chars to bound tooltip width. Right-aligned bytes column. |
| `loading…` (small, `TextMuted`) | **Loading state** (fetch in flight) | Visually distinct from Empty per UI_QUALITY_STANDARDS §3.1 |
| `no resolved destinations` (small, `TextMuted`) | **Empty state** (fetch returned 0 rows) | |
| `destinations unavailable` (small, `TextMuted`) | **Failed state** (RPC failure) | **Silent-degrade** per design: destinations are opportunistic, no ErrorBanner — the header + bytes are still useful, and the user can switch to COLS for the authoritative list. |
| *(rows section omitted)* | **No-fetch-yet state** (no hover yet on this country) | Tooltip stays at its pre-rows height |

**Per-country top-N fetch (Phase 8 polish):** hovering a country triggers a lazy fetch via the existing `GetProcessDestinations` RPC scoped to `country = iso2, limit = 3`, with `process_path` from the Traffic tab's left-panel selection and the range from the time-range dropdown. Results are cached in the VM per `(country, range, process)` key — re-hovering the same country instantly populates from cache (no RPC). The cache is invalidated whenever the time range or process selection changes. Rapid hover between countries cancels in-flight fetches via a single-flight CTS so only the final hover's data lands in the tooltip.

**Caption strip below the map:** the LAN ("--") and Unknown ("??") sentinel countries have no geographic location to plot at, so they surface in a caption strip rendered below the map: `LAN: ▼ X / ▲ Y   ·   Unknown: ▼ X / ▲ Y`. Always shown (even at zero) so the caption strip's height stays stable across data updates.

**Font sizes are named constants in `WorldMapTooltipRenderer`** (`HeaderFontSize = 13`, `RowFontSize = 11`) rather than inline literals — per UI_QUALITY_STANDARDS §5 #2 banned-pattern guidance. Canvas-drawn `FormattedText` can't bind to `DynamicResource` theme tokens at draw time, so constants are the right granularity (same pattern as `TrafficChartControl`'s stroke widths).

**States implemented:** Loading (parent VM's spinner overlay for the whole map; the in-tooltip `loading…` for the per-country top-N fetch), Empty (caption "No foreign traffic" + LAN/Unknown caption still shown; in-tooltip "no resolved destinations" for the per-country case), Populated (heat-mapped countries + hover + up-to-3 destination rows), Error (ErrorBanner Danger variant for the map; silent-degrade in-tooltip "destinations unavailable" for the per-country case), Asset-unavailable (centered "World map unavailable" caption — protects against corrupt builds without crashing the Traffic tab).

Distinct from `TrafficChartControl` (§4 chart area) which renders a time-series area chart. The two share a common pattern — custom `Control` subclass, lazy token resolution, reusable geometry buffers, theme-swap invalidation — but render disjoint visual languages.

---

## 6. Tab-Specific Notes

### Traffic

- **Left panel:** Scrollable process list. Each row: colored series square (legend marker), process name, cumulative OUT value (right-aligned, tabular figures). First row is "All processes" aggregate using `Series01`. Selected row uses `BackgroundSelected`.
- **Right panel:** Time-series area chart with chart/cols/map view toggle buttons in the top-right corner. Chart shows stacked area plots (outbound dominant, inbound as secondary overlay). Event pins (NEW, ALERT) appear at meaningful moments. X-axis: timestamps. Y-axis: byte rates with SI suffixes (K, M, G).
- **View toggles:** GRAPH (area chart), COLS (columnar table of per-host connections), MAP (world heatmap — see Map sub-view below). Active toggle uses `AccentPrimary`.

#### Traffic → Map sub-view (Phase 8)

When the MAP toggle is active, the right pane renders the world-heatmap surface defined in §5.11 (`WorldMapControl`). Behavior shared with GRAPH and COLS:

- **Per-process filter from the left panel.** Selecting "All processes" calls `GetCountryBreakdown(processPath=null)` for the aggregate; selecting a specific process scopes the heatmap to that process's per-country traffic.
- **Time-range from the dropdown.** The heatmap respects the same range presets the chart does. Switching range while MAP is active re-fetches the breakdown.
- **Cancellation.** A second concurrent fetch (rapid view-mode flip, range change, or process change) cancels the prior one via a single-flight CTS — no stacked RPCs.
- **LAN + Unknown caption.** Sentinel country codes "--" (private/reserved IPs) and "??" (resolved to no country) have no geographic location to plot at, so they surface in the caption strip below the map per §5.11 — never on the map itself.
- **Top-3 destinations on hover** (Phase 8 polish). Hovering a country fetches its top-3 destinations (by total bytes) via the existing `GetProcessDestinations` RPC, extended with `country = iso2, limit = 3` filters. Hostname (or fallback to raw IP) + total bytes shown in the tooltip below the country header. Lazy fetch, cached per `(country, range, process)`. Failed fetch silently degrades to "destinations unavailable" rather than raising an ErrorBanner that would obscure the map — see §5.11.

### Firewall

- **Toolbar:** Search/filter input, filter controls, "+ NEW RULE" action button.
- **Rules table:** Columns include process name, path, IN/OUT toggle pills, hosts count with country-code summary, recent throughput, source. Blocked rows may have a tinted `SeverityDanger` background wash.
- **Activity feed:** Below the table. "RECENT FIREWALL ACTIVITY" section header. Each entry: timestamp, kind label (BLOCKED/RULE/NEW in semantic colors), description. Uses the severity rail pattern on the left edge.

### Alerts

- **Summary bar:** Below tabs. Shows alert counts ("47 TODAY · 3 CRITICAL · 12 NEW") followed by the filter chip row (ALL/CRITICAL/WARN/INFO) and utility buttons (filter, EXPORT).
- **Timeline (left):** Grouped by date headers (-- TODAY, -- YESTERDAY). Each row: timestamp, kind badge (CHAIN, BLOCK, HOST, RULE, NEW, HASH, WIFI), description, and optional count. Severity rail on left edge. Selected row highlighted in `BackgroundSelected` with a play-triangle indicator.
- **Detail pane (right):** Shows selected alert's full context. Subsections: header with type and timestamp, description, PROCESS (name, path, hash status badge), REMOTE (hostname, IP, country, ASN), CONNECTION (first seen, bytes, status), ACTIONS (button row).
- **Detail pane footer (orphaned binary):** When the alert's binary no longer exists on disk, both action buttons (ADD RULE and BLOCK / UNBLOCK PROCESS OUT) disable (default Avalonia opacity reduction) and an `EXECUTABLE NOT FOUND` caption in `SeverityWarn` appears next to them. Mirrors the Firewall tab's orphaned-row treatment so users get the same affordance for "this binary is gone" across both tabs. Existing button tooltips remain readable on the disabled buttons via `ToolTip.ShowOnDisabled="True"`.

### Scanner

No mockups exist for this tab. When its phase arrives, it should inherit the design language from this document and add a tab-specific subsection here.

*(Map is no longer silent — see §5.11 + the Traffic→Map sub-view subsection above.)*

---

## 7. Iconography and Visual Elements

### Logo

The Beholder logo appears in the top-left of the navigation bar: a stylized eye/target icon rendered as a compact, monochrome stroked glyph in `AccentPrimary`. It should remain monochrome and adapt to the current accent color — no multi-color treatment.

### Functional Icons

Icons are used sparingly and serve functional, not decorative, purposes:

- **Search magnifier** — in filter/search input fields
- **Arrow indicators** — `▲` (outbound/up) and `▼` (inbound/down) in the status strip
- **Play triangle** — marks the selected row in the Alerts timeline
- **Chart/grid/globe glyphs** — view toggle icons (GRAPH, COLS, MAP)

**Principle:** Icons are monochrome, stroked (not filled/colored glyphs), and use `TextSecondary` or `TextMuted` as their default color. Interactive icons follow the same hover behavior as text.

---

## 8. Explicit Non-Goals

This document is **not**:

- **A pixel-perfect specification.** Exact positions, column widths, margins, and padding are not authoritative. Avalonia layout handles responsive sizing; don't extract fixed dimensions from mockups.
- **Authoritative on exact dimensions or spacing.** The mockups communicate intent and proportions, not measurements.
- **Authoritative on sample data.** Process names (firefox.exe, claude-code.exe), IP addresses, alert descriptions, and throughput values in the mockups are illustrative. Don't hardcode them.
- **Final.** Later phases may revise decisions as real usage reveals friction and the light theme lands.
- **A substitute for accessibility review.** Contrast ratios in the dark theme appear adequate but need formal auditing. Light theme values will need proper contrast testing when defined.

### Design Gaps (mockups are silent)

The following UI states and elements are not covered by the current mockups. They should be designed when their implementing phase arrives and documented here:

- Focus rings and keyboard navigation indicators
- Dialog/modal appearance and overlay treatment
- Context menu styling
- Empty states (no data, first launch)
- Loading states (connecting to daemon, waiting for first data)
- Scrollbar styling
- Undo-banner appearance (toast notifications now defined in §5.9; error banners in §5.10)

---

## 9. Usage Instructions for UI Prompts

When implementing UI work in Phase 5 or later:

1. **Read this file first.** It is the curated distillation of the mockups.
2. **Use tokens from section 2 for every color.** Never use hardcoded hex values in view code or styles. Bind to semantic token names that resolve via `ThemeDictionary`.
3. **When mockups and this document disagree, this document wins.** Mockups show early-stage intent with minor inconsistencies; this document synthesizes the authoritative design language.
4. **Do not match mockups pixel-for-pixel.** Use them for structural reference and visual feel, not as measurement targets.
5. **When introducing a new component pattern** not covered in section 5, add a subsection to this document as part of the same commit. Keep the pattern catalog current.
6. **When introducing a new design token** (e.g., for a focus ring color), add it to the appropriate table in section 2 with both dark and light-theme columns.

---

## 10. Light Theme Roadmap

A light theme is planned for a later phase (likely Phase 12 polish, or earlier if user demand appears).

**Rules for all Phase 5+ UI work:**

- All view code and styles MUST use the semantic tokens from section 2. Never hardcode hex values. This is a hard rule, not a suggestion.
- When the light theme is designed, its values will be filled into the "Light Theme" column of the same token tables in section 2. No view code changes will be needed.
- The current dark palette values are NOT automatically invertible. A proper light theme requires deliberate color selection, not mechanical inversion. Contrast ratios, readability, and visual hierarchy must be reviewed independently.
- Light theme TBD values are explicitly left blank rather than filled with guesses. Fabricated placeholder values would need revisiting and create false confidence.
- **Phase 8 heatmap tokens** (`HeatmapCold` / `HeatmapLow` / `HeatmapMedium` / `HeatmapHigh` / `HeatmapPeak`) are five deliberately-chosen colors, not opacity overlays of a single base. A mechanical inversion will NOT yield a usable light-theme palette — the reviewer must pick five light-theme counterparts that maintain perceptual ordering (Cold→Low→Medium→High→Peak should still read as a low→high intensity ramp) and adequate contrast against the light-theme `BackgroundPanel` ocean fill.
