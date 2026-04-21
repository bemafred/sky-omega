# Production Hardening Roadmap — Sky Omega 1.8.0

**Status:** Drafted 2026-04-20. Sequences ADR-028, ADR-029, ADR-030, ADR-031 toward a 1.8.0 release.

## Progress (updated 2026-04-21)

| # | Phase | Target versions | Status | Evidence |
|---|---|---|---|---|
| 1 | ADR-028 rehash-on-grow | 1.7.24–1.7.26 | ✅ Shipped 1.7.24 | `docs/validations/adr-028-rehash-gradient-2026-04-20.md` — 1 M / 10 M / 100 M exact-match to pre-rehash baseline. Stage 3 (full Wikidata) deferred with ADR-029. |
| 2 | ADR-029 Reference profile | 1.7.27–1.7.30 | ✅ Functionally complete 1.7.30 | `docs/validations/adr-029-reference-gradient-2026-04-20.md` — 5× B+Tree index reduction at 100 M confirmed; 21.3 B projection fits 8 TB. ADR-030 Decision 5 amended after the gradient exposed the inline-secondary-write cost. |
| 3 | ADR-030 Phase 1 measurement infrastructure | 1.7.31 | ✅ Shipped 1.7.31 | `QueryMetrics` / `RebuildMetrics` + `IQueryMetricsListener` / `IRebuildMetricsListener` + `JsonlMetricsListener` + CLI `--metrics-out` integration. 8 tests. First practical use during Phase 4 validation. |
| 4 | ADR-031 Pieces 1+2 | 1.7.32–1.7.33 | ✅ Shipped 1.7.32, validated 1.7.33 | `docs/validations/adr-031-dispose-gate-2026-04-21.md` — 1 B Cognitive Dispose 14 min → 0.84 s on wiki-1b. 13 flag tests. Phase 3 metrics captured the measurement. |
| 5 | ADR-030 Phases 2-4 (parallel rebuild + sort-insert + Reference bulk refactor + 21.3 B validation) | 1.7.34–1.7.38 | ⏭ Next | Incorporates ADR-030 Decision 5 Reference refactor (bulk-writes-GSPO-only mirroring Cognitive). |
| 6 | Release 1.8.0 | 1.8.0 | ⏭ Pending | After Phase 5 completes and the full 21.3 B Reference run lands at a documented endpoint. |

