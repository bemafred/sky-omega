# ADR-037: Pipelined spill in SortedAtomBulkBuilder

## Status

**Status:** Proposed — 2026-05-06

## Context

Cycle 8 (the first instrumented 21.3 B Reference + Sorted bulk-load, 2026-05-04/05) measured per-spill timings via `SpillEvent` (cycle 7 instrumentation, commit `c79e590`). Two facts emerged:

- **Sort dominates write 12–16:1 across every measured scale** (10 M / 100 M / 1 B / 21.3 B).
- **At 21.3 B, parser wall-clock spent ≈38% blocked on sort** across 3,923 spills — roughly 5 h of the 14 h 15 m parser phase.

The current `SortedAtomBulkBuilder` is single-threaded: `AddOneAtomOccurrence` accumulates records into `_spillBuffer`; when the buffer crosses `_chunkBufferBytes`, `FlushSpillBuffer` runs `SortedAtomStoreExternalBuilder.SpillOneChunk` synchronously — sort + write + book-keeping — and only then returns control to the parser.

Sort and write are not on the parser's critical path semantically. The parser does not depend on either completing before it can produce the next atom. The blocking is purely a sequencing artefact of single-thread design.

This ADR scopes a pipelined spill: parser keeps filling buffers; one background worker thread sorts + writes spilled buffers in the background; parser blocks only when the worker is behind.

Limits register: [`docs/limits/spill-blocks-parser.md`](../../limits/spill-blocks-parser.md) — Triggered with measurement.

## CPU vs I/O budget — why 1 worker is right *now*

The design choice "single background worker doing sort + write together" is not laziness — it is principled given cycle 8's measurements. Both constraints fall out:

**Per-chunk timing budget at 21.3 B (re-derived from cycle 8):**

- Parser wall-clock: 14 h 15 m = 51,300 s across 3,923 spills → **~13 s parser-active per chunk**.
- Parser-blocked share of wall-clock: 38% → ~19,500 s blocked total → **~4.9 s blocked per spill on average** (the inherent spill cost).
- Sort:write ratio 12–16:1 → of that ~4.9 s, sort is ~4.5 s, write is ~0.4 s.

**Sort is CPU-bound.** `List<T>.Sort` with `SequenceCompareTo`-based comparator on a 1 GB / ~17 M-record buffer is pure CPU — no I/O. On the M5 Max (10 P-cores + 4 E-cores), one parallel sort does not compete with the single-threaded parser; they run on different cores.

**Write is I/O-bound, single-SSD.** Cycle 8's hardware is a single Apple Silicon NVMe (no RAID — see [`feedback_eee_discipline`](../../../) and the Phase 5.2 trace doc). Write throughput is maximized by **sequential streams from one writer**. Multiple concurrent writers would interleave on the device queue, fragment the write pattern, and reduce effective throughput. One worker thread doing all writes naturally gives the optimal access pattern — the constraint is satisfied incidentally by the 1-worker design, not deliberately.

**Why 1 worker is sufficient (not just allowed):**

```
parser-active  : ~13.0 s/chunk
sort + write   : ~4.9 s/chunk  (sort ~4.5 s + write ~0.4 s)
                  ───────────
parser is the bottleneck.
```

With one sort+write worker running in parallel with parser-fill, the entire spill cost (4.9 s) hides inside the next chunk's parser-fill window (13 s). Adding more parallel sorts gains nothing — the parser can't feed them faster. Adding parallel writers actively hurts — they would compete on the SSD.

