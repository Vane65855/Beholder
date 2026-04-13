# UI Shell — Manual Test Checklist

**Phase**: 5.1
**Command**: `dotnet run --project Beholder.Ui`

---

## Window basics

- [ ] Window opens without crash
- [ ] Title bar reads "Beholder NMT"
- [ ] Window size is approximately 1280x800
- [ ] Entire window background is very dark navy (#0B0E14)
- [ ] Window can be resized (minimum ~960x600)
- [ ] Text renders in Inter font (check tabular figures in status strip)

## Top nav bar

- [ ] Background is darker than main content area (#080B10)
- [ ] Bottom border visible as subtle line
- [ ] "BEHOLDER NMT" in teal/cyan, ALL CAPS, letter-spaced
- [ ] "v0.1.0" in muted gray to the right of brand
- [ ] "daemon: offline" visible, "offline" in red
- [ ] 5 tab buttons visible: TRAFFIC, FIREWALL, ALERTS, MAP, SCANNER
- [ ] TRAFFIC is active by default (teal color, underline)
- [ ] Inactive tabs are gray
- [ ] "LAST 24 HOURS" visible on right side in muted text

## Tab switching

- [ ] Clicking FIREWALL switches active tab indicator and center content
- [ ] Clicking ALERTS switches active tab indicator and center content
- [ ] Clicking MAP switches active tab indicator and center content
- [ ] Clicking SCANNER switches active tab indicator and center content
- [ ] Clicking TRAFFIC returns to traffic tab
- [ ] Each tab shows its placeholder text (e.g., "Firewall tab content")
- [ ] Only one tab is highlighted at a time

## Status strip

- [ ] Visible at bottom of window
- [ ] Background is slightly lighter than main area (#0D1117)
- [ ] Top border visible as subtle line
- [ ] "▼ DOWNLOAD Σ 0 B · 0 B/s" visible on the left (teal arrow)
- [ ] "▲ UPLOAD Σ 0 B · 0 B/s" visible next to DOWNLOAD (purple arrow)
- [ ] "DEV-0000" visible on the right in muted text

## Theme tokens

- [ ] All text is light-on-dark (no black text on dark background)
- [ ] Teal/cyan accent color used consistently for active states
- [ ] No default Avalonia blue/purple colors bleeding through
- [ ] No white or light backgrounds visible
