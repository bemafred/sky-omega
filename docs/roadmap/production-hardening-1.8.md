# Production Hardening Roadmap — Sky Omega 1.8.0

**Status:** Drafted 2026-04-20. Amended 2026-04-26 after Phase 6 (21.3 B Wikidata) validated end-to-end. Amended 2026-04-29 after Phase 7b (ADR-036 bzip2 substrate) shipped in 1.7.45 and the WDBench cold baseline against `wiki-21b-ref` surfaced two distinct executor/parser issues (one fixed in `527016f`, one characterized in `744edf7`). Amended 2026-04-30 after the property-path hardening arc closed all surfaced flaws: 1.7.46 cancellation token coverage (12 sites), 1.7.47 parser refactor + zero-GC walker + Case 2 binding fix, ADR-006 (MCP surface discipline) and ADR-007 (sealed substrate immutability) shipped Proposed. WDBench cold baseline 1.7.47 sealed as the disclosure-marked Phase 7c starting point. Sequences ADR-028, ADR-029, ADR-030, ADR-031, ADR-032, ADR-033, ADR-036, ADR-006, ADR-007, the Phase 7 performance rounds (`docs/limits/`), and the DrHook engine — all within 1.7.x — toward 1.8.0 as the cognitive-layers entry point.

## Version-line model (amended 2026-04-26)

The 2026-04-20 draft framed 1.8.0 as "Mercury production-hardening complete." That framing has shifted: **1.8.0 is now the boundary between substrate work and cognitive work**, not the boundary between production-hardening and everything-else.

```
1.7.x development
  ├── Phases 1–6: Production hardening (ADR-028/029/030/031/032/033) ✅ complete 2026-04-26
  ├── Phase 7:    Performance rounds (limits register — bz2 streaming, metrics, sorted atom store, …)
  └── Phase 8:    DrHook engine (BCL-only replacement of netcoredbg + Microsoft.Diagnostics.NETCore.Client)

1.8.0 release marker
  └── Cognitive layers begin (Lucy / James / Mira / Sky)
```

DrHook is "the very last of 1.7, possibly" — possibly because the engineering may prove larger than expected, in which case the 1.8.0 boundary may move. The current working assumption is: substrate work (Mercury hardened + Phase 7 rounds + DrHook engine BCL-only) ends in 1.7; cognitive layers begin at 1.8.0.

This roadmap remains canonically `production-hardening-1.8.md` because the document still tracks the work *toward* 1.8.0 — even though 1.8.0's meaning has shifted from "production-hardening release" to "substrate-complete, cognitive entry point."

## Progress (updated 2026-04-30)

