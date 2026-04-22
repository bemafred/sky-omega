# ADR-033: Bulk-Load Radix External Sort

## Status

**Status:** Accepted — 2026-04-22 (validated at 100M and 1B end-to-end; see [phase 5 validation](../../validations/adr-033-phase5-bulk-radix-2026-04-22.md))

## Context

[ADR-032 Phase 4](../../validations/adr-032-phase4-trigram-radix-2026-04-22.md) reduced the 100M Reference rebuild from 511 s to 48.64 s by applying radix sort + external merge + sequential append to both the GPOS and trigram secondary indexes. Eliminating their B+Tree write amplification converted random-write hot loops into bandwidth-friendly sequential appends — the architectural prediction from [Phase 5.2](../../validations/adr-030-phase52-trace-2026-04-21.md) confirmed in two independent paths.

The cost balance has shifted. Before Phase 4:

| Phase | 1 B wall-clock | Share of combined |
|---|---:|---:|
| Bulk-load | 50 m 17 s | 27% |
| Rebuild | 3 h 7 m | 73% |

After Phase 4 (linear extrapolation from 100 M):

| Phase | 1 B wall-clock | Share of combined |
|---|---:|---:|
| Bulk-load | 50 m 17 s | **~75-80%** |
| Rebuild | ~10-18 min | ~20-25% |

Bulk-load is now the dominant phase. And the bulk-load `AddCurrentBatched` → `_gspoReference!.AddRaw(...)` path is structurally identical to the random-insert pattern ADR-032 just eliminated for rebuild — same B+Tree write amplification, just on the primary GSPO index instead of the GPOS or trigram secondaries.

The mechanism is the same: triples arrive from the parser in input-file order, atom IDs are assigned in arrival order, and the resulting (Graph, Subject, Predicate, Object) keys are random with respect to GSPO sort order. Each B+Tree leaf gets touched many times during bulk-load — once for each triple that happens to land in it after some unrelated atom assignments shifted intervening leaves.

This ADR extends ADR-032's architecture to the bulk-load path. The implementation reuses every primitive: `RadixSort`, `ExternalSorter<ReferenceKey, ReferenceKeyChunkSorter>`, `ReferenceQuadIndex.AppendSorted`. The integration point shifts from `RebuildReferenceSecondaryIndexes` to `AddCurrentBatched`/`CommitBatch`.

### Why this is a separate ADR, not an amendment to ADR-032

ADR-032's scope, validation arc, and commit history are tied specifically to the rebuild path. Extending the same architecture to a different code path with a different invocation lifecycle (per-call streaming during a batch session vs once-per-rebuild) deserves its own validation arc. Kept clean: ADR-032 = rebuild, ADR-033 = bulk-load, both share primitives.

## Decision

### 1 — Buffer bulk-load writes through ExternalSorter

When `_bulkLoadMode` is set and a Reference batch is active (`_referenceBulkActive == true`), `AddCurrentBatched` pushes the resolved (G, S, P, O) ReferenceKey to a per-batch `ExternalSorter<ReferenceKey, ReferenceKeyChunkSorter>` instead of writing it directly to GSPO via `AddRaw`. The sorter is allocated in `BeginBatch`, populated by every `AddCurrentBatched` call, drained in `CommitBatch` via `AppendSorted`, and disposed (with temp-file cleanup) in `CommitBatch`/`RollbackBatch`.

```
BeginBatch (Reference + _bulkLoadMode):
    _bulkSorter = new ExternalSorter<ReferenceKey, ReferenceKeyChunkSorter>(
        tempDir: {storeRoot}/bulk-tmp,
        chunkSize: 16M)
    _referenceBulkActive = true

AddCurrentBatched (per triple):
    [graphId, subjectId, predicateId, objectId] = atom-resolve as today
    _bulkSorter.Add(new ReferenceKey { Graph, Primary=Subject, Secondary=Predicate, Tertiary=Object })

CommitBatch (Reference + _bulkLoadMode):
    _bulkSorter.Complete()
    _gspoReference.BeginAppendSorted()
    while _bulkSorter.TryDrainNext(out var key):
        _gspoReference.AppendSorted(key)
    _gspoReference.EndAppendSorted()
    _bulkSorter.Dispose()        // removes {storeRoot}/bulk-tmp
    _gspoReference.Flush()
    _referenceBulkActive = false

RollbackBatch (Reference + _bulkLoadMode):
    _bulkSorter?.Dispose()       // removes {storeRoot}/bulk-tmp
    _referenceBulkActive = false
```

### 2 — Atom interning is unchanged

Atom IDs are still resolved during the parser → `AddCurrentBatched` call path, exactly as today. The atom store sees a streaming flow of `Intern` calls in input order; only the GSPO key insertion is deferred. This keeps the atom-side hot path independent and allows ADR-028 (rehash-on-grow) and any future atom-store work to compose without entanglement.

