# ADR-030: Bulk Load and Rebuild Performance Architecture

## Status

**Status:** Completed — 2026-04-26 with phase reconciliation. Phase 1 (measurement infrastructure) shipped 1.7.31 and is the foundation every subsequent perf claim measures against. **Phases 2 (parallel rebuild, 1.7.36) and 3 (sort-insert, 1.7.37) shipped, then reverted in 1.7.38** after the Phase 5.2 dotnet-trace + iostat work (`adr-030-phase52-trace-2026-04-21.md`) exposed the binding bottleneck as write amplification, not CPU. The replacement architecture lives in **ADR-032** (radix external sort for rebuild, shipped 1.7.39–1.7.42) and **ADR-033** (radix external sort for bulk-load, shipped 1.7.43), which together delivered the wall-clock wins this ADR originally targeted: 100 M rebuild 511 s → 48.64 s (10.5× faster), 1 B end-to-end ~3h57m → 60m36s (3.92× combined speedup), 21.3 B Phase 6 end-to-end at 85 h. Phases 2 and 3 are therefore **Superseded** by ADR-032/033; Phase 1 stands as Completed.

## Context

Two days of gradient work (2026-04-17 through 2026-04-19) took bulk-load throughput from 57.7 K triples/sec to **331 K/sec at 1 B** — a 4.7× improvement driven by profile-identified fixes (see [bulk-load-gradient-2026-04-17.md](../../validations/bulk-load-gradient-2026-04-17.md) and [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md)). **Rebuild**, validated today for the first time at scale, came in at **3 h 7 m 32 s for 1 B triples** — five times slower in wall clock than the bulk load of the same data.

Neither has been pushed to its structural limit. The bulk-load profile showed **60-70 % of CPU cores idle** at peak — the critical path is serial. The rebuild path has **never been profiled**. And the existing rebuild is doing things that are algorithmically wasteful given what we know about the input.

The forward-extrapolation from 1 B to full Wikidata (21.3 B) under current performance:

| Phase | 1 B wall | 21.3 B projection | Real-world feasibility |
|---|---|---|---|
| Bulk load (linear) | 50 m 17 s | **~18 h** | Acceptable for a one-time load |
| Rebuild (super-linear, ~16× per decade) | 3 h 7 m | **~70 h** | Borderline — an experienced-engineer workweek |
| Combined | ~4 h | ~88 h (~3.5 days) | Friction for iteration |

Rebuild dominates. Closing the gap is what makes full Wikidata a routine operation instead of a ceremony.

There are three **architectural** levers (not micro-optimizations) that we know about but haven't exercised:

1. **Rebuild input is already sorted.** A GSPO range scan produces triples in primary-key order. Feeding that sorted stream into the secondary indexes via the *random-insertion* B+Tree path is wasting what the scan gives us for free. A sort-insert or bulk-append fast path is algorithmically cheaper, not just faster.

2. **Rebuild targets are independent.** GPOS, GOSP, TGSP, and Trigram each read the same GSPO scan but write to their own file. Today they run sequentially. They can run in parallel, one thread per target. On the M5 Max (14 P-cores + 10 E-cores), this is 3-4× headroom unused.

3. **Measurement infrastructure is absent.** We have no standing way to capture query latency percentiles, per-phase rebuild timing, or allocation histograms across runs. Every profile run is a one-off. Without instrumentation, further optimization decisions are based on anecdote.

This ADR captures the architectural decisions that unlock those three levers. It does not propose specific micro-optimizations — those emerge from the measurement infrastructure once it exists.

### Why this is an ADR and not "just do profiling"

Each of the three levers requires an architectural change that is not free to revert:

- Sort-insert introduces a distinct code path through the B+Tree (`AppendSorted` vs `Insert`) with its own invariants (sorted input mandatory — feeding unsorted data corrupts the tree).
- Parallel rebuild reshapes `RebuildSecondaryIndexes` from a sequential loop into a fan-out/join pattern. Cross-thread coordination is net-new code.
- Measurement infrastructure is a small public surface: opt-in listeners, per-phase timers, histogram output. Once callers depend on it, changing it is a breaking change.