Phases 1-4 took roughly two days of focused work (2026-04-20 and 2026-04-21). The remaining work in Phase 5 is the substantial piece of the 1.8.0 roadmap — both in code complexity (parallel broadcast + sort-insert) and in validation wall-clock (one full 21.3 B run). See [Risks and mitigations](#risks-and-mitigations) below for the budget.

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

### Phase 6 — Release

**Target version:** **1.8.0**
**Objective:** Clean release marker: production hardening complete.

**ADR-031 Piece 3 is explicitly deferred to 031b** based on the 2026-04-20 Dispose profile: with Piece 2 capturing the entire 14 min (CheckpointInternal, not msync), Piece 3's remaining open-side wins are in the low-seconds range and do not justify the live mmap escalation complexity in this timeline. ADR-031 closes at Pieces 1 and 2 for 1.8.0.

Required before release:
- All four ADRs moved from Proposed → Accepted → Completed with dated status fields. ADR-031 Completed at Pieces 1 + 2 (Piece 3 deferred to 031b by ADR scope decision — not pending work).
- Full W3C SPARQL + Turtle conformance suites green on all four profiles (Cognitive, Graph, Reference, Minimal — or justify Minimal deferral to post-1.8).
- Wikidata Reference profile 21.3 B live at a documented endpoint (internal or external). Captured WDBench-style query latencies against it, comparable to QLever/Virtuoso published numbers.
- `STATISTICS.md` updated with the final line counts and benchmark summary.
- Release-notes document in `docs/releases/1.8.0.md` summarizing the hardening arc.

**Exit from 1.8.0 development:** the DrHook track and cognitive-layers track become the next focus. This roadmap does not plan those.

## Version strategy

**During development (phases 1–5):** stay on 1.7.n. Each substantive merge to `main` bumps the patch. This is already the project's pattern (see `Directory.Build.props` and recent commit history). Don't prematurely bump to 1.8; leave that as an earned milestone.

**Bump to 1.8.0** only when Phase 6's required exit criteria are met. 1.8.0 means "production-hardened; downstream tracks (DrHook, cognitive) are cleared to proceed."

**Branch strategy:** proposed all-on-`main` with frequent small commits, per the existing project convention. If a phase's work needs a longer-lived branch (likely ADR-030 Phase 2 due to parallel-correctness churn), use a topic branch named `hardening/adr-030-phase-2` and merge when green.

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

## Explicit non-goals for 1.8.0

Listed here so the "no" is as visible as the "yes":

- **We are not shipping a full query planner overhaul.** Cost-based join ordering, column statistics, cardinality estimation — all deferred. Today's index-pick-and-scan planner is adequate for 1.8.
- **We are not shipping the Graph profile unless a use case surfaces.** ADR-029 proposes it; it may collapse into Reference or Cognitive. The open question in ADR-029 stays open through 1.8; implement only Cognitive and Reference first.
- **We are not shipping cross-process read-only sharing.** Multi-reader processes against one store is valuable but a distinct architectural decision (single-writer contract revisit). Out of scope.
- **We are not shipping opt-in hints for session mode.** ADR-031 is deliberately inference-only for mutable profiles. No `OpenReadOnly` API surface.
- **We are not shipping bit-packed atom IDs.** ADR-029 Decision 5 defers this. Revisit post-1.8.
- **We are not refactoring the CLI surface.** `mercury`, `mercury-mcp`, etc. stay as they are. Only additive changes (`--limit`, `--profile`) in scope.

## Exit criteria for 1.8.0 — the one-page checklist

Copying what matters from the six phases above into one reviewable list:

- [ ] ADR-028 Completed. Rehash-on-grow tested under concurrency; 100 M load past prior 58 M ceiling green.
- [ ] ADR-029 Completed. Reference profile loaded 21.3 B within 24 h and within 2.6 TB on disk. Decision 7 enforcement verified (session-API mutation rejected, bulk-append works). Bulk-append dedup policy answered.
- [ ] ADR-030 Completed. Phase 1 measurement infrastructure shipped and used in subsequent claims. Parallel rebuild equivalence-tested. Full 21.3 B pipeline under 24 h.
- [ ] ADR-031 Pieces 1 and 2 Completed. 1 B cognitive read-only query Dispose under 5 s. Piece 3 either Completed or explicitly deferred to 031b.
- [ ] All W3C SPARQL + Turtle conformance tests green on Cognitive and Reference profiles.
- [ ] `STATISTICS.md` updated.
- [ ] `docs/releases/1.8.0.md` written, listing every commit tied to the four ADRs.
- [ ] Version bumped to 1.8.0 in `Directory.Build.props`.
- [ ] Release notes published.

## References

- [ADR-027 — Wikidata-Scale Ingestion Pipeline](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md) — Completed 2026-04-19, the gradient that surfaced the four findings below
- [ADR-028 — AtomStore Rehash-on-Grow](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md) — Proposed
- [ADR-029 — Store Profiles](../adrs/mercury/ADR-029-store-profiles.md) — Proposed, includes Decision 7
- [ADR-030 — Bulk Load and Rebuild Performance](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) — Proposed
- [ADR-031 — Read-Only Session Fast Path](../adrs/mercury/ADR-031-read-only-session-fast-path.md) — Proposed
- [Validation 2026-04-17](../validations/bulk-load-gradient-2026-04-17.md) — NT gradient 1 M–100 M
- [Validation 2026-04-19](../validations/full-pipeline-gradient-2026-04-19.md) — NT gradient through 1 B with rebuild
- [Validation 2026-04-20](../validations/turtle-at-wikidata-scale-2026-04-20.md) — Turtle bulk-load at 100 M

## After 1.8.0

This roadmap ends at 1.8.0. The next two tracks are not planned here:

- **DrHook track** — continue the BCL-only runtime-inspection work (see memory entry `project_drhook_engine_concept`). Replace netcoredbg + Microsoft.Diagnostics.NETCore.Client; restore substrate independence.
- **Cognitive layers track** — Lucy (deep semantic memory), James (orchestration), Sky (agent surface). The three-substrate architecture validated on 2026-03-29 (see `project_adhoc_mvp_validated`) becomes the next production target.

Each gets its own roadmap when 1.8 ships.
