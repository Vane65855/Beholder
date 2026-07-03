# 017: Wall-Clock-True Traffic Charts

## Context

A user selected a visible traffic bump on the live chart and the selection
bar reported "0 destinations" for a window that plainly contained the bump.
Querying the database directly proved the data existed — at 19:11:40, while
the selection's computed window was 19:11:45–19:11:59. The chart had drawn
the bump several pixels away from where that traffic actually happened.

The root defect was one assumption appearing in three places: **that the
chart's pixel axis is linear in wall-clock time.** It wasn't:

1. **Live mode.** `TrafficEngine` skipped the per-second broadcast whenever
   no process moved a byte, and the UI appends one sample per *received
   batch* to its fixed 300-sample buffers. Every fully-idle second silently
   compressed the drawn timeline, so a bump's drawn position drifted
   seconds-to-tens-of-seconds from reality — and the selection feature maps
   plot fractions to wall-clock assuming 1 sample = 1 second. The same
   compression affected the connect-time seed (`GetProcessTimeline` returned
   only non-zero seconds) which also never padded up to *now*. A visible
   symptom of the same defect: the live chart froze entirely while the
   machine was idle.
2. **Historical mode.** `TimelineStitcher` returned only non-empty buckets,
   so gaps compressed the drawn axis; worse, the selection mapped fractions
   onto the *requested* range rather than the drawn data — on All Time the
   request starts at the Unix epoch, so every selection mapped to decades
   before the data and always returned 0 destinations.
3. **Tier selection.** `TierSelector.Select` ignored retention, on the
   documented theory that ranges always end at *now*. The selection feature
   issues small windows anywhere in history; a 30-minute window (pseudo-
   resolution ~6 s) older than ten minutes routed to `traffic_raw`, whose
   10-minute retention guaranteed an empty answer while `_10s` held the data.

## Decision

### The daemon broadcasts a heartbeat batch on idle ticks

`TrafficEngine` fires `OnSnapshotBatch` on every tick, sending an empty batch
when nothing moved. The empty batch is the UI's per-second clock: the
existing "processes absent from this batch get a zero sample" path advances
every buffer, keeping 1 sample = 1 wall-clock second. Wire-compatible in
both directions (old UIs already zero-fill on it; the batch always carried
`tick_timestamp_unix_ns`). Cost: one tiny message per second per subscriber,
only while a UI is attached. Bonus: the live chart scrolls and rates decay
to zero during idle instead of freezing.

### The UI gap-fills from tick timestamps (defense in depth)

`ProcessStateService` tracks the last `tick_timestamp_unix_ns` and appends
one zero sample per missed second before applying a batch — covering batches
dropped by the broadcast channel's DropOldest policy, OS sleep/resume, and
old daemons that skip idle ticks. Zero/unknown timestamps disable the fill;
backwards clock steps reset the baseline; fills cap at the 300-sample window.
The seed path pads trailing zeros from the process's last timeline bucket up
to *now* and sets the tick baseline to the seed instant.

### Stitched timelines are zero-filled uniform grids

`TimelineStitcher` expands its merged result onto the contiguous grid from
first to last non-empty bucket, emitting explicit zeros. Idle stretches
render as flat zero lines instead of being compressed away; index ↔
wall-clock is linear across the whole array. Bounded by construction: the
effective resolution targets ≤ 400 buckets per extent (beyond the 1-day
`NiceResolutionsMs` cap an All Time grid grows ~365 rows/year — acceptable).
"Same data → same chart" is preserved (the grid is deterministic from the
same merged buckets). Empty results stay empty.

*Rejected alternative:* adding `effective_resolution_ms` to the timeline
responses so the UI could zero-fill client-side. Unnecessary once the daemon
returns a contiguous grid — the UI infers the step from the endpoints
(`(last − first) / (count − 1)`), and every consumer of the RPC gets the
honest axis for free instead of only the ones that reimplement the fill.

### The selection maps fractions onto the DRAWN domain, bucket-aligned

`TrafficTabViewModel` captures the drawn domain (first/last sample
wall-clock + grid step) whenever a historical chart is applied, and maps
selection fractions through it; live mode keeps the resolved 5-minute
window, which the heartbeat + gap-fill made exact. The fetch window aligns
outward to the drawn bucket grid — floor the start to its bucket, extend the
end past its bucket close — so a band edge sitting on a drawn peak includes
that bucket and sub-bucket straddle disappears. The window label shows the
aligned range. A selection on an empty historical chart is dropped.

### Tier selection is retention-aware

`TierSelector.Select` only considers tiers whose retention still covers the
query's `from` (null retention = infinite). Among covering tiers the
coarsest-fitting-the-resolution rule is unchanged; if none fits, the finest
covering tier serves the query — coarser-than-requested data beats a
guaranteed-empty finer tier, and for the aggregate-summary queries this
selector serves, the rollup invariant makes sums identical wherever tiers
overlap. Every preset view resolves to the same tier as before; the
behavioral delta is exactly small-and-old windows.

## Consequences

### Positive

- The chart's pixel axis is truthful in both modes: what you see at a
  position is what happened at that time, idle periods render as flat zero
  lines (matching commercial network monitors), and the live chart advances
  even on a silent machine.
- The selection feature works on every timeframe — including All Time —
  and its destinations bar reports exactly the traffic drawn inside the band.
- Old-daemon/new-UI and new-daemon/old-UI pairings degrade gracefully
  (gap-fill and the pre-existing zero-fill loop respectively).

### Negative

- One empty `CounterBatch` per second per subscriber while a UI is attached
  (~30 bytes/s; nothing when no UI is connected).
- Zero-filled timelines carry more points over the pipe (bounded ≤ ~400 per
  query except multi-year All Time at ~365 rows/year) and the seed backfill
  grew to ≤ 300 points per process per connect — negligible over the local
  named pipe.
- Summary queries for small-and-old windows now serve from coarser tiers,
  whose wider buckets can include bytes from just outside the window edges
  (bounded by one bucket width; the selection UI's own grid alignment makes
  the drawn window match what is queried).
