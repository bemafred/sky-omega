# Limit: Rebuild phases lack in-progress observability at scale

**Status:**        Latent
**Surfaced:**      2026-04-25, during Phase 6's trigram rebuild phase. 4+ hours into a silent rebuild phase with no in-progress emission, ETAs and progress had to be estimated from indirect signals (`du -sh` of `rebuild-tmp/`, chunk file count, OS-level iostat). Workable for a single live operator but inadequate for unattended runs, CI automation, or post-completion analysis of phase-internal behavior.
**Last reviewed:** 2026-04-25
**Promotes to:**   ADR when (a) a rebuild phase silent-time exceeds the operator's monitoring patience on production runs, OR (b) automation/CI needs to detect stuck rebuilds without human inspection, OR (c) a future ADR-034 SortedAtomStore introduces an additional silent phase (vocabulary build) that compounds the gap

## Description

Mercury's existing rebuild metrics infrastructure (`IRebuildMetricsListener` from ADR-030 Phase 1) fires **once per index phase, at phase end**. The CLI emits one line: `"  GPOS: 17,029,283,265 entries"` after GPOS completes; another after trigram completes. Between those lines, there is **no inline progress emission** during the multi-hour interior of each phase.

For Phase 6 at 21.3 B Reference scale, the trigram rebuild's interior is 4+ hours (likely 8-12+ hours total). During that span, the operator cannot answer questions like:

- How far into the GSPO scan are we?
- Has the sorter started spilling chunks, or are we still filling the in-memory buffer?
- Are we currently in chunk emission or chunk drain?
- Approximately how many entries remain?
- What's the rate of progress within this phase?

These can be answered indirectly via filesystem and process inspection (chunk file count in `rebuild-tmp/trigram/`, modification time of the most-recent chunk, `iostat` patterns, dotnet process RSS), but those are operator workarounds for a logging gap. They don't survive into the metrics JSONL stream that automation and post-hoc analysis depend on.

## What's adequate vs what's missing

**Adequate today:**
- Per-phase end summary in CLI output (`name: N entries`)
- Per-phase summary record in JSONL via `RebuildPhaseMetrics` when `--metrics-out` is set
- Bulk-load already has good in-progress emission (every 100K triples, throttled to terminal display every 10s)

**Missing — the gap:**
- No equivalent in-progress emission for rebuild's GSPO scan
- No emission for sorter chunk spills (each spill is a distinct measurable event)
- No emission during k-way merge / `AppendSorted` / `AppendBatch` drain
- No estimated-completion line based on current rate
- No rate-per-second tracking within a phase

## Concrete additions worth specifying

When promoted to ADR, the changes follow the bulk-load progress pattern:

1. **Periodic rebuild progress records** during each phase's interior:
   - GSPO-scan progress every N entries (e.g., every 1M, throttled to display every 10s)
   - Sorter chunk-spill events (each spill emits a record: phase, sub-phase=`emit`, chunk-index, entries-in-chunk)
   - Drain progress every M drained entries (or every chunk consumed)
   - Field shape mirroring `LoadProgress`: phase, sub-phase, entries-processed, total-estimated (where computable), rate-per-second, GC-heap, working-set, elapsed
2. **Sub-phase identification.** Within "trigram", distinguish `emission` (GSPO scan + sorter add) from `drain` (k-way merge + AppendBatch). Same for GPOS rebuild's `emission` / `drain`. Two boolean signals operators can correlate against disk activity.
3. **Estimated total entries.** GSPO scan can be bounded: total = `_gspoReference.QuadCount` (already known at phase start). Emission progress = `entries_scanned / quad_count`. For drain: total chunks known from the sorter's `ChunkCount`. Both straightforward.
4. **Rate-per-second.** Already computed for bulk; reuse the same tracking pattern.
5. **JSONL record per progress event.** `--metrics-out` consumers can stream and visualize. Rebuild becomes observable to Grafana/`jq`/anything else, not just the live operator.
6. **Throttling.** Match bulk-load's discipline: per-record JSONL write is per-1M-entries; terminal display is throttled to every 10s. Keeps log volume sane during multi-hour phases.