None of these are rollback-cheap. That's what an ADR is for.

## Decision

### 1 — Sort-insert fast path for rebuild

`RebuildIndex` currently iterates a GSPO scan and calls `target.AddRaw(...)` for each triple. `AddRaw` walks the B+Tree from the root, finds the leaf, checks for splits, inserts. This is correct for random insertion — it is **wasteful** when the caller guarantees sorted input.

Add `QuadIndex.AppendSorted(TemporalKey key)` (and the corresponding `ReferenceKey` variant from [ADR-029](ADR-029-store-profiles.md)) with an explicit contract:

- Caller guarantees keys arrive in **non-decreasing** order per the index's `KeyComparer`.
- Implementation writes directly to the current "tail" leaf page, creating new leaves on the right edge as needed.
- Internal-node construction happens bottom-up at the end of the sorted run (or in a periodic fixup).
- No full-tree traversal per insert.
- Contract violation (unsorted input) is a hard error in DEBUG builds, undefined behavior in RELEASE (same discipline as `Span<T>` bounds).

`RebuildIndex` becomes: sort isn't needed (GSPO scan is already sorted for GPOS if we re-map dimensions — see detail below), wrap the loop in `AppendSorted`, skip the tree-walk overhead. Expected improvement is substantial because sort-insert into a B+Tree is O(1) amortized per entry; random-insert is O(log N).

**Important detail.** GSPO scan order is (Graph, Subject, Predicate, Object). When rebuilding GPOS, the target sort is (Graph, Predicate, Object, Subject) — different order, so the GSPO scan output is **not** sorted for GPOS input. Options:

- **(a) Sort in memory per chunk.** Scan GSPO in chunks of N million, sort by the target's key order, append. Requires O(N × entry-size) memory per chunk; acceptable at 16-64 M triples.
- **(b) External merge-sort to disk.** For chunks exceeding RAM (> ~1 B in one go). Two-phase: write sorted runs to temp files, then k-way merge into the target. Classic external-sort algorithm.
- **(c) Accept random-insert for dimension-remapped indexes and use sort-insert only for structurally sorted cases.** TGSP (same entity order as GSPO, different time sort) might benefit; GPOS/GOSP definitely don't arrive sorted.

The answer is probably a mix — **(a) for GPOS/GOSP at chunk scale, no sort needed for TGSP since sort order differs on secondary dimensions only**. Sorting 100 M keys in memory (88 B each = 8.8 GB) is feasible on a 128 GB machine. At full Wikidata (21.3 B / 100 M = 213 chunks), this is a lot of chunked sorts but each is a cache-friendly operation.

### 2 — Parallel rebuild across independent target indexes

`QuadStore.RebuildSecondaryIndexes` currently runs GPOS → GOSP → TGSP → Trigram sequentially. Each phase does its own GSPO scan. Running them in parallel has two wins:

- **Wall-clock savings.** 3 phases in parallel on a 14 P-core M5 Max: ~3× wall-clock reduction for the B+Tree secondaries alone.
- **Shared GSPO scan.** A single scan of GSPO can feed all three consumers via a broadcast channel. Currently we scan GSPO three times — once per target index.

The implementation shape:

```csharp
// Conceptual — not final API
var gspoEnum = _gspoIndex.QueryHistory(default, default, default);
var channel = new BroadcastChannel<TemporalQuad>(consumerCount: 3);

// Spawn consumer threads, one per target
var t1 = Task.Run(() => RebuildFromChannel(_gposIndex, "GPOS", ..., channel.Reader1));
var t2 = Task.Run(() => RebuildFromChannel(_gospIndex, "GOSP", ..., channel.Reader2));
var t3 = Task.Run(() => RebuildFromChannel(_tgspIndex, "TGSP", ..., channel.Reader3));

// Producer — reads GSPO once, broadcasts to all three
while (gspoEnum.MoveNext()) channel.Write(gspoEnum.Current);
channel.Complete();

Task.WaitAll(t1, t2, t3);
```