The (G, S, P, O) tuples accumulated in the sorter carry only the resolved atom IDs (4 × 8 B = 32 B per entry). The sorter's chunk files contain only those IDs, never strings.

### 3 — Restrict to `_bulkLoadMode` sessions

The radix sort path is gated on the existing `_bulkLoadMode` flag (set via `QuadStorePoolOptions.BulkMode` at store open). When false (the default for interactive REPL sessions, MCP server sessions, etc.), `AddCurrentBatched` retains the current `_gspoReference!.AddRaw(...)` direct-insert behavior.

Two reasons:

- **`AppendSorted` requires an empty target.** The bulk path is "open empty store, load file, close." Subsequent batches would already-populate GSPO, violating AppendSorted's non-decreasing-keys contract on the next batch. Interactive incremental writes need the random-insert path.
- **Latency vs throughput.** The sorter pattern can't write anything to GSPO until the entire batch is parsed and merged — appropriate for a load operation but wrong for an interactive insert that wants fast individual-write latency.

For the same reason: only the *first* batch of a `_bulkLoadMode` session uses the radix path. If a caller does multiple BeginBatch/CommitBatch cycles within one bulk session (uncommon but possible), only the first uses sort; subsequent batches fall back to `AddRaw`. The flag tracking this is in the QuadStore.

### 4 — `--min-free-space` raised for bulk

The temp file footprint at scale is significant. At 21.3 B Reference, the worst case is the entire input materialized as sorted chunk files: 21.3 B × 32 B = **~680 GB** of temp space at peak (just before the merge phase consumes them). Mercury's `--min-free-space` for bulk currently defaults to 100 GB; this needs to be raised to ~700-800 GB for the 21.3 B case, or a hierarchical merge introduced (per ADR-032 Section 2 fallback).

For Phase 5 gradient validation up to 1 B (32 GB temp peak), the existing 100 GB default is sufficient. The 21.3 B tooling adjustment lands separately.

### 5 — Reuse all ADR-032 primitives

No new abstractions. The implementation footprint:

- `BeginBatch` Reference path: ~3 lines added to allocate `_bulkSorter`
- `AddCurrentBatched` Reference path: ~5 lines added (gate on `_bulkLoadMode`, push to sorter)
- `CommitBatch` Reference path: ~10 lines added (drain via AppendSorted)
- `RollbackBatch` Reference path: ~3 lines added (dispose sorter)
- One new private field: `ExternalSorter<ReferenceKey, ReferenceKeyChunkSorter>? _bulkSorter`

Total: ~25-30 lines of integration code. All correctness tests for the underlying sort, AppendSorted, and ExternalSorter already exist (Phases 1-3).

## Consequences

### Positive

- **Bulk-load throughput projects to drop substantially.** The actual GSPO-write fraction of bulk-load wall-clock has not been profiled directly post-Phase-4, but the structural argument is identical to rebuild's GPOS path (3× faster) and the magnitude depends on what fraction of bulk-load is GSPO-write vs parser+atom-resolve. Conservative estimate: **bulk-load drops 1.5-3×** depending on profile mix; aggressive estimate: 5×+ if the GSPO write dominates today's bulk cost. Phase 5 gradient validation will measure.
- **Architectural symmetry.** Bulk-load and rebuild now use the same access pattern (sequential append after radix-external-sort). The Phase 5.2 hypothesis applies uniformly across the entire write path.
- **Composes with future micros.** With architecture settled, the next round of cost (UTF-8 lowercase, atom store hot path, hash quality, B+Tree page size) can be measured against a stable baseline. Pre-Phase-4 trace would have been confounded by GC/lock noise.
- **Disk-temp ownership pattern reuses ADR-032 lifecycle.** Same `{storeRoot}/{phase}-tmp/` directory convention, same orphan-cleanup-on-construct, same Dispose-removes recursive cleanup.

### Negative

- **Behavior change: kill mid-load is now a total loss.** Today's `AddCurrentBatched` writes incrementally — interrupting bulk-load partway through leaves a partial GSPO that subsequent operations could observe. With the sorter, no GSPO writes happen until `CommitBatch` drains. Process kill before commit means zero progress was persisted. The ADR-026 contract ("delete the store and retry") already covers this case formally; the practical change is that "partial progress" is no longer observable from within a partial bulk run.
- **Temp disk space requirement.** ~680 GB at 21.3 B. Need to bump `--min-free-space` default for bulk (or add hierarchical merge). Trivial at 1 B (32 GB) and below.
- **Memory floor 512 MB.** The chunk scratch buffer is allocated on the LOH at BeginBatch and lives until CommitBatch. For a 1 B bulk-load that's 50 minutes of LOH residency. Acceptable on the M5 Max (128 GB RAM); worth flagging for smaller hosts.
- **No streaming visibility during load.** Today the user sees GSPO populated incrementally and could in principle query partial results. With the sorter, GSPO is empty until commit. Lost capability is minor — the existing `--bulk-load` flow already disables queries during the operation.

