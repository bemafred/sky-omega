# Observability coverage map

**Purpose.** Inventory what Mercury exposes as observable signals, what it does not, and the discipline that long-running operations must follow before being considered shippable. Surfaced as a load-bearing concern by the 2026-05-04 21.3 B run, where 8+ hours of merge-phase progress were unobservable from outside the process.

This is a *coverage map*, not a metrics catalogue. The catalogue (event types, schemas) lives in [metrics-emission.md](metrics-emission.md). This document answers a different question: **for any given long-running operation, can an outside observer tell whether it's progressing, blocked, or degraded — and if so, by how much?** The 21.3 B merge case was a clear "no" for 8 hours. That's the gap this map exists to characterize and prevent.

## Discipline

Every operation projected to take more than **1 minute** in production must emit periodic progress to the JSONL listener (or equivalent) before it is considered shippable.

"Progress" means at minimum:
1. **A heartbeat** — proof the operation is still running, not hung
2. **A throughput proxy** — records processed, bytes written, percent complete (whichever is meaningful)
3. **A live-state probe** — a key piece of internal state that distinguishes healthy from degraded operation (e.g., pool hit rate, queue depth, current resource consumption against limits)

The rationale is the EEE methodology: an operation without progress emission is opaque to Epistemics. A 25 h ingest run with one summary line at the end isn't characterization, it's hope. Sky Omega's "characterization, not vibes" stance makes this discipline non-optional at scale.

## What is currently observable

### Bulk-load (parser phase)

| Signal | Source | Coverage |
|---|---|---|
| Triples processed (cumulative + recent rate) | `LoadProgressTick` event, `--metrics-state-interval` | ✓ per-tick |
| GC heap, working set | `ProcessStateProducer` | ✓ per-tick |
| Disk free | `DiskFreeProducer` | ✓ per-tick |
| HashAtomStore intern rate, load factor, probe distance | `AtomStoreProducers` | ✓ when HashAtomStore active |
| File path, format, mode | `LoadStartedEvent` | ✓ once at start |
| Final summary | `LoadCompletedEvent` | ✓ once at end |

### Rebuild phase

| Signal | Source | Coverage |
|---|---|---|
| Per-index entries processed | `RebuildProgressTick` event | ✓ per-tick |
| Rebuild start/complete | events | ✓ once each |
| Sub-phase classification (GPOS / Trigram) | event field | ✓ |

### Process-level

| Signal | Source | Coverage |
|---|---|---|
| RSS, GC heap, LOH delta | `ProcessStateProducer` | ✓ per `--metrics-state-interval` |
| Disk free | `DiskFreeProducer` | ✓ per-tick |
| Triple count (current store) | `StoreStateProducer` | ✓ |

## What is not currently observable

### Long-running internals (the headline gap)

Operations >1 minute that have **no progress emission**:

| Operation | Code surface | Worst observed (21.3 B) |
|---|---|---|
| `SortedAtomStoreExternalBuilder.MergeAndWrite` | k-way merge over all atom-occurrence chunks | 8+ hours opaque |
| `ExternalSorter<T>.Merge` (used by ResolveRecord resolver AND bulk GSPO) | external-merge sort | hours, used twice in the pipeline |
| `SortedAtomBulkBuilder` per-spill (sort + write) | called per chunk during ingest | ~10 s per spill × hundreds of spills, no per-event signal |
| GSPO sort-and-write at end of bulk | bulk primary-index materialization | ~2-3 h projected, single end-state point |
| Drain `EnumerateResolved` into bulk sorter | replay phase | ~1-2 h, no progress |
| WAL replay at Cognitive open | startup recovery | scales with WAL size, no progress |

### Live state vs aggregate counters

Mercury holds rich live state in memory but emits aggregates only. Examples:

| Live state | Currently exposed? | Why it would matter |
|---|---|---|
| `BoundedFileStreamPool.Hits` / `Misses` (streaming) | only at end of merge | catch eviction storms in flight |
| Priority-queue depth and current-min-prefix | no | merge progress + locality measurement |
| `ChunkReader._offset` | no | resume-state visibility, stuck-detection |
| Resolver spill backlog vs merge consume rate | no | back-pressure detection |
| `SortedAtomStore` current `_atomCount` during merge | no | mid-phase progress proxy |
| `atoms.atoms` write-position during merge | derivable from disk, not emitted | mid-phase progress proxy |

### Configuration disclosure at startup

The 2026-05-03 Hash/Sorted dispatch bug went undetected because **at run startup, there was no log line declaring which AtomStore implementation was active**. The information was in the persisted `store-schema.json` but not in the live run output.

Missing startup-banner items:

- AtomStore implementation in effect (and the derivation: profile → schema → impl)
- Effective chunk size (`SortedAtomBulkBuilder.DefaultChunkBufferBytes`)
- Pool size in effect (hard-cap value vs auto-sized vs `MERCURY_MERGE_POOL_SIZE` override)
- Resolver chunk size
- File-pool per-stream buffer size
- Whether disk-backed `AssignedIds` is in use
- Whether `--limit` is bounding ingest

A standardized startup banner emitting every load-bearing tuning and dispatch decision would have caught the dispatch bug in cycle 1 instead of cycle 4.

### Decisions and invariant-approach states

Things the code checks but does not surface as observable signals:

- ADR-029 profile-capability checks (throw on violation, no threshold-approach warning)
- ADR-034 single-bulk-load enforcement (throws, no "you're approaching this constraint" warning)
- FD consumption vs OS limit (we silently saturated at ~4,090 in cycle 6 with no signal)
- Memory budget vs RAM (no proactive pressure emission before paging)

### Query phase (acknowledged gap, not addressed here)

SPARQL operator-level metrics, executor cancellation timing, and per-query I/O are largely opaque from outside. Touched by `cancellable-executor-paths.md` (Triggered) and not the focus of this map; flagged for future round.

## Mitigation: the systemic answer

This map's existence is part of the mitigation. The discipline ("every >1-min operation emits progress") is the rest. What follows is the concrete sequence:

1. **Wire `MergeAndWrite` to emit per-N-records progress.** Hooks into the existing `JsonlMetricsListener`. Per-record-batch: records_processed, atoms_emitted, current_pool_open, current_pool_misses, resolver_records_spilled. Once-per-N-records (e.g., 100 M) is plenty.

2. **Wire `ExternalSorter<T>.Merge` similarly.** Used in two distinct paths (ResolveRecords drain, bulk GSPO writes); instrumentation here covers both.

3. **Per-spill emission in `SortedAtomBulkBuilder`.** Each `SpillOneChunk` call emits: chunk_index, records_in_chunk, sort_duration_ms, write_duration_ms. Lets us measure the spill-blocks-parser cost (sibling limits entry) directly.

4. **Startup banner.** Single multi-line emission at run start covering all configuration items listed above.

5. **Approach-warnings on hard limits.** When in-flight resource consumption exceeds 80% of an enforced limit, emit a warning event. Applies to FD count, single-bulk-load constraint, memory budget vs RAM.

6. **Live-state producers.** New `MergeStateProducer` exposes pool stats, queue depth, current write-position. Hooks into the same periodic state-emission loop as `ProcessStateProducer`.

The natural sequence is (1) immediately for cycle 7, (2)-(3) before any further large-scale runs, (4) as a one-shot quality-of-life win, (5)-(6) as the proper substrate-discipline build-out.

## References

- `docs/architecture/technical/metrics-emission.md` — the metrics catalogue (event types, schemas)
- `docs/limits/observability-coverage-gap.md` — limits-register entry that gates this work
- `docs/limits/spill-blocks-parser.md` — sibling limit; (3) above directly serves it
- ADR-035 (Phase 7a metrics infrastructure) — what we have, why
- 2026-05-04 21.3 B run — the surfacing observation; 8 h of opaque merge phase
