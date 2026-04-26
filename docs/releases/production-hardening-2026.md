# Production Hardening Milestone — 2026

**Milestone closed:** 2026-04-26
**Versions:** 1.7.13 → 1.7.44 (no major-version bump; 1.8.0 reserved for cognitive layers entry under the amended version-line model)
**Roadmap:** [`docs/roadmap/production-hardening-1.8.md`](../roadmap/production-hardening-1.8.md)

This document marks the close of Mercury's production-hardening arc. It is a milestone, not a release in the version-bump sense. The work described here ran from 2026-04-17 (start of the Wikidata-scale gradient that surfaced the four core findings) through 2026-04-26 (query-side validation of the sealed 21.3 B artifact). Eleven days of focused architectural work. Six ADRs shipped + validated. One full-Wikidata graph queryable on a single laptop.

## What Production Hardening Means

Mercury entered this arc as a working RDF triple store with W3C conformance and 1 B-triple validated bulk-load. It exits with the same conformance, plus:

- **Capacity:** the substrate ingests, stores, rebuilds indexes for, and queries the full Wikidata graph (21,260,051,924 triples) on a single M5 Max laptop with 128 GB RAM, BCL-only .NET, ~2.5 TB physical on disk (4.1 TB logical mmap, sparse-allocated on APFS).
- **Architecture:** profile-based storage dispatch (Cognitive vs Reference) with explicit lifecycle contracts and durable schema.
- **Performance:** measured 10.5× rebuild speedup at 100 M and 3.92× combined bulk+rebuild speedup at 1 B from the radix external-sort architecture (ADR-032/033). Phase 6 21.3 B end-to-end at 85 hours.
- **Observability:** measurement infrastructure (ADR-030 Phase 1) wired into bulk-load, rebuild, and per-query timing, with JSONL output for downstream analysis.
- **Read-side UX:** Cognitive read-only Dispose 14 minutes → 0.84 seconds (16.6× headline win, full magnitude given the original 14 min was misattributed to msync).
- **Empirical validation:** the 21.3 B artifact is queryable. Both indexes return correct results. Cold-cache `LIMIT 10` queries return in tens of milliseconds. The substrate's queryable-at-scale claim is no longer aspirational.

The capacity dimension of production hardening is now an empirical, sound finding. What remains is performance — characterized in `docs/limits/` with explicit trigger conditions for each round, and sequenced into Phase 7 of the roadmap.

## The Six ADRs

The arc was driven by six Architecture Decision Records, each forced by a specific hard finding from the gradient runs that preceded it. All six are Completed as of 2026-04-26.

### ADR-028 — AtomStore Rehash-on-Grow

The 1 B Cognitive run on 2026-04-19 closed at 213 M atoms / 256 M buckets, 83% load factor. Wikidata projects to ~4 B unique atoms — an order of magnitude past the fixed-bucket ceiling. ADR-028 introduced dynamic rehash-on-grow at 75% load factor while preserving the ADR-020 single-writer contract.

**Shipped:** 1.7.24. **Validated:** [`adr-028-rehash-gradient-2026-04-20.md`](../validations/adr-028-rehash-gradient-2026-04-20.md) — 1 M / 10 M / 100 M with forced 16 K initial hash, 8/11/14 rehashes per scale, exact-match query rows to baseline. Phase 6 confirmed at full 21.3 B scale: ~4 B atoms with no ceiling event.

### ADR-029 — Store Profiles

Mercury's storage schema treated every triple as bitemporal — correct for Cognitive use, expensive for archival workloads. ADR-029 introduced profile-based dispatch with the **Reference profile** (no temporal columns, structurally read-only after bulk-load) and durable `store-schema.json` metadata. Decision 7 codified Reference's mutation semantics: session-API mutations rejected at plan time; bulk-append remains as the only update path.

**Shipped:** 1.7.27 → 1.7.30. **Validated:** [`adr-029-reference-gradient-2026-04-20.md`](../validations/adr-029-reference-gradient-2026-04-20.md) — Reference vs Cognitive at 1 M / 10 M / 100 M, ~5× B+Tree index reduction confirmed (the storage-economy thesis validated). Phase 6 21.3 B Reference profile: 2.5 TB physical, well within the projected 2.6 TB envelope. Graph and Minimal profiles deferred until use cases surface.

### ADR-030 — Bulk Load and Rebuild Performance

Originally proposed as a four-phase plan: measurement infrastructure, parallel rebuild, sort-insert fast path, full-pipeline composition. **The final shape diverged from the proposal** — instructively.

- **Phase 1 (measurement infrastructure)** shipped 1.7.31 and is the foundation every subsequent perf claim measures against. **Completed.**
- **Phase 2 (parallel rebuild via broadcast channel)** shipped 1.7.36, validated as wall-clock-neutral at 100 M ([`adr-030-phase2-parallel-rebuild-2026-04-21.md`](../validations/adr-030-phase2-parallel-rebuild-2026-04-21.md)).
- **Phase 3 (sort-insert via comparator)** shipped 1.7.37, also wall-clock-neutral at 100 M ([`adr-030-phase3-sort-insert-2026-04-21.md`](../validations/adr-030-phase3-sort-insert-2026-04-21.md)).