### Risks

- **Atom-ID stability across the sorter.** The sort runs on atom IDs. If atom IDs were reassignable post-resolve (they're not — they're append-only IDs into the AtomStore), the sort would be invalidated. The append-only invariant per ADR-020 makes this risk zero. Document explicitly.
- **Crash mid-merge.** Like rebuild, a crash leaves chunk files in `{storeRoot}/bulk-tmp/`. Recovery is the same: `QuadStore.Open` checks for orphan tmp dirs; `_indexState != Ready` means re-do. The bulk-load case adds one wrinkle: the GSPO might be partially populated by an interrupted AppendSorted. Recovery: clear GSPO and re-load. The ADR-026 "delete and retry" model already covers this in spirit.
- **First-batch-only restriction may surprise callers.** If a programmatic caller does multiple BeginBatch cycles in `_bulkLoadMode`, only the first benefits. Documented behavior; edge case not a real workflow today.
- **Interaction with future Cognitive bulk path.** Cognitive profile currently runs bulk-load through WAL → batch buffer → ApplyToIndexesById. Different code path — this ADR does not touch it. Cognitive equivalent (if useful) would be a follow-up. Reference's bulk-load is the only path this ADR affects.

## Implementation plan

**Phase 1 — Integration**
- Add `_bulkSorter` field to `QuadStore` (nullable, lifetime per-batch).
- Add a private `_bulkSortActive` flag to track first-batch-only restriction.
- Modify `BeginBatch` Reference path to allocate sorter when `_bulkLoadMode && !_bulkSortActive`.
- Modify `AddCurrentBatched` to push to sorter when `_bulkSortActive`, else `AddRaw`.
- Modify `CommitBatch` Reference path to drain sorter via `AppendSorted` before flushing.
- Modify `RollbackBatch` Reference path to dispose sorter.
- Tests: a new BulkLoadSorterTests covering first-batch-uses-sorter, second-batch-uses-AddRaw, RollbackBatch cleans temp dir, large input (10K triples) end-to-end equivalence vs the AddRaw path.

**Phase 2 — Gradient validation**
- 1 M Reference bulk-load (existing wiki-1m-ref dataset). Capture wall-clock + iostat.
- 10 M Reference bulk-load. Capture.
- 100 M Reference bulk-load. Capture.
- 1 B Reference bulk-load. Full end-to-end run. Capture.
- Validation doc per scale, comparing to pre-Phase-5 baselines.

**Phase 3 — 21.3 B Wikidata Reference end-to-end**
- Combined bulk + rebuild for full Wikidata.
- Target: combined wall-clock < 24 hours.
- Publish the benchmark.

**Phase 4 — ADR transitions**
- Status Proposed → Accepted after Phase 1 tests + 100M validation.
- Status Accepted → Completed after Phase 3 publishes 21.3 B benchmark.

## Open questions

- **Should the sorter be raised on the first non-batched `AddCurrent` call too?** Currently restricted to `BeginBatch`/`AddCurrentBatched`/`CommitBatch` — the `AddCurrent` path (no batch) goes straight to `AddRaw`. Probably correct as-is (`AddCurrent` is for incremental updates, not bulk), but worth flagging if the bulk-load pipeline grows beyond batched access.
- **First-batch-only or all-batches-in-`_bulkLoadMode`?** Decision in this ADR is first-batch-only (subsequent batches against now-populated GSPO violate AppendSorted's contract). Alternative: clear GSPO between batches, but that throws away progress. First-batch-only is the right semantic for a bulk-load operation that is fundamentally one-shot.
- **Should `_bulkSorter` use `MemoryMappedFile` for chunk storage instead of raw `FileStream`?** Same trade-off discussed in ADR-032 Section 7; defer until measurement justifies.
- **Does the same pattern apply to Cognitive bulk-load?** Cognitive's bulk path is different (WAL + batch buffer); the gain may or may not materialize the same way. Out of scope for this ADR; revisit after Phase 3 if Cognitive becomes a target.

## References

- [ADR-026](ADR-026-bulk-load-path.md) — original bulk-load contract; "delete the store and retry" model
- [ADR-029](ADR-029-store-profiles.md) — Reference profile + 32-byte ReferenceKey
- [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) — Decision 5 split bulk into primary-only + rebuild
- [ADR-032](ADR-032-radix-external-sort.md) — sister ADR; rebuild side of the same architecture
- [Phase 5.2 trace + I/O validation](../../validations/adr-030-phase52-trace-2026-04-21.md) — original measurement that motivated the architecture
- [ADR-032 Phase 4 validation](../../validations/adr-032-phase4-trigram-radix-2026-04-22.md) — most recent measurement; established the cost-balance shift