| # | Phase | Target versions | Status | Evidence |
|---|---|---|---|---|
| 1 | ADR-028 rehash-on-grow | 1.7.24–1.7.26 | ✅ Shipped 1.7.24 | `docs/validations/adr-028-rehash-gradient-2026-04-20.md` — 1 M / 10 M / 100 M exact-match to pre-rehash baseline. |
| 2 | ADR-029 Reference profile | 1.7.27–1.7.30 | ✅ Functionally complete 1.7.30 | `docs/validations/adr-029-reference-gradient-2026-04-20.md` — 5× B+Tree index reduction at 100 M confirmed. ADR-030 Decision 5 amended. |
| 3 | ADR-030 Phase 1 measurement infrastructure | 1.7.31 | ✅ Shipped 1.7.31 | `QueryMetrics` / `RebuildMetrics` listeners + `JsonlMetricsListener` + CLI `--metrics-out` integration. |
| 4 | ADR-031 Pieces 1+2 | 1.7.32–1.7.33 | ✅ Shipped 1.7.32, validated 1.7.33 | `docs/validations/adr-031-dispose-gate-2026-04-21.md` — 1 B Cognitive Dispose 14 min → 0.84 s. |
| 5a | ADR-030 Phase 2 parallel rebuild | 1.7.36 | ⚠️ Shipped, **reverted** in 1.7.38 | `docs/validations/adr-030-phase2-parallel-rebuild-2026-04-21.md` — wall-clock-neutral at 100 M; Phase 5.2 trace exposed hidden cost (453 s GC + 552 s lock-acquire-slowpath that didn't exist in sequential baseline). |
| 5b | ADR-030 Phase 3 sort-insert | 1.7.37 | ⚠️ Shipped, **reverted** in 1.7.38 | `docs/validations/adr-030-phase3-sort-insert-2026-04-21.md` — concept right (eliminate write amplification) but `Array.Sort` with comparator + 3.2 GB monolithic buffer cost as much as it saved. |
| 5.2 | Phase 5.2 dotnet-trace + iostat (architectural pivot) | 1.7.38 | ✅ Pivot complete 1.7.38 | `docs/validations/adr-030-phase52-trace-2026-04-21.md` — identified write amplification (~3× useful I/O) as the binding bottleneck, ruled out CPU and bandwidth. Drove the revert + the radix architecture in ADR-032/033. |
| 5c | ADR-032 Phases 1-4 (radix external sort for rebuild) | 1.7.39–1.7.42 | ✅ Shipped + validated 1.7.42 | Phase 1: `RadixSort` primitive (LSD, 8-bit digits, signed-long bias). Phase 2: `ExternalSorter<T,TSorter>` (chunked spill + k-way merge). Phase 3: `docs/validations/adr-032-phase3-gpos-radix-2026-04-22.md` — GPOS rebuild 3× faster, peak 2463 MB/s. Phase 4: `docs/validations/adr-032-phase4-trigram-radix-2026-04-22.md` — **10.5× total rebuild speedup at 100 M**. |
| 5d | ADR-033 (radix external sort for bulk-load) | 1.7.43 | ✅ Shipped + validated 1.7.43 | `docs/validations/adr-033-phase5-bulk-radix-2026-04-22.md` — 1 B end-to-end ~3h57m → **60m36s** (3.92× combined speedup). |
| 5e | Phase 6 — 21.3 B Wikidata Reference end-to-end | 1.7.44 | ✅ Shipped 2026-04-25 | `aa35514` bumped Reference index BulkMode floor 256 GB → 1 TB. Launched 2026-04-22; sealed 2026-04-25 22:32 at 85 h end-to-end. |
| 5f | Query-side validation against wiki-21b-ref | 1.7.44 | ✅ Validated 2026-04-26 | `docs/validations/21b-query-validation-2026-04-26.md` — both GSPO and GPOS indexes return correct results at 21.3 B; cold-cache `LIMIT 10` queries in tens of milliseconds. Capacity dimension of production hardening is empirical, not estimated. |
| 6 | Production hardening milestone (close-out) | 1.7.x (no bump) | ⏭ Pending | ADR status transitions, STATISTICS update, milestone doc. WDBench latencies *moved to Phase 7c* (see line below). Production hardening *milestone* — not a release. 1.8.0 is reserved for cognitive layers entry. |
| 7a | Phase 7a — metrics infrastructure maturation | 1.7.x | ⏭ Pending | Eight observability gap categories from `docs/limits/metrics-coverage-review.md`. Required before any Phase 7c round merges (no perf round without the metric to demonstrate the win). |
| 7b | Phase 7b — bzip2 streaming source decompression (ADR-036) | 1.7.45 | ✅ Substrate complete 2026-04-26 | `7bba720` ADR-036 Phase 7b — bzip2 substrate complete. `8e0c688` packed BWT + tight MTF shift. End-to-end `--bulk-load latest-all.ttl.bz2 --limit N` runs without uncompressed staging; full gradient validation at 1 M / 10 M / 100 M / 1 B is still pending (Phase 7c sequencing input). |
| 7c-baseline | WDBench cold baseline against `wiki-21b-ref` | 1.7.47 | ✅ Sealed 2026-04-30 | Final disclosure-marked baseline at `docs/validations/wdbench-paths-21b-2026-04-29-1747.jsonl` + `wdbench-c2rpqs-21b-2026-04-29-1747.jsonl`. 1,199 queries, **0 parser failures**, p50=45ms, p95=29.85s, p99=49.50s, max=59.82s; cancellation contract honored at scale (every one of 655 timeouts closed 60.000s–63.620s). The 1.7.46 cancellation fix (commits `527016f` + `963340c`) closed 12 cancellation-token gaps across `Operators/TriplePatternScan.cs`, `SlotBasedOperators.cs`, `MultiPatternScan.cs`, `QueryResults.Patterns.cs`. The 1.7.47 parser refactor (commit `1be7a4d`) + walker rewrite + Case 2 binding fix closed the 12 grammar-gap shapes (Shape 1 `^(P){q}`, Shape 2 `^((A\|B)){q}`, Shape 3 `(^A/B)`) plus a latent silent-zero-row failure mode for object-bound transitive paths. Limits-register entries `cancellable-executor-paths.md` and `property-path-grammar-gaps.md` move Triggered → Resolved. |
| 7c-governance | ADR-006 + ADR-007 | 1.7.47 | ✅ Shipped 2026-04-30 | ADR-006 MCP Surface Discipline + ADR-007 Sealed Substrate Immutability landed Proposed (commit `432f613`). MCP `mercury_prune` removed; PruneEngine rejects Reference profile at plan time with bulk-load re-creation guidance. Operationalizes the governed-automation thesis at concrete decision points. |
| 7c-rounds | Phase 7c — measured perf rounds (limits register) | 1.7.x | ⏭ Pending | Sequence determined by post-Phase-6 trace at 1 B + WDBench cold-baseline distribution. Each round ships with captured JSONL pre/post artifact. |
| 8 | DrHook engine (BCL-only) | 1.7.x | ⏭ Pending | The very last of 1.7, possibly. |
| — | Release 1.8.0 — cognitive layers begin | 1.8.0 | ⏭ Future | After Phase 8 lands. Entry to a different roadmap. |

Phases 5c-5d (the radix architecture) took roughly two days of focused work (2026-04-21 → 2026-04-23) after the Phase 5.2 pivot exposed the architectural mistake in Phases 5a+5b. The discipline of **measuring before claiming, reverting when the evidence demands, and documenting limits as they surface** is what turned a wall-clock-neutral failure into a 10.5× rebuild speedup and a 3.92× combined speedup at 1 B.

The Phase 6 run is the longest-tail validation: ~65-72 hours wall-clock on consumer hardware (M5 Max, 128 GB RAM), validating the architecture at the full Wikidata target size — past the Blazegraph WDQS scaling ceiling (~12-13 B triples) where incumbent RDF infrastructure has historically given up.

## Purpose

Four architectural ADRs (028, 029, 030, 031) emerged from the 2026-04-17 through 2026-04-19 Wikidata-scale validation work. Each was forced by a specific hard finding. All four are in Proposed status with Martin's working-assumption that they are viable. They compose but are orthogonal — any one can be accepted or deferred independently.

This roadmap sequences the four ADRs into a production-hardening effort leading to **1.8.0**, at which point Mercury is considered solid enough for the two next tracks: **DrHook** (runtime observation substrate) and **cognitive layers** (Lucy/James/Sky).

The framing matters: 1.8 is not "Mercury done." It is "Mercury load-bearing enough for the work above it to begin." Every decision in this roadmap is evaluated against that bar.

## Scope

**In scope:**
- ADR-028 — AtomStore rehash-on-grow (capacity ceiling)
- ADR-029 — Store profiles including Decision 7 Reference mutation semantics (storage economy)
- ADR-030 — Bulk load + rebuild performance architecture (time)
- ADR-031 — Read-only session fast path (ACID overhead on read-dominant workloads)
- Full-Wikidata Reference profile validation as the exit criterion for 1.8.0

**Explicitly out of scope:**
- DrHook.Engine (BCL-only runtime inspection) — queued, post-1.8
- Cognitive layers (Lucy semantic memory, James orchestration, Sky agent) — post-1.8
- Query planner optimization (cost-based joins, statistics) — separate ADR if taken up
- Cross-process read-only sharing — noted in ADR-031, deferred
- Offset / ID bit-packing — small win, deferred in ADR-029 Decision 5
- Hash function replacement (xxHash-style) — separate ADR after ADR-030's measurement infrastructure exists
- Any feature work that does not serve one of the four ADR decisions

**Scope discipline is the main risk.** Production hardening has a way of becoming "clean up every known thing at once." This roadmap resists that — each phase has a narrow objective, and widening it requires a new ADR.

## Dependency graph

```
ADR-028 (capacity)
  │
  ├────► every subsequent full-scale validation
  │      (nothing loads past ~58 M triples without rehash)
  │
  └────► ADR-029 (profiles)
           │
           ├────► ADR-031 Piece 1 (declared read-only, depends on Decision 7)
           │
           └────► ADR-030 Phase 3 (Reference-profile sort-insert variant)

ADR-030 Phase 1 (measurement infrastructure)  ── independent
ADR-031 Piece 2 (mutation-tracked Dispose)    ── independent  
ADR-031 Piece 3 (optimistic read-open)        ── independent, HARD
```

**Hard constraints:**
- ADR-028 must land first. Nothing validates at scale without it.
- ADR-029 must land before ADR-031 Piece 1 and before ADR-030 Phase 3.
- ADR-030's measurement infrastructure (Phase 1) is technically independent but should ship early — it's the prerequisite for defensible perf claims throughout the rest of the roadmap.
- ADR-031 Piece 3 is optional. If it derails other work, it can defer to a follow-up ADR (031b) without blocking 1.8.

## Phased plan

Six phases, each with an explicit exit criterion. Version numbers below are targets; actual bumps happen per convention (one per substantive change).

### Phase 1 — Capacity foundation (ADR-028)

**Target versions:** 1.7.24–1.7.26
**Objective:** Remove the 256 M-atom-bucket ceiling as a blocker for all downstream work.

Implement dynamic rehash-on-grow in the AtomStore: when bucket load factor crosses 75 %, allocate a new hash table at 2× size, re-insert all atoms via stored per-bucket hashes, swap the file atomically. Preserve the ADR-020 single-writer contract during rehash; readers using `AcquirePointer` must continue to see a coherent view throughout.

**Exit criterion:**
- All existing tests green.
- Dedicated rehash correctness tests: concurrent read-while-rehash equivalence; rehash-during-bulk-load crash recovery.
- 100 M Wikidata bulk-load completes cleanly past the prior 58 M ceiling — proves the fix works at a scale that previously crashed.

**Why first:** Without ADR-028, no other ADR can be validated at full scale. Every other phase's exit criterion requires loading past the 256 M ceiling.

### Phase 2 — Storage economy (ADR-029)

**Target versions:** 1.7.27–1.7.30
**Objective:** Make 21.3 B Wikidata fit on commodity 8 TB hardware via the Reference profile; lock in mutation semantics via Decision 7.

Implement `StoreProfile` enum, `store-schema.json` durable metadata, and `ReferenceQuadIndex` as a parallel concrete type to `TemporalQuadIndex` (not generics — see ADR-029 Decision 3). Extend CLI: `mercury --create-store --profile <name>` and profile-aware `--bulk-load`. Plan-time rejection of capability-mismatched queries (temporal against Reference) and session-API mutations against Reference (Decision 7).

**Exit criterion:**
- 21.3 B `latest-all.nt` loaded cleanly into a Reference profile store within 24 hours.
- Store size within the ~2.6 TB projection (2 indexes × ~1.1 TB + atoms + trigram).
- Cross-profile migration tests: profile mismatch on open is a hard error; reload from source produces the correct profile.
- Cognitive regression unchanged: all current tests pass, `TemporalQuadIndex` is the current `QuadIndex` behaviorally.
- Decision 7 enforcement: session-API mutation against Reference fails at plan time with a clear error.

**Why second:** ADR-029 is the load-bearing storage-economy claim. Without it, Mercury disqualifies itself from the Wikidata backend conversation on commodity hardware, and the whole hardening effort's external value shrinks.

**Open question from Decision 7 that must be resolved during this phase:** bulk-append dedup policy (silent dedup vs error on conflict). Answer before Reference profile ships.

### Phase 3 — Measurement infrastructure (ADR-030 Phase 1 only)

**Target version:** 1.7.31
**Objective:** Stand up pluggable metrics (`QueryMetrics`, `RebuildMetrics`, histogram reservoirs, JSONL output path) so every subsequent perf claim has a repeatable evidence base.

Pull ADR-030 Phase 1 forward from its position in the ADR's own phased plan. The measurement work is not glamorous, but it is the prerequisite for defensible perf claims in phases 4 and 5 — without it, "14 min Dispose → seconds" and "rebuild 70 h → 20 h" remain anecdotal.

**Exit criterion:**
- `QueryMetrics` struct + `IQueryMetricsListener` + no-op default with zero overhead when no listener attached.
- `RebuildMetrics` per-index and per-phase timing wired into `RebuildSecondaryIndexes`.
- JSONL output path reusing `--metrics-out`, one record per query or rebuild phase.
- Listener-attachment overhead under 1 % on a synthetic query-heavy workload.
- At least one histogram dashboard consumer (jq one-liner or Prometheus config) demonstrated on captured metrics.

**Why here and not later:** Every perf claim from phase 4 onward is measured using this infrastructure. Shipping it after the work it measures creates a credibility gap.

### Phase 4 — Read-only UX wins (ADR-031 Pieces 1 and 2)

**Target versions:** 1.7.32–1.7.33
**Objective:** Collapse Dispose time for read-only sessions from ~14 min (measured at 1 B) to essentially zero; ship Reference profile's structural read-only fast path.

**Mechanism update (from the 2026-04-20 Dispose profile):** The 14 min is not msync as originally assumed in ADR-031's draft — it is `CollectPredicateStatistics` called from `CheckpointInternal()` during `Dispose()`. See [dispose-profile-2026-04-20.md](../validations/dispose-profile-2026-04-20.md). The fix is a one-line gate (`if (_sessionMutated) CheckpointInternal();`) rather than any msync-skip machinery.

Piece 1: `QuadStore.Open` branches on profile; Reference profile skips writer lock, opens mmap for read-only, skips WAL recovery, unconditionally skips `CheckpointInternal` on Dispose. Follows directly from ADR-029 Decision 7 (Reference has no WAL, no statistics maintenance).

Piece 2: Add `volatile bool _sessionMutated` flag. Every mutation path (AtomStore writes, QuadIndex adds/removes, WAL appends, WAL replay on Open, trigram updates) sets it. `Dispose` gates `CheckpointInternal` on the flag. No API change; inference is correct-by-construction for mutable profiles.

**Exit criterion:**
- 1 B cognitive store predicate-bound COUNT: total wall time (open + query + Dispose) under 60 s, Dispose phase under 1 s.
- Reference profile 21.3 B query: open + query + Dispose under 2 minutes total.
- Flag-enumeration test: every public mutation API flips the flag; every pure-query session leaves it false.
- Behavior after a mutating session unchanged: `CheckpointInternal` still runs, `_statistics` still gets updated for the next Open, WAL checkpoint marker still written.

**Why before phase 5:** Pieces 1 and 2 are days of work with large user-facing impact (queries stop taking 14 min to close). ADR-030's parallel rebuild is weeks of harder engineering with impact mostly visible to operators running full reloads. Ship the user-facing wins first; both user and maintainer see the system become immediately more usable.

### Phase 5 — Bulk and rebuild performance (ADR-030 Phases 2–4)

**Target versions:** 1.7.34–1.7.38
**Objective:** Close the bulk/rebuild time gap at 21.3 B so full-Wikidata is a routine operation rather than a ceremony.

Phase 2 (from ADR-030): Parallel rebuild across GPOS/GOSP/TGSP/Trigram via a broadcast channel feeding one consumer per target. Single GSPO scan shared; back-pressure prevents producer from out-running slowest consumer. Targets 3× wall-clock reduction on the M5 Max.

Phase 3 (from ADR-030): Sort-insert fast path `QuadIndex.AppendSorted` + chunked in-memory sort for GPOS/GOSP (different dimension order than GSPO). TGSP keeps random-insert (dimensional sort mismatch on secondary only).

Phase 4 (from ADR-030): Full 21.3 B Reference profile run with all optimizations composed. This IS the 1.8 exit event.

**Exit criterion:**
- 100 M rebuild: sequential ≈ parallel output equivalence (byte-identical secondary indexes).
- 1 B rebuild wall time: target under 30 minutes (down from 3 h 7 m measured 2026-04-19).
- 21.3 B Reference profile full pipeline (bulk + rebuild): under 24 hours total.
- Parallel rebuild correctness fuzz tests: stall-injection, out-of-order consumer stalls, producer-faster-than-consumer — all equivalence-check green.

**Why last among the core four:** Substantial engineering, substantial correctness risk (parallel coordination bugs), substantial validation wall-clock cost per iteration. Do it after the foundation (028, 029) is solid and the UX wins (031 piece 2) are banked.

#### Phase 5 methodology — gradient before scale, profile before commit

Added 2026-04-21. Every optimization landing in Phase 5 goes through the same discipline that surfaced ten bug classes across Phases 1-4 and took bulk-load from 57 K → 331 K triples/sec across 1.7.13 → 1.7.22.

**5.1 Gradient before scale.** Each architectural optimization is exercised through the 1 M / 10 M / 100 M gradient against the captured pre-change baseline before the next optimization lands on top of it. Metrics are captured through the ADR-030 Phase 1 `--metrics-out` JSONL path so every claim has an evidence file, not a recollection. The sequence:

1. Parallel rebuild lands → gradient rebuild vs sequential baseline at 1 M / 10 M / 100 M → confirm byte-identical secondary-index output at every scale, measure wall-clock win.
2. Sort-insert fast path lands → gradient rebuild vs random-insert baseline → same correctness + perf check.
3. Reference bulk refactor lands (Decision 5 follow-through) → gradient bulk throughput vs the inline-writes baseline (the 210 K → 31 K triples/sec collapse captured 2026-04-20) → confirm the refactor closes the gap.
4. All three composed → 1 B Cognitive + Reference dry run on `wiki-1b` and a fresh 1 B Reference slice → confirm the combined optimizations hold at the largest non-toy scale before the 21.3 B commitment.

Budget: ~10-20 gradient runs (each 1 M / 10 M / 100 M is ~30-60 min; each 1 B is ~3-4 h after the optimizations) spread across the three architectural changes = roughly 40-80 h wall-clock before the 21.3 B run. Keeps all intermediate runs recoverable — if any one step regresses, we roll it back and iterate without having wasted a full-scale run.

**5.2 dotTrace-driven micro-optimization pass.** After 5.1's architectural wins are banked, profile the composed system with dotTrace against a 100 M bulk + rebuild. The architectural changes will have re-shuffled the hot paths — ADR-030's call-out of "60-70 % of cores idle at bulk-load peak" is the pre-parallel baseline; after parallel rebuild the hot path moves. That's the invariant of high-level optimization: it changes what's slow, so the cycle repeats.

Pattern mirrors the 1.7.13 → 1.7.22 arc:

- dotTrace sampling pass identifies top-N hot methods
- Each candidate micro-optimization: implement → 1 M / 10 M / 100 M gradient → captured JSONL metrics proving the win → commit
- Refuse any micro-opt that can't be measured (hash-function replacement already queued in `docs/limits/hash-function-quality.md` for exactly this reason)

Examples the 1.7.x arc shipped this way: atom-ID routing kills the `GetAtomString` round-trip (1.7.14), `UtcNow` caching in AddBatched (1.7.14), fstat elimination in `EnsureDataCapacity` (1.7.14), `SaveMetadata` msync deferral (1.7.15, 1.7.22). Each was a percent-level win individually; in aggregate they were the 4.7× throughput gain.

Budget: 5-10 iterations post-5.1, each measured, each gradient-verified. ~20-40 h additional wall-clock.

**5.3 Full 21.3 B commitment.** Only after 5.1 + 5.2 are clean through the 1 B dry run. One `mercury --store wiki-ref --profile Reference --bulk-load latest-all.nt` invocation with `--metrics-out` capturing the JSONL record stream. Validation doc at `docs/validations/21.3b-reference-first-landing-<date>.md`. If it fails mid-run, we have 5.1/5.2 numbers to compare against to localize the regression — unlike a blind 21.3 B attempt where failure is unattributable.

**Why structural, not implicit.** The gradient discipline caught every bug class in Phases 1-4 by design: a 1 M run has a 6-order-of-magnitude lower cost of iteration than a 21.3 B run, and every failure mode we've hit in production is latent at some smaller scale if you look. Not gradient-testing pre-21.3B is equivalent to betting a ~24 h wall-clock run (plus cleanup + retry) on unverified code.

### Phase 6 — Production hardening milestone (close-out)

**Target version:** 1.7.x (no version bump — this is a milestone, not a release)
**Objective:** Close out the four production-hardening ADRs cleanly. Capture the substantive measurements (WDBench-style query latencies) that the Phase 6 validation deferred. Move the production-hardening arc from "live work" to "documented milestone" so Phase 7 starts with a clean substrate.

**Why Phase 6 is no longer a release.** The 2026-04-20 draft expected Phase 6 to bump to 1.8.0. Under the amended version-line model (see top of doc), 1.8.0 is reserved for the cognitive-layers entry point, which arrives after Phase 8 (DrHook engine). Phase 6's ADR closures, milestone doc, and STATISTICS update happen on `main` as ordinary 1.7.x work, without a major-version bump.

**ADR-030 status reconciliation.** ADR-030 Phases 2 (parallel rebuild, 1.7.36) and 3 (sort-insert, 1.7.37) shipped, then **reverted in 1.7.38** after the Phase 5.2 dotnet-trace + iostat exposed the binding bottleneck as write amplification, not CPU. The replacement architecture lives in **ADR-032** (radix external sort for rebuild) and **ADR-033** (radix external sort for bulk-load), shipped + validated 1.7.39–1.7.43. ADR-030 should be marked Completed at Phase 1 (measurement infrastructure) with Phases 2-3 explicitly marked Superseded by ADR-032/033, so a future reader doesn't think the original parallel-rebuild plan is still pending.

**ADR-031 Piece 3 is explicitly deferred to 031b** based on the 2026-04-20 Dispose profile: with Piece 2 capturing the entire 14 min (CheckpointInternal, not msync), Piece 3's remaining open-side wins are in the low-seconds range and do not justify the live mmap escalation complexity in this timeline. ADR-031 closes at Pieces 1 and 2.

**Required before Phase 7 starts:**
- All four production-hardening ADRs moved Proposed → Accepted → Completed with dated status fields. ADR-031 Completed at Pieces 1 + 2 (Piece 3 deferred to 031b by ADR scope decision — not pending work). ADR-030 Completed at Phase 1; Phases 2-3 marked Superseded; Phase 4+ replaced by the ADR-032/033 architecture.
- ADR-032 and ADR-033 moved Proposed → Completed (both already validated through 1 B; 21.3 B confirms the architecture at full scale).
- Full W3C SPARQL + Turtle conformance suites green on all four profiles (Cognitive, Graph, Reference, Minimal — or justify Minimal deferral).
- Wikidata Reference profile 21.3 B live at a documented endpoint (internal sufficient). **WDBench-style query latencies are NOT in Phase 6 scope** — they belong to Phase 7, where the optimization rounds that move them get measured. Running WDBench against an unoptimized 1.7.44 publishes numbers the next round will obsolete and produces an externally-uncharitable comparison to QLever/Virtuoso, whose published numbers reflect their own shipped optimization rounds. See Phase 7c for the WDBench thread.
- `STATISTICS.md` updated with the final line counts and benchmark summary.
- Milestone document in `docs/releases/production-hardening-2026.md` summarizing the hardening arc end-to-end. (Not `1.8.0.md` — 1.8.0 is reserved for the cognitive-layers release.)

**Phase 6 exit:** Phase 7 (performance rounds) can begin. The substrate is queryable at scale, measured, and the ADR record is closed.

### Phase 7 — Performance rounds

**Target versions:** 1.7.x (incremental bumps, one per measured round)
**Objective:** Convert the seven characterized optimization rounds in `docs/limits/` from estimated impacts to measured wins. Each round: instrument, gradient-validate, ship.

**Sequencing — enabling-dependency order, not estimated-impact order:**

#### Phase 7a — Metrics infrastructure maturation (foundation)

ADR-030 Phase 1 metrics shipped at 1.7.31 cover bulk-load, rebuild, and per-query timing. Phase 7a expands this to cover the eight observability gap categories from `docs/limits/metrics-coverage-review.md` — write amplification, page-cache pressure, B+Tree split cadence, atom-store hash drift, cold-cache I/O distribution, GC pause histograms, lock-acquire slowpath, and per-predicate cardinality during scans. **Ground rule for Phase 7: no perf round merges without the relevant metric in place to demonstrate the win.** Estimates are not measurements.

Sub-deliverable: instrument both Cognitive and Reference profile paths from day one. Metrics surfaces that work for one and bolt onto the other create silent gaps.

**Exit:** every Phase 7 round can produce a JSONL artifact showing before/after on the metric it claims to improve.

#### Phase 7b — BZip2 streaming source decompression

[ADR-036](../adrs/mercury/ADR-036-bzip2-streaming-decompression.md) — substrate-level bzip2 decompression in pure C# / BCL-only inside `src/Mercury/Compression/`. SharpZipLib-in-CLI and P/Invoke-to-libbz2 alternatives were considered and rejected: both create substrate-debt and silently externalize a capability that must be intrinsic to Mercury. Establishes `latest-all.ttl.bz2` (114 GB compressed) as the canonical Phase 7 source artifact, with `--limit N` providing gradient runs at any scale from a single source. Implementation target: BWT-inverse hot path within 1.5× of `libbz2`, zero-GC steady state, optimization pass (SIMD where it applies) after correctness lands.

**Exit:** `mercury --bulk-load latest-all.ttl.bz2 --limit N` runs end-to-end without staging an uncompressed intermediate file. Gradient runs at 1 M / 10 M / 100 M / 1 B all sourced from the same `.bz2`.

#### Phase 7c onward — Measured perf rounds, ranked by trace evidence

After 7a (metrics) and 7b (cheap gradient runs from `.bz2` source), run a fresh end-to-end trace on the post-Phase-6 codebase at 1 B scale. The trace decomposes Phase 6's 85 h into measured contributions; the seven characterized rounds in `docs/limits/` get ordered by observed-impact, not by article-time projection.

Candidate rounds, in their currently-estimated impact order (subject to re-ranking by 7a/7b trace):

1. **Sorted atom store for Reference (ADR-034 candidate)** — `docs/limits/sorted-atom-store-for-reference.md`. 30-40% wall-clock projected.
2. **Bit-packed atom IDs** — `docs/limits/bit-packed-atom-ids.md`. 20-30% rebuild + bulk projected.
3. **Hardware-accelerated XxHash3** — `docs/limits/hash-function-quality.md`. 5-15% on hash hot path.
4. **Prefetch + pipelined batch intern** — Cognitive-side; 20-30% on probe cost.
5. **MPHF on sorted vocab (BBHash)** — Phase 2 of #1; query-side O(1) lookup.
6. **B+Tree mmap remap** — `docs/limits/btree-mmap-remap.md`. Unblocks > 1 TB cases.
7. **Reference read-only mmap** — `docs/limits/reference-readonly-mmap.md`. Query-time relaxed page handling for sealed stores.

Each round's exit is a captured JSONL artifact comparing pre/post on the metric it targets. No round merges as "we believe it improves X" — only as "the metric moved from Y to Z, gradient-validated at 1 M / 10 M / 100 M."

**WDBench thread.** WDBench is the recurring external comparison benchmark threaded through Phase 7c, capturing the system's externally-comparable behavior as the optimization rounds compose:

- **Cold baseline at Phase 7c start** — after 7a (metrics) and 7b (bz2 streaming) land, before any perf round ships. One well-instrumented run against `wiki-21b-ref` produces the unoptimized-baseline distribution data (median, p95, p99, tail). This is the "where we are now, externally" number — published with explicit framing that it represents the substrate before Phase 7's measured wins.
- **Rerun after each major perf round** — post-SortedAtomStore, post-prefetch, etc. Captures each round's external impact in the units the world cares about, on the same query set, against the same artifact. Each rerun is a JSONL artifact in `docs/validations/wdbench-<round>-<date>.md`.
- **Final at Phase 7 close** — the consolidated "where we ended up" comparison against QLever/Virtuoso published numbers. This is the externally-defensible Phase 7 close-out claim — comparable, distribution-aware, sourced from a sealed artifact, runnable by anyone with the same hardware.

The cadence puts external comparison where it belongs: at the end of optimization arcs, not at the start.

**Early-baseline observation (added 2026-04-29).** The first WDBench cold baseline ran 2026-04-27 against `wiki-21b-ref`, slightly ahead of the planned "after 7a + 7b" sequencing — Phase 7a metrics infrastructure is only partially shipped (ADR-030 Phase 1 metrics from 1.7.31 cover the bulk/rebuild/per-query path, but the eight observability gap categories from `docs/limits/metrics-coverage-review.md` are not yet fully wired). The early run was epistemically productive: it surfaced two issues that affect every subsequent Phase 7 round, both characterized within 48 hours.

1. **Executor cancellation gap.** Property-path inner loops did not honor the `CancellationToken`. One `c2rpqs` query consumed 4 h 51 m of wall-clock under a 60 s timeout cap, and ~547 of 660 `paths` events were silently lost when the harness blocked waiting for an executor that never unwound. Without this fix, *no* timed benchmark on this substrate measures what it claims to measure. Captured in `docs/limits/cancellable-executor-paths.md`; fixed in commit `527016f`.
2. **Property-path parser grammar gaps.** Three combinations (`^(P)*`, `^((A|B))+`, `(^A/B)`) the W3C SPARQL 1.1 conformance suite does not exercise are reachable through real-world WDBench shapes. 12 / 1,199 queries (1.0 %) parse-fail cleanly; the remaining 99 % parse and execute. Characterized via a parse-only sweep in `docs/limits/property-path-grammar-gaps.md`; commit `744edf7`.

Both were *latent under W3C conformance* and *surfaced under WDBench*, which is itself an artifact of why the external benchmark thread is in this roadmap. The clean disclosure-marked baseline rerun is in flight 2026-04-29 (output split per category: `wdbench-paths-21b-2026-04-29.jsonl` + `wdbench-c2rpqs-21b-2026-04-29.jsonl`). The 2026-04-27 file remains as the pre-cancellation-fix reference, not deleted.

**Exit from Phase 7:** all seven rounds shipped or explicitly deferred (with reason). The combined measured impact is captured in `docs/validations/phase7-rounds-summary.md`. WDBench cold baseline + per-round runs + final captured. The 21.3 B re-run (if undertaken) is a deliberate choice, not an obligation.

**Both profiles in scope.** Every Phase 7 metric and every Phase 7 round answers "and what does this mean for Cognitive?" before it's considered done. The seven characterized rounds skew Reference; Phase 7 must not let Cognitive get under-served.

### Phase 8 — DrHook engine (BCL-only)

**Target versions:** 1.7.x (the very last of 1.7, possibly)
**Objective:** Replace the netcoredbg + Microsoft.Diagnostics.NETCore.Client dependency in DrHook with a BCL-only implementation. Restores the substrate-independence ethos that the rest of Sky Omega holds — DrHook is currently the only substrate that depends on a non-BCL package, due to the historical POC trajectory documented in memory entry `project_drhook_engine_concept`.

DrHook today provides MCP-exposed runtime inspection (EventPipe + DAP-via-netcoredbg). The DAP path is the dependency: netcoredbg is an external process, and `Microsoft.Diagnostics.NETCore.Client` wraps the EventPipe protocol. Both can be replaced — EventPipe is a documented protocol that BCL types can speak directly; DAP is overkill for the inspection surface DrHook actually exposes (process attach, stack walk, breakpoint, step, var inspect). A BCL-only implementation cuts the netcoredbg subprocess and the NuGet package, restoring the same ownership model Mercury and Minerva already have.

Known limits going in: `project_drhook_eval_dead.md` — function evaluation deadlocks on macOS/ARM64 with netcoredbg. A BCL-only rewrite has the opportunity to either (a) avoid the same architectural constraint, or (b) explicitly accept the limit as fundamental rather than implementation-specific.

**Exit:**
- DrHook MCP server runs without netcoredbg or `Microsoft.Diagnostics.NETCore.Client`.
- All 13 currently-exposed MCP tools functional on the BCL-only path (or the subset that can be made functional, with the rest explicitly retired).
- The substrate-independence claim ("Sky Omega's substrates are BCL-only") becomes true across all three substrates, not just two.

**The "possibly" in "very last of 1.7, possibly":** this work is unscoped at the time of this amendment. If it proves substantially larger than expected (multi-month), the 1.8.0 boundary may move — DrHook engine could become its own release line, with cognitive layers shifting to 1.9.0. The version-line model in this doc is the working assumption, not a commitment.

### Release 1.8.0 — cognitive layers entry point

After Phase 8 lands, the substrate work is complete: Mercury production-hardened + measured + queryable at scale; Minerva BCL-only inference substrate; DrHook BCL-only runtime inspection. **1.8.0 is the boundary** — at this point the work shifts from substrates to the cognitive layers built on top of them (Lucy / James / Mira / Sky).

This roadmap does not plan the cognitive-layers track. A separate roadmap document covers that work when it begins.

## Version strategy

**During Phases 1-8:** stay on 1.7.n. Each substantive merge to `main` bumps the patch. This is already the project's pattern (see `Directory.Build.props` and recent commit history). Don't prematurely bump to 1.8; leave that as an earned milestone.

**Bump to 1.8.0** only when Phase 8 (DrHook engine) lands and the substrate work is complete. **1.8.0 means "Mercury hardened + Phase 7 perf rounds delivered + DrHook BCL-only — substrates ready, cognitive layers can begin."** Under the amended version-line model (top of doc), 1.8.0 is no longer the production-hardening release — that milestone closes within 1.7.x as Phase 6.

**Branch strategy:** proposed all-on-`main` with frequent small commits, per the existing project convention. If a phase's work needs a longer-lived branch (Phase 8 DrHook engine is a likely candidate given its scope uncertainty), use a topic branch named `hardening/<phase>` and merge when green.

## Risks and mitigations

### Risk 1 — Full-scale validation wall-clock cost

Every 21.3 B validation run takes ~20 hours today (projected at current Cognitive throughput; Reference profile expected to be faster). This roadmap requires roughly one full run per phase (Phase 2 Reference validation, Phase 5 full-pipeline). Plus unplanned re-runs when bugs surface. Budget: **5–10 full-scale runs over the whole effort = 100–200 hours of wall-clock**.

**Mitigation:**
- Run full-scale tests overnight / weekends when possible.
- Use the 100 M gradient point for fast-iteration debugging; full-scale only for final validation per phase.
- Keep `latest-all.nt` on disk (already committed to in the Step 2/3 cleanup) so re-runs don't require re-download.

### Risk 2 — ADR-028 concurrent-reader correctness

Rehash-on-grow is the subtlest correctness question across all four ADRs. Rehash must not break readers holding `AcquirePointer` handles or produce a moment where a reader sees a partially-rehashed table.

**Mitigation:**
- Leverage the existing `AcquirePointer` invalidation model (already proven for posting-file growth).
- Stress-test with high-concurrency readers during rehash; require output equivalence to a non-rehashing baseline.
- Invariant-checking pass: post-rehash, assert every atom resolves from both old and new IDs during the transition window.

### Risk 3 — ADR-030 parallel-rebuild correctness

Broadcast-channel race bugs are famously hard to reproduce. A race that loses or duplicates a secondary-index entry produces a subtly wrong store — no crash, just missing or duplicated data visible only in query results.

**Mitigation:**
- Equivalence testing on every CI run: rebuild-parallel output must be byte-identical to rebuild-sequential output on a fixed 100 M fixture.
- Stall-injection fuzz testing: randomly slow down one consumer to surface back-pressure bugs.
- Consumer ordering invariant: each consumer sees triples in the same order the producer emits.

### Risk 4 — Scope creep into unrelated features

Production-hardening efforts attract "while we're in there" additions (query planner, new SPARQL features, etc.). Every such addition extends the timeline and dilutes the release marker's meaning.

**Mitigation:**
- Treat this roadmap as the scope contract. Additions require either a new ADR or explicit approval to extend the four in-scope ADRs.
- `docs/releases/1.8.0.md` at release time must list only work tied to ADR-028/029/030/031 or directly-forced dependencies.

### Risk 5 — Profile migration interrupting ongoing work

ADR-029 implementation requires reloading Cognitive stores as Reference. The currently-validated `wiki-1b` store (648 GB) was built with profile-unaware Mercury. Any ADR-028 validation work that reloads `wiki-1b` must account for Phase 2's later requirement to build a Reference equivalent.

**Mitigation:**
- Sequence ADR-028 validation so it doesn't require rebuilding `wiki-1b` unless strictly necessary. Phase 1's exit criterion is 100 M past the 58 M ceiling — we can validate on a 100 M store without touching 1 B.
- Budget for one Reference profile 21.3 B load during Phase 2 exit validation, not multiple.

### Risk 6 — Measurement infrastructure drift

If Phase 3's metrics land but subsequent phases don't discipline themselves to use them, perf claims regress to anecdote.

**Mitigation:**
- After Phase 3, every perf claim in phases 4 and 5 requires a captured JSONL artifact showing the measurement. No handwaving.
- Include metrics output in release notes for traceability.

## Explicit non-goals for production hardening (Phase 6)

Listed here so the "no" is as visible as the "yes". These are non-goals for *production hardening close-out* — the 2026-04-20 list, with annotations on which items have moved:

- **We are not shipping a full query planner overhaul.** Cost-based join ordering, column statistics, cardinality estimation — all deferred. Today's index-pick-and-scan planner is adequate. *(Still out of scope through 1.7.x; revisit at 1.8.0+.)*
- **We are not shipping the Graph profile unless a use case surfaces.** ADR-029 proposes it; it may collapse into Reference or Cognitive. *(Open question stays open.)*
- **We are not shipping cross-process read-only sharing.** Multi-reader processes against one store is valuable but a distinct architectural decision (single-writer contract revisit). *(Now characterized in `docs/limits/reference-readonly-mmap.md`; promotes to Phase 7 if a workload surfaces it.)*
- **We are not shipping opt-in hints for session mode.** ADR-031 is deliberately inference-only for mutable profiles. No `OpenReadOnly` API surface. *(Still the design.)*
- **We are not shipping bit-packed atom IDs in Phase 6.** ADR-029 Decision 5 defers this. *(Promoted to a Phase 7 candidate round in `docs/limits/bit-packed-atom-ids.md`.)*
- **We are not refactoring the CLI surface.** `mercury`, `mercury-mcp`, etc. stay as they are. Only additive changes (`--limit`, `--profile`, future `--metrics-out` extensions). *(Still the design.)*

**Phase 7 is the place where most of these "post-1.8" items resurface as scoped, measured rounds.** "Out of scope for production hardening" is not the same as "out of scope for 1.7.x." The limits register is the canonical pre-Engineering catalog; promotions to Phase 7 happen when a round is metrics-equipped to validate the win.

## Exit criteria — three checklists, one per remaining phase

Under the amended version-line model, the original "one-page 1.8.0 checklist" splits into three: Phase 6 close-out (production hardening milestone), Phase 7 close-out (performance rounds), Phase 8 close-out (DrHook engine), and finally the 1.8.0 release.

### Phase 6 — production hardening close-out (current)

- [x] ADR-028 Completed. Rehash-on-grow tested under concurrency; 100 M load past prior 58 M ceiling green. *(Shipped 1.7.24, validated; status field needs final transition.)*
- [x] ADR-029 functionally complete; Reference profile loaded 21.3 B (85 h, sealed 2026-04-25). Decision 7 enforcement verified. Bulk-append dedup policy answered. *(Status field needs final transition.)*
- [x] ADR-030 Phase 1 measurement infrastructure shipped (1.7.31). *(Phases 2-3 reverted; status reconciliation needed in ADR doc — Phases 2-3 marked Superseded by ADR-032/033.)*
- [x] ADR-031 Pieces 1 and 2 Completed. 1 B cognitive read-only query Dispose 14 min → 0.84 s. Piece 3 deferred to 031b by ADR scope decision. *(Status field needs final transition.)*
- [x] ADR-032 + ADR-033 shipped + validated through 1 B and confirmed at 21.3 B. *(Status fields need final transition.)*
- [x] Wikidata Reference profile 21.3 B artifact validated query-side (`docs/validations/21b-query-validation-2026-04-26.md`).
- WDBench latencies — *moved to Phase 7.* Running WDBench against unoptimized 1.7.44 captures numbers Phase 7's rounds will obsolete, and the externally-comparable framing (vs QLever/Virtuoso) is fairer after Phase 7's wins compose. Phase 6 closes on artifact correctness (validated 2026-04-26); Phase 7c carries WDBench through the optimization arc.
- [ ] All W3C SPARQL + Turtle conformance tests green on Cognitive and Reference profiles. *(Currently green at 4,205 + 25; verify post-Phase-6 build.)*
- [ ] `STATISTICS.md` updated with final line counts and benchmark summary.
- [ ] `docs/releases/production-hardening-2026.md` written — the milestone document for production hardening close-out (not a release in the version-bump sense; the version-bump release is 1.8.0 after Phase 8).

### Phase 7 — performance rounds close-out

- [ ] Phase 7a metrics infrastructure: eight observability gap categories from `docs/limits/metrics-coverage-review.md` covered for both Cognitive and Reference. JSONL artifact pattern proven for Phase 7 round validation.
- [ ] Phase 7b BZip2 streaming: `mercury --bulk-load latest-all.ttl.bz2 --limit N` runs end-to-end without uncompressed staging. Gradient runs at 1 M / 10 M / 100 M / 1 B all sourced from the same `.bz2` artifact.
- [ ] Phase 7c onward: each of the seven characterized rounds in `docs/limits/` either (a) shipped with a captured JSONL artifact showing measured pre/post, or (b) explicitly deferred with reason recorded in the limits register.
- [x] **WDBench cold baseline** captured at Phase 7c start (post-7a, post-7b, post-substrate-hardening, pre-perf-rounds): median, p95, p99, tail distribution data against `wiki-21b-ref`. *Sealed 2026-04-30 — 1.7.47 cold baseline at `docs/validations/wdbench-paths-21b-2026-04-29-1747.jsonl` + `wdbench-c2rpqs-21b-2026-04-29-1747.jsonl`. 1,199 queries, 0 parser failures, p50=45ms, cancellation contract honored. The 04-27 first attempt surfaced 12 cancellation gaps + 12 grammar gaps + 1 Case 2 binding bug; all closed in 1.7.46/1.7.47.*
- [ ] **WDBench rerun** after each major perf round, captured as a per-round validation entry.
- [ ] **WDBench final** at Phase 7 close: consolidated comparison to QLever/Virtuoso published numbers, externally-defensible, distribution-aware, sourced from a sealed artifact.
- [ ] `docs/validations/phase7-rounds-summary.md` consolidates the measured impact across rounds *and* the WDBench arc (cold → per-round → final). The 21.3 B re-run, if undertaken, is its own deliberately-chosen validation, not an obligation.

### Phase 8 — DrHook engine close-out

- [ ] DrHook MCP server runs without netcoredbg subprocess and without `Microsoft.Diagnostics.NETCore.Client` package.
- [ ] All 13 currently-exposed MCP tools functional on the BCL-only path, or the subset that can be made functional with the rest explicitly retired.
- [ ] Substrate-independence claim true across all three substrates (Mercury + Minerva + DrHook all BCL-only).
- [ ] Validation entry recording the BCL-only inspection path's behavior on macOS/ARM64 and Linux.

### Release 1.8.0 — cognitive layers entry point

- [ ] All three close-out checklists above green.
- [ ] `Directory.Build.props` version bumped to 1.8.0.
- [ ] `docs/releases/1.8.0.md` written. **Scope note:** 1.8.0 is the cognitive-layers entry release, not the production-hardening release. Release notes summarize the substrate completion arc (Phases 1-8) and frame what cognitive-layer work begins next.
- [ ] Cognitive-layers roadmap document drafted (separate file; this roadmap closes here).

## References

**ADRs (production hardening):**
- [ADR-027 — Wikidata-Scale Ingestion Pipeline](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md) — Completed 2026-04-19, the gradient that surfaced the four findings
- [ADR-028 — AtomStore Rehash-on-Grow](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md) — status pending Phase 6 close
- [ADR-029 — Store Profiles](../adrs/mercury/ADR-029-store-profiles.md) — status pending Phase 6 close
- [ADR-030 — Bulk Load and Rebuild Performance](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) — status pending Phase 6 close (Phases 2-3 superseded by ADR-032/033)
- [ADR-031 — Read-Only Session Fast Path](../adrs/mercury/ADR-031-read-only-session-fast-path.md) — status pending Phase 6 close (Pieces 1+2 only)
- [ADR-032 — Radix External Sort (rebuild)](../adrs/mercury/ADR-032-radix-external-sort.md) — status pending Phase 6 close
- [ADR-033 — Radix External Sort (bulk-load)](../adrs/mercury/ADR-033-bulk-load-radix-external-sort.md) — status pending Phase 6 close

**Validations (measurement record):**
- [Validation 2026-04-17](../validations/bulk-load-gradient-2026-04-17.md) — NT gradient 1 M–100 M
- [Validation 2026-04-19](../validations/full-pipeline-gradient-2026-04-19.md) — NT gradient through 1 B with rebuild
- [Validation 2026-04-20](../validations/turtle-at-wikidata-scale-2026-04-20.md) — Turtle bulk-load at 100 M
- [Validation 2026-04-26](../validations/21b-query-validation-2026-04-26.md) — Query-side validation of the 21.3 B Phase 6 artifact (closes the production-hardening empirical loop)

**Phase 7 source:**
- [Limits register](../limits/) — characterized but not-yet-engineered optimization opportunities. Phase 7 candidate rounds source from here.
- [`docs/limits/streaming-source-decompression.md`](../limits/streaming-source-decompression.md) — Phase 7b source-format recommendation (`latest-all.ttl.bz2` canonical)
- [`docs/limits/metrics-coverage-review.md`](../limits/metrics-coverage-review.md) — Phase 7a metrics scope (eight observability gap categories)

**Phase 8 source:**
- Memory entry `project_drhook_engine_concept` — DrHook BCL-only rewrite scope and motivation
- Memory entry `project_drhook_eval_dead` — known limit (func-eval deadlock on macOS/ARM64 with netcoredbg) that the Phase 8 rewrite has the opportunity to resolve or formalize

**Public framing:**
- [Phase 6 article](../articles/2026-04-26-21b-wikidata-on-a-laptop.md) — the milestone narrative

## After 1.8.0

This roadmap ends at 1.8.0. **Cognitive layers** become the next focus:

- **Lucy** — deep semantic memory layered on Mercury.
- **James** — orchestration with pedagogical guidance.
- **Mira** — surface/interaction layer.
- **Sky** — agent surface integrating all three.

The three-substrate architecture validated on 2026-03-29 (see memory `project_adhoc_mvp_validated`) becomes the production target.

**DrHook is no longer in this section** — under the amended version-line model it lives in Phase 8 of 1.7.x. The "After 1.8.0" track is exclusively cognitive layers.

A separate roadmap document covers the cognitive-layers track when it begins.

Each gets its own roadmap when 1.8 ships.