Wall-clock-neutral *both ways* was the puzzle. The Phase 5.2 dotnet-trace + iostat investigation ([`adr-030-phase52-trace-2026-04-21.md`](../validations/adr-030-phase52-trace-2026-04-21.md)) found that wall-clock equality was hiding a structural cost shift: 1.7.37 had 453 s `GC.RunFinalizers` + 552 s `Monitor.Enter_Slowpath` that 1.7.34 baseline did not. The binding bottleneck was identified as **write amplification (~3× useful I/O)**, not CPU and not raw bandwidth — SSD at 7% of bandwidth headroom, access pattern was the lever.

This drove the **revert in 1.7.38** and the architectural pivot to ADR-032/033. ADR-030 Phases 2-3 are therefore **Superseded** by ADR-032/033; Phase 1 stands as Completed.

The instructive part: shipping wall-clock-neutral and *staying* with it would have been the easy mistake. The trace work that exposed the hidden cost is what made the revert possible — and the revert is what made the radix architecture possible. The discipline of measuring before claiming, reverting when the evidence demands, and documenting limits as they surface is what turned a wall-clock-neutral failure into a 10.5× rebuild speedup.

### ADR-031 — Read-Only Session Fast Path

The 1 B Cognitive read-only query on 2026-04-19 ran in 49 seconds and then took **14 minutes on Dispose**. The 2026-04-20 Dispose profile ([`dispose-profile-2026-04-20.md`](../validations/dispose-profile-2026-04-20.md)) found the cost was not msync (as ADR-031's draft assumed) but `CollectPredicateStatistics` called from `CheckpointInternal()` during Dispose. The fix collapsed from a complex msync-skip mechanism to a one-line gate (`if (_sessionMutated) CheckpointInternal();`).

**Shipped:** 1.7.32. **Validated:** [`adr-031-dispose-gate-2026-04-21.md`](../validations/adr-031-dispose-gate-2026-04-21.md) — 1 B Cognitive Dispose 14 min → **0.84 s** (16.6× headline win). Pieces 1 + 2 Completed; Piece 3 (optimistic read-open with live mmap escalation) is deferred to ADR-031b — with Piece 2 capturing the entire 14 min, Piece 3's remaining open-side wins are in the low-seconds range, not justifying the complexity.

### ADR-032 — Radix External Sort for Index Rebuild

ADR-030 Phase 5.2 identified the access pattern as the lever. ADR-032 implemented **LSD radix sort + chunked external merge + sequential `AppendSorted`** for both GPOS and trigram secondary indexes — converting random B+Tree leaf writes into sequential append, eliminating the write amplification.

**Shipped:** 1.7.39 → 1.7.42 across four phases (RadixSort primitive, ExternalSorter, GPOS rebuild, trigram rebuild). **Validated:**
- [`adr-032-phase3-gpos-radix-2026-04-22.md`](../validations/adr-032-phase3-gpos-radix-2026-04-22.md): GPOS rebuild 76 s → 24 s (~3× faster), peak iostat **2,463 MB/s** (7.5× the baseline 327 MB/s — sequential GPOS append finally hitting NVMe bandwidth).
- [`adr-032-phase4-trigram-radix-2026-04-22.md`](../validations/adr-032-phase4-trigram-radix-2026-04-22.md): trigram rebuild 17× faster, total 100 M Reference rebuild **511 s → 48.64 s (10.5× faster)**.

Phase 6 confirmed the architecture at 21.3 B scale.

### ADR-033 — Bulk-Load Radix External Sort

Same architectural pattern as ADR-032, applied to the bulk-load primary GSPO path. The hypothesis from Phase 5.2 (access pattern is the lever) verified in three independent code paths: GPOS rebuild, trigram rebuild, GSPO bulk-load.

**Shipped:** 1.7.43. **Validated:** [`adr-033-phase5-bulk-radix-2026-04-22.md`](../validations/adr-033-phase5-bulk-radix-2026-04-22.md) — 1 B end-to-end ~3 h 57 m → **60 m 36 s (3.92× combined speedup)**. Phase 6 21.3 B end-to-end at 85 h confirmed the architecture at full scale.

## Phase 6 — The Production Validation Run

The production-hardening exit event. ADR-033 commit `aa35514` bumped the Reference index BulkMode floor from 256 GB to 1 TB to accommodate the 21.3 B mmap geometry. Phase 6 launched 2026-04-22 and ran continuously for 85 hours.

**Result:** sealed 2026-04-25 22:32. **21,260,051,924 triples** ingested into a Reference profile store; **~2.5 TB physical on disk** (4.1 TB logical mmap, sparse-allocated on APFS); both GSPO (primary, bulk-load) and GPOS (secondary, rebuild) indexes built. The discipline of monitored stability — checking the substrate's quiet hour after hour for 85 continuous hours — is the body of the achievement; the 21.3 B headline is the closing measurement.

The article [`2026-04-26-21b-wikidata-on-a-laptop.md`](../articles/2026-04-26-21b-wikidata-on-a-laptop.md) frames the public narrative.

The query-side validation [`21b-query-validation-2026-04-26.md`](../validations/21b-query-validation-2026-04-26.md) closes the empirical loop. Both indexes return correct results. The article's "queryable at scale" claim is no longer aspirational. **The capacity dimension of production hardening is empirical, sound evidence.**

## What Remains in 1.7.x

Production hardening is closed. **1.8.0 is reserved for cognitive layers entry**, not for this milestone.

Two phases remain in 1.7.x:

- **Phase 7 — Performance rounds.** Seven optimization rounds characterized in `docs/limits/`, sequenced by enabling-dependency: metrics infrastructure first, then BZip2 streaming source decompression (single source-of-truth artifact, gradient runs cheap), then measured-impact perf rounds in trace-evidence order. Conservative compounding of Rounds 1-4 projects a fully-tuned 21.3 B run somewhere around 15-25 hours on the same laptop — though that projection is now subject to Phase 7's rule: no perf claim without metrics-validated measurement.

- **Phase 8 — DrHook engine (BCL-only).** Replace netcoredbg + `Microsoft.Diagnostics.NETCore.Client` with a BCL-only runtime-inspection implementation, restoring substrate independence across all three substrates (Mercury + Minerva + DrHook). "The very last of 1.7, possibly" — possibly because the engineering scope is uncertain at milestone time.

The roadmap document [`production-hardening-1.8.md`](../roadmap/production-hardening-1.8.md) carries the full sequencing, exit criteria for Phases 7 and 8, and the version-line model that makes 1.8.0 the cognitive-layers boundary.

## Test Suite — All Green

| Project | Tests |
|---|---:|
| Mercury.Tests | 4,205 |
| Mercury.Solid.Tests | 25 |
| DrHook.Tests | 23 |

W3C conformance: 100% on core (1,181/1,181), 100% on SPARQL 1.1 Query (421/421) and Update (94/94), 99% with optional extensions (2,063/2,069 — six skipped: 4 JSON-LD 1.0 legacy + 2 generalized RDF, both intentional).

## The Discipline Behind the Numbers

Eleven days produced six ADRs, three architectural reverts, two architectural pivots, and one fully-validated 21.3 B Wikidata graph queryable on a laptop. The numbers are real because the discipline behind them is documented:

- **Gradient before scale.** Every architectural change exercised at 1 M / 10 M / 100 M before any 1 B run, every 1 B run before the 21.3 B commitment. No blind full-scale attempts.
- **Profile before commit.** Wall-clock equality is not validation; the Phase 5.2 trace work caught what wall-clock alone missed. Commit only after the *cost shape* is understood.
- **Revert when evidence demands.** ADR-030 Phases 2 and 3 shipped, then reverted, then replaced with a better architecture. The ability to revert is what made the better architecture possible.
- **Limits register, not Consequences sections.** Items past Emergence + Epistemics but pre-Engineering live in `docs/limits/` where they remain visible. Burying them in ADR Consequences sections — where they become invisible after the ADR closes — is the failure mode this register exists to prevent. Phase 7 sources from here.
- **Memory must be reflexive.** Observations recorded in Mercury as they happen, not when prompted. The memory system is not a write-when-asked archive; it is the substrate's own working state, and the discipline of using it is what makes future sessions coherent with this one.

The collaboration that produced this work was a sustained human-AI engineering arc — architectural decisions, debugging, validation rhythms, the limits register itself, all developed in dialogue with shared epistemic discipline. Sky Omega is built for that kind of partnership; the engineering that built Mercury's production-hardened state is itself a demonstration of what Sky Omega is for.

## References

**ADRs (all Completed 2026-04-26):**
- [ADR-027 — Wikidata-Scale Ingestion Pipeline](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md)
- [ADR-028 — AtomStore Rehash-on-Grow](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md)
- [ADR-029 — Store Profiles](../adrs/mercury/ADR-029-store-profiles.md)
- [ADR-030 — Bulk Load and Rebuild Performance](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) (Phase 1 Completed; Phases 2-3 Superseded by ADR-032/033)
- [ADR-031 — Read-Only Session Fast Path](../adrs/mercury/ADR-031-read-only-session-fast-path.md) (Pieces 1+2 Completed; Piece 3 deferred to 031b)
- [ADR-032 — Radix External Sort (rebuild)](../adrs/mercury/ADR-032-radix-external-sort.md)
- [ADR-033 — Radix External Sort (bulk-load)](../adrs/mercury/ADR-033-bulk-load-radix-external-sort.md)

**Validations (the measurement record):**
- See [`docs/validations/`](../validations/) for the full arc, with [`21b-query-validation-2026-04-26.md`](../validations/21b-query-validation-2026-04-26.md) closing the empirical loop.

**Roadmap:**
- [`production-hardening-1.8.md`](../roadmap/production-hardening-1.8.md) — the canonical sequencing document, amended 2026-04-26 with the new version-line model and Phase 7/8 plans.

**Public framing:**
- [`2026-04-26-21b-wikidata-on-a-laptop.md`](../articles/2026-04-26-21b-wikidata-on-a-laptop.md) — the milestone article.