The broadcast channel avoids the three-scan duplication; each consumer sees every triple exactly once in an ordered stream. Back-pressure prevents producers from getting too far ahead of slow consumers.

Trigram indexing runs as a fourth parallel consumer, filtering for literals.

**Concurrency invariants:**
- Each target index is written by exactly one thread. Single-writer contract per [ADR-020](ADR-020-atomstore-single-writer-contract.md) is preserved (per index, not per store).
- `AtomStore` is read-only during rebuild — no new atoms created. The `AcquirePointer` model Just Works for concurrent readers.
- `QuadStore._lock` is held for the duration of the rebuild (write lock), so no external queries run concurrently. Internal coordination uses channels, not shared mutable state.

### 3 — Measurement infrastructure — standing, not ad-hoc

Mercury today has `LoadProgress` for bulk-load callbacks. Nothing equivalent for queries or rebuild phases. Add:

- **`QueryMetrics`** — captured at `SparqlEngine.Query` boundaries. Records parse time, plan time, execution time, rows returned, index paths taken. Logged via pluggable listener (defaults to no-op).
- **`RebuildMetrics`** — per-index and total timing, entries processed, splits, page-allocations. Per-phase breakdown (scan time, sort time, append time for the sort-insert path).
- **Histogram support.** Latency measurements go into fixed-size reservoir samplers (HdrHistogram-style), publishable as p50/p95/p99.
- **JSONL output path.** Like the existing `--metrics-out` for bulk load, but emitted on every query and every rebuild phase. Pipes into Prometheus/Grafana-friendly consumers for anyone who wants dashboards, or into a trivial `jq` for ad-hoc analysis.

This is scaffolding, not glamorous work. But without it, every future optimization is evaluated with a one-shot dotTrace session and eyeballed from the REPL. That's not a sustainable discipline.

### 4 — Scope boundary

Three things explicitly NOT in this ADR:

- **Hash function replacement.** The 1.7.16 word-wise FNV disaster was caught without a regression harness; a proper SIMD-friendly hash (xxHash64-style) requires one. That's a separate ADR once the regression-harness scaffolding exists. Tracked in the limits register: [hash-function-quality](../../limits/hash-function-quality.md).
- **Offset/ID bit-packing.** Real savings but much smaller than the schema-reduction win in [ADR-029](ADR-029-store-profiles.md). Defer until after ADR-029 is shipped and measured. Tracked in the limits register: [bit-packed-atom-ids](../../limits/bit-packed-atom-ids.md).
- **Query planner optimizations.** The planner today picks an index and scans it. Cost-based planning (use column statistics to pick joins) is potentially a large gain for multi-pattern queries but is a significant body of work. Separate ADR if taken up.

### 5 — The bulk/rebuild split is profile-invariant

_Amended 2026-04-20 after the Reference gradient (see [adr-029-reference-gradient-2026-04-20.md](../../validations/adr-029-reference-gradient-2026-04-20.md))._

The split between "bulk-load writes the primary-key index only" and "rebuild populates secondaries from the sorted primary" is not a Cognitive-specific design accident. It is the structural answer to the hard problem of writing to multiple B+Trees in different sort orders from the same loop: every triple generates random-access writes into each secondary index, and as soon as the combined working set exceeds RAM the page cache thrashes and throughput collapses. The Cognitive pipeline was built this way on purpose; the purpose is profile-independent.

The [ADR-029](ADR-029-store-profiles.md) Phase 2 implementation (1.7.29) inlined GPOS and trigram writes into the Reference bulk-load loop, contradicting this principle. The 2026-04-20 gradient measured the cost: Reference bulk rate declined from **210 K triples/sec at 1 M** to **80 K/sec at 10 M** to **31 K/sec at 100 M**, with recent rate at end of 100 M down to ~24 K/sec. Extrapolated to 21.3 B at sustained 24 K/sec, a full-Wikidata Reference bulk-load would take ~10 days — not deployable. Meanwhile Cognitive at 100 M completes bulk + rebuild in 17 m 24 s.