**Memory cost of 1 worker:** one extra 1 GB buffer on LOH (the snapshot in the worker's hand while parser fills the next one). Total LOH residency for the bulk builder: 2 × chunk size = 2 GB. Cycle 8 ran 114 GB / 128 GB peak — 2 GB headroom is present and fits the OS reserve.

## When this design changes (trigger conditions for revision)

The 1-worker design is correct *at cycle 8 numbers*. It would become wrong if the timing budget shifts such that sort + write per chunk exceeds parser-fill per chunk. Possible triggers:

1. **Parser parallelizes.** If parser throughput rises (e.g., parallel BZip2 streaming were piped into a parallel parser), parser-active per chunk drops below the spill cost. Sort becomes the new bottleneck.
2. **Sort cost rises faster than parser cost at scale.** Sort is O(N log N) in records-per-chunk. With chunk size fixed at 1 GB, N is bounded — sort cost per chunk is roughly constant. Parser cost is O(input bytes). Ratio holds at scale unless chunk size changes.
3. **Atom average length drops dramatically.** Smaller atoms → more records per 1 GB chunk → larger N → larger sort cost. Wikidata-class workloads sit at ~50 bytes/atom; FHIR or DBpedia could differ. Worth measuring.
4. **The host changes.** A high-IOPS RAID would not change the analysis (parser is still the bottleneck). A slow SSD where write dominates the ratio would — write would become I/O-bound in absolute terms, not just relatively.

When any of these triggers, the right architectural change is **N sort workers + 1 writer thread**: parser → sort-queue → N parallel sort workers (CPU-parallel) → write-queue → 1 writer thread (sequential SSD writes preserved). Sort scales with available cores; write stays single-threaded. The current ADR's `BlockingCollection` handoff generalizes — it becomes two queues instead of one, but the ownership-transfer + exception-propagation contracts carry over unchanged.

The `worker_queue_depth_at_handoff` metric (Decision 8) is what surfaces this trigger. If steady-state depth > 0 at any scale, the per-chunk budget has flipped — re-measure and consider the split design.

## Hypothesis (falsifiable)

If a single background worker thread runs sort + write while the parser fills the next buffer, then at scales where parser-fill-time per buffer ≥ sort+write-time per buffer (cycle 8 numbers say roughly 13 s vs 7–8 s), `parser_blocked_on_spill` drops from `Σ(sort_duration + write_duration)` to ≈ 0. End-to-end parser wall-clock drops by the proportional amount that was blocked (≈38% at 21.3 B; less at gradient scales where total spill count is lower).

The hypothesis is **falsified** if any of:

1. `parser_blocked_on_spill` does not drop near zero at 100 M (queue depth steady-state > 0).
2. End-to-end wall-clock drops by less than half of the projected savings.
3. Merge phase wall-clock changes by more than ±5% (would indicate the change leaked into the merge path).

## Decision

### 1 — Single background worker, bounded handoff queue

Producer: parser thread (the existing single thread that calls `AddTriple` / `AddOneAtomOccurrence`).
Consumer: one background worker thread.
Handoff: `BlockingCollection<SpillJob>` with `BoundedCapacity = 1`.

Why not `Channel<T>`: same primitive class, larger surface (async API, multi-subscriber semantics) we don't use. `BlockingCollection` is the minimum primitive that gives synchronous bounded-block semantics + clean `CompleteAdding` shutdown.

Why not async / `Task`-per-spill: the parser is `IEnumerable<byte[]>`, synchronous all the way down. Making `AddTriple` async ripples through `QuadStore.BeginBatch` and the bulk-load API for no benefit — there is no awaitable between AddTriple calls. A long-lived background thread plus a synchronous handoff matches the actual call-shape.

Why bound = 1: with 2 buffers alive (1 in parser's hand, 1 in queue) the pipeline overlaps parser-fill with sort+write — exactly enough to hide spill cost when fill-time ≥ spill-time. More buffers add memory cost (1 GB each on LOH) without throughput improvement in that regime. If measurement shows steady-state queue depth > 0, the bound is the one number that changes — not a structural redesign.

### 2 — Buffer ownership transfers atomically

Today `_spillBuffer` is the parser's accumulator AND the sort target. Pipelined version separates them:

```
parser fills _spillBuffer (List<(byte[], long)>);
when full:
    var snapshot = _spillBuffer;
    _spillBuffer = new List<(byte[], long)>();
    _spillQueue.Add(snapshot);   // BlockingCollection.Add — blocks if bound reached
```

After `Add`, the parser **never** touches `snapshot`. The worker has exclusive ownership. The List itself is not concurrently accessed; the byte[] arrays inside were allocated once per atom (`bytes.ToArray()` in `AddOneAtomOccurrence`) and never mutated after addition. No locks beyond what `BlockingCollection.Add`/`Take` already provides; no `volatile` fields.

### 3 — Worker accumulates its own chunk-paths list

Today `_chunkFiles` is the parser-thread's list. The worker writes the path of each spilled chunk to its own list, never `_chunkFiles`. At `Finalize`:

```
_spillQueue.CompleteAdding();
_workerTask.Wait();          // worker drains the queue and exits
_chunkFiles.AddRange(_workerChunkFiles);
```

After `_workerTask.Wait()` returns, no other thread accesses `_workerChunkFiles`. The parser merges into `_chunkFiles` and proceeds to `MergeAndWrite` exactly as today.

`_chunkFiles` ordering is preserved because the worker processes spills in the order they arrive, and the queue is FIFO.

### 4 — Exception propagation

Worker wraps its loop in `try { ... } catch (Exception ex) { _workerException = ex; _spillQueue.CompleteAdding(); }`.

Parser checks `_workerException` on every `AddTriple` (at the top of `AddOneAtomOccurrence`). If non-null, the parser throws — surfacing the exception on the parser's stack with the original wrapped as `InnerException`. `Finalize` does the same check before calling `MergeAndWrite`.

This is the load-bearing bit: a silent loss of a sort/write exception would corrupt the resulting store. The check must be on every AddTriple, not just at handoff.

### 5 — Disposal without Finalize

If the bulk builder is `Dispose`d without `Finalize` (cancellation, error path), the worker thread must shut down without leaking the in-flight buffer. `Dispose`:

```
if (!_finalized && _workerTask is not null)
{
    _spillQueue.CompleteAdding();
    try { _workerTask.Wait(TimeSpan.FromSeconds(30)); } catch { /* ignore */ }
}
_spillQueue.Dispose();
// then existing tempDir cleanup
```

Worker thread sees `CompleteAdding` and exits its `foreach` over `_spillQueue.GetConsumingEnumerable()`. Any in-flight buffer being processed completes normally and writes its chunk; the next `Take` returns false and the worker exits.

### 6 — Listener thread-safety contract

`OnSpill`, `OnMergeProgress`, `OnMergeCompleted` are now invoked from either parser or worker thread. `JsonlMetricsListener` is already thread-safe (uses an internal bounded channel with single-writer semantics — see `Mercury.Diagnostics.JsonlMetricsListener`). Other implementations of `IObservabilityListener` must be thread-safe; this becomes a documented contract on the interface.

### 7 — Metric: `parser_blocked_on_spill_ms`

New cumulative counter on `SortedAtomBulkBuilder`. Incremented at every `_spillQueue.Add` call by the wall-time `Add` blocked. Emitted at `Finalize` via either:

- A new event `BulkBuilderCompletedEvent { ParserBlockedOnSpillMs, TotalSpillCount, ... }` on `IObservabilityListener`, OR
- Extension of `MergeCompletedEvent` with the field.

The first is cleaner — the bulk builder's end-of-phase summary is a different concept from the merge's end-of-phase summary, even though they happen in succession. Decision: new event. Avoids overloading `MergeCompletedEvent` with bulk-builder-specific state.

`SpillEvent` is unchanged. Per-spill `sort_duration` and `write_duration` continue to be the inherent-cost-of-spill measurements.

### 8 — Optional secondary metric: `worker_queue_depth_at_handoff`

Each spill captures the queue depth observed at handoff time (via `_spillQueue.Count` immediately before `Add`). Emitted on `SpillEvent` as a new field, OR via the new bulk-builder-completed event as a histogram.

This is the load-bearing measurement for tuning the bound. If steady-state depth is always 0 → bound 1 is enough. If it spikes or steady-states higher → increase bound and re-measure.

## Validation protocol

### Phase 1 — Unit tests

- Existing tests pass unchanged (sequential happy path).
- New test: stress the queue saturation path. Construct builder with deliberately small `chunkBufferBytes` (1 MB) and feed inputs faster than worker can spill (synchronous tight loop). Assert: handoff blocks; parser_blocked > 0; final atoms count matches expected; ordering preserved.
- New test: worker exception surfaces on parser thread. Inject a faulting `IObservabilityListener.OnSpill` that throws after 2 spills. Assert: subsequent `AddTriple` throws with original exception as inner.
- New test: Dispose-without-Finalize completes within 30 s with no leaked tasks.

### Phase 2 — Gradient (perf claim)

A/B against 1.7.49 baseline at 1 M / 10 M / 100 M Wikidata Reference + Sorted bulk-load. From Mercury's JSONL only (no external tools):

| Metric | Source | Expected on 1.7.49 | Expected on 1.7.50 |
|---|---|---|---|
| End-to-end wall-clock | `load_complete` event | baseline | smaller |
| `parser_blocked_on_spill_ms` | new `bulk_builder_completed` event | ≈ Σ(sort+write) | ≈ 0 |
| Σ(`spill.sort_duration` + `spill.write_duration`) | `spill` events | baseline | unchanged |
| `merge_completed.duration_ms` | `merge_completed` event | baseline | within ±5% |
| `worker_queue_depth_at_handoff` distribution | new field | n/a | mostly 0, rarely 1 |

100 M is the load-bearing scale: 18 chunks gives steady-state behavior; 1 M (1 chunk) and 10 M (2 chunks) don't exercise the queue. The 1 M and 10 M runs are smoke; the 100 M run is the perf claim.

### Phase 3 — Production validation (cycle 9)

21.3 B Reference + Sorted bulk-load with the new metric. Confirms the cycle 8-projected ~5 h parser wall-clock reduction at scale.

## Consequences

### Positive

- ~38% parser wall-clock reduction at 21.3 B (cycle 8 projection) → ~5 h saved per full run.
- The hot path stays single-threaded *for the parser*; only spill-handoff is concurrent. Parser inner loop unchanged.
- Same primitive (BlockingCollection, BCL) used across the substrate; no new dependency.
- Memory cost: +1 GB peak working set (one extra buffer alive at handoff time). Acceptable on 128 GB hosts; documented.

### Negative / risks

- First concurrency on the bulk-load hot path. Every buffer ownership transfer is a correctness-critical handoff. The unit tests and exception-propagation contract are mandatory, not nice-to-have.
- Listener contract becomes "must be thread-safe." `JsonlMetricsListener` complies. Future listeners must too — documented on the interface.
- If parser-fill < sort+write at some scale (not measured), 2 buffers don't suffice and queue saturates. Mitigation: the queue-depth metric makes this measurable; the bound is the one parameter to tune.

### Limits-register entries

- Resolves [`spill-blocks-parser.md`](../../limits/spill-blocks-parser.md) (in-flight; production validation = cycle 9).
- Reinforces [`metrics-coverage-review.md`](../../limits/metrics-coverage-review.md) — every >1-min phase emits its own end-of-phase summary event.

## References

- [`docs/limits/spill-blocks-parser.md`](../../limits/spill-blocks-parser.md) — surfacing limit
- [`urn:sky-omega:obs:sort-write-ratio-cross-scale-2026-05-04`](#) (Mercury) — cycle 7 cross-scale measurement
- [`urn:sky-omega:incident:cycle8-21b-instrumented-2026-05-04`](#) (Mercury) — surfacing run
- `src/Mercury/Storage/SortedAtomBulkBuilder.cs` — implementation site
- `src/Mercury.Abstractions/SortedAtomMetrics.cs` — `SpillEvent`, `MergeCompletedEvent`; new `BulkBuilderCompletedEvent`
- `src/Mercury.Abstractions/IObservabilityListener.cs` — listener contract update