## Why this isn't done already

The bulk-load progress pattern was added under ADR-030 Phase 1 (1.7.31), but the rebuild path never received the equivalent treatment because:

- ADR-030 Phase 2-3 (parallel + sort-insert) shifted attention before the rebuild progress was generalized
- Those phases were reverted and replaced by ADR-032 (radix external sort), which restructured the rebuild internals — adding progress would have meant rewriting it after the new architecture stabilized
- Phase 6 is the first time a rebuild has run long enough to make the missing observability load-bearing in real time

## Trigger condition

Promote to ADR when any of:

- An operator-attended Phase 6-class run has a silent phase exceeding ~1 hour and the lack of progress signals materially complicates monitoring (already true at 21.3 B; will be more true at any scale that pushes farther past RAM)
- Automation/CI/scheduled runs need to detect "rebuild stuck" vs "rebuild progressing slowly" without human inspection
- A future architectural change introduces additional silent phases (e.g., SortedAtomStore's vocabulary build adds another scan + sort + finalize cycle that would need the same instrumentation)
- A failure post-mortem requires understanding *where* in a phase a problem occurred, not just that the phase failed

## Current state

Latent. Phase 6 21.3 B Wikidata is running through the trigram silent phase as of 2026-04-25; the operator (Martin) is using indirect filesystem signals (`rebuild-tmp/trigram/` chunk count, `du -sh`, `iostat` patterns) to estimate progress. Workable for a live single-machine session; would be inadequate for unattended runs.

The post-Phase-6 trace pass is the natural moment to add rebuild progress emission, alongside any other micro-optimizations that surface from that analysis.

## Candidate mitigations (not yet characterized)

Ordered by leverage / effort:

1. **Add `OnRebuildProgress(...)` event to `IRebuildMetricsListener`** with the same throttling discipline as the bulk-load progress callback. Wire emission at the documented points in `RebuildReferenceSecondaryIndexes`. Small, well-scoped change — probably 50-100 lines plus tests.
2. **Add a `LogRebuildProgress` flag in CLI** (default on) that prints throttled terminal progress lines analogous to bulk-load's. Same throttling.
3. **Document the manual-monitoring fallback.** In `MERCURY.md` or a `docs/operations/` page, explicitly recommend the `du -sh rebuild-tmp/`, chunk-count, and iostat signals operators currently rely on. Useful even after (1) ships — for older Mercury versions or under metrics-disabled configurations.
4. **Add structured `--metrics-out` rebuild progress** alongside the existing per-phase summary records. JSONL volume grows but stays bounded by the throttle.
5. **Estimated-completion line in the CLI.** Simple "based on current rate, this phase finishes in ~N min" — reuses the rate calculation. Slightly speculative (rate can change) but useful for operators planning around a long run.

## References

- `IRebuildMetricsListener` source: `src/Mercury/Diagnostics/IRebuildMetricsListener.cs` — current contract emits `OnRebuildPhase` once per phase
- `JsonlMetricsListener` source: `src/Mercury/Diagnostics/JsonlMetricsListener.cs` — current JSONL emission pattern
- `RebuildReferenceSecondaryIndexes` source: `src/Mercury/Storage/QuadStore.cs:935-…` — where progress emission would be wired
- `LoadProgress` source: `src/Mercury/Diagnostics/LoadProgress.cs` (or similar) — the pattern to mirror for rebuild
- ADR-030 Phase 1 (`docs/adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md`) — established the metrics listener pattern; this limit extends it to rebuild's interior
- Sister limits entries:
  - [bulk-load-memory-pressure](bulk-load-memory-pressure.md) — composes (a memory-pressure event during a silent rebuild phase is doubly hard to diagnose without progress emission)
  - [streaming-source-decompression](streaming-source-decompression.md) — composes (smaller intermediate disk footprint means more capacity for the JSONL output a richer metrics setup would generate)