**Commitment.** The bulk-load path for every profile with ≥ 2 indexes writes only the primary index inline. Secondary indexes (and trigram) are populated by `RebuildSecondaryIndexes`, which reuses the Phase 2 parallel and Phase 3 sort-insert infrastructure regardless of profile. Reference gets:

- Bulk phase: sequential writes to `_gspoReference` only; `StoreIndexState` transitions to `PrimaryOnly`.
- Rebuild phase: scan GSPO, fan out to `_gposReference` and trigram; state transitions to `Ready`.

This also means Phase 2 and Phase 3 — originally scoped to Cognitive's GPOS/GOSP/TGSP rebuild — inherit Reference's GPOS + trigram rebuild for free. One code path, two profiles.

## Consequences

### Positive

- **Rebuild wall time projects to drop dramatically.** Combining sort-insert (~3-5× structural improvement) with parallel rebuild (~3× wall-clock on 3 independent secondaries) targets 1 B rebuild at **~20-30 min** (down from 3 h 7 m) and 21.3 B rebuild at **~7-10 h** (down from ~70 h). Combined bulk + rebuild for full Wikidata lands in **~1 day** — a routine operation, not a ceremony.
- **Measurement infrastructure unblocks all future optimization.** Hash function replacement, query planner work, and eventually cross-machine federation all need repeatable performance evidence. This ADR puts that foundation in place.
- **Core-utilization claim matures.** "60-70 % of cores idle" is today a dotTrace anecdote. After this ADR, it's a standing metric visible per rebuild, per query, per load.
- **Composes with [ADR-029](ADR-029-store-profiles.md) (profiles).** Reference profile has 32 B entries (vs 88 B); combined with sort-insert the storage-bandwidth win is multiplicative. Reference + sort-insert + parallel ≈ ingest throughput that might actually match the source file read bandwidth (the true lower bound).
- **Composes with [ADR-028](ADR-028-atomstore-rehash-on-grow.md) (rehash).** Rehash-on-grow runs during bulk load, outside the rebuild path. Orthogonal, both wins stack.

### Negative

- **Sort-insert is a new code path with new invariants.** `AppendSorted` is fundamentally trust-based — feeding unsorted data corrupts the tree. DEBUG-mode assertions help; RELEASE is UB. This requires clear documentation and guarded entry points.
- **Parallel rebuild coordination is non-trivial.** Broadcast channels, back-pressure, thread-safe progress reporting. All solvable but all new code. Bugs in parallel coordination are famously hard to reproduce.
- **In-memory sort for chunked rebuild requires memory.** Sorting 100 M 88 B entries = 8.8 GB transient allocation. Not a problem on the M5 Max (128 GB) but Mercury's zero-GC ethos expects us to reuse buffers, not rent large transient arrays. Design needs to account for this — probably a dedicated sort buffer pool.
- **Measurement infrastructure has a maintenance cost.** New schemas, new output formats, new serialization paths. Worth it, but not free.

### Risks

- **Parallel rebuild correctness.** A race-condition bug in the broadcast channel or consumer ordering could produce subtly wrong secondary indexes — no crash, just missing or duplicated entries. Mitigation: exhaustive per-profile rebuild tests comparing parallel output to sequential baseline, on every CI run.
- **Sort-insert leaf-page invariants.** B+Tree sort-insert must maintain the split invariant (leaves never exceed N entries, internal nodes point at valid children). Bottom-up construction after a sorted run is a well-known algorithm but requires careful implementation. Mitigation: invariant-checking pass at the end of the sort-insert run, in DEBUG builds.
- **Measurement overhead becoming load-bearing.** If query metrics capture adds measurable latency (say, 5-10 %), the "measurement infrastructure" becomes the bottleneck. Mitigation: listener pattern with an explicit no-op default; metric capture is opt-in and skips entirely when no listener is attached.

## Implementation plan

