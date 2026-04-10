# 003: Country Codes Over Flag Icons

## Context

Network monitoring tools typically display country flags next to IP addresses or hosts. Flags are visually recognizable but introduce problems: bitmap assets look fuzzy at small sizes, political disputes over certain flags create bug reports, and flags can only represent one country per cell without truncation.

## Decision

Beholder NMT displays ISO 3166-1 alpha-2 country codes (US, DE, JP) as monospaced text instead of flag images. In the firewall hosts column, the format is `count · dominant_country +N` where N is the number of additional distinct countries beyond the dominant one (e.g., `23 · US +6`). Dominant country is determined by bytes transferred, not host count.

Special values:
- `LAN` — all traffic to private/reserved ranges
- `—` (em dash) — no hosts (fully blocked process with zero connections)
- `??` — IP not found in DB-IP database

## Consequences

- Consistent with the monospaced, text-first UI aesthetic
- No bitmap assets to maintain or scale
- No political flag disputes
- The `+N` suffix communicates geographic spread at a glance — a process talking to 7 countries looks different from one talking to 1 country, which is security-relevant information
- Country flags can optionally appear in hover tooltips later without dirtying the main table
- Requires alpha-2 to alpha-3 conversion for LiveCharts2 GeoMap (handled in UI aggregation layer, not in the protocol)