**Phase 1 — Measurement infrastructure (foundation)**
- `QueryMetrics` struct + pluggable `IQueryMetricsListener` + no-op default.
- `RebuildMetrics` struct + per-phase timers in `RebuildSecondaryIndexes`.
- JSONL output path reusing the `--metrics-out` machinery.
- Tests: listener fires correctly, no-op default has zero overhead, JSONL round-trips.

**Phase 2 — Parallel rebuild**
- Broadcast channel implementation (BCL only — `System.Threading.Channels` with a small wrapper).
- `RebuildSecondaryIndexes` refactor to fan-out/join pattern.
- Tests: output equivalence to sequential rebuild for every profile; concurrency fuzz with random stall injection.
- Baseline measurement: 100 M rebuild at sequential vs parallel (expected ~3× on M5 Max).

**Phase 3 — Sort-insert fast path**
- `QuadIndex.AppendSorted` + contract documentation.
- In-memory chunked-sort path in `RebuildSecondaryIndexes` for GPOS/GOSP.
- TGSP keeps random-insert (dimensional sort mismatch). Or evaluate whether a distinct TGSP-sort pattern helps.
- Tests: sort-insert output matches regular insert for all profiles; DEBUG-mode unsorted-input assertion fires as expected.
- Baseline measurement: 100 M rebuild at sort-insert vs random-insert.
- **Refactor Reference bulk-load (Decision 5 follow-through).** `QuadStore.AddBatched` / `AddCurrentBatched` / `CommitBatch` stop writing to `_gposReference` and trigram inline; bulk phase writes only `_gspoReference`, state transitions to `PrimaryOnly` (skip currently guarded on `_schema.HasTemporal` — remove that guard). Extend `RebuildSecondaryIndexes` to the Reference profile: scan GSPO, populate GPOS and trigram via the same parallel + sort-insert machinery as Cognitive. Post-rebuild state is `Ready`. Expected throughput: GSPO-only bulk matches Cognitive's rate (~300 K+/sec), and Reference's single-secondary rebuild is far smaller than Cognitive's four, so total time-to-Ready lands below Cognitive at every scale.

**Phase 4 — Full Wikidata Reference profile run with all optimizations**
- Combine ADR-029 Reference profile + ADR-030 optimizations.
- Load 21.3 B triples into a Reference-profile store.
- Target: combined bulk + rebuild completes in < 24 hours.
- Publish the benchmark.

**Phase 5 — ADR update and retrospective**
- Move status Proposed → Accepted after Phase 2 passes correctness tests.
- Status → Completed after Phase 4.
- Document any emergent architectural issues for follow-up.

## Open questions

- **What's the chunk size for in-memory sort?** 100 M chunks × 88 B = 8.8 GB. 50 M chunks = 4.4 GB. 200 M = 17.6 GB. Probably tune against the target host's available RAM at runtime.
- **Should `AppendSorted` reject unsorted input at all, or only assert in DEBUG?** Release-build validation is cheap (compare previous key) but adds a branch per entry. Worth measuring the cost; default is probably DEBUG-only until proven needed.
- **Is there a parallel version of the bulk load itself?** Bulk load is single-threaded parser → single-threaded insert. Parallelizing would require parallel parsing (hard — N-Triples is line-oriented but state-per-line is minimal; Turtle is more entangled) or parallel primary-index insert (hard — GSPO is one file). Out of scope for this ADR, flagged here as a follow-up.
- **Does the Reference profile need a separate `AppendSorted` for 32 B entries?** Yes; ADR-029's `ReferenceQuadIndex` needs its own sort-insert path. Part of Phase 3 implementation.

## References

- [ADR-020](ADR-020-atomstore-single-writer-contract.md) — single-writer contract preserved per-index
- [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) — Wikidata pipeline and current measurements
- [ADR-028](ADR-028-atomstore-rehash-on-grow.md) — orthogonal, composes
- [ADR-029](ADR-029-store-profiles.md) — orthogonal, composes; Reference profile multiplies wins
- [bulk-load-gradient-2026-04-17.md](../../validations/bulk-load-gradient-2026-04-17.md) — bulk load optimization history (57 K → 331 K triples/sec)
- [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md) — rebuild baseline measurements
