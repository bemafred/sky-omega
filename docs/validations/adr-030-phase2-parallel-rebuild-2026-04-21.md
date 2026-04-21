# ADR-030 Phase 2 Parallel Rebuild Validation — 2026-04-21

**Status:** Phase 5.1.b second architectural optimization. Gradient-tests the parallel rebuild path (commit `f320251`, version 1.7.36) against the 1.7.34 sequential baseline. Result: **neutral wall-clock win at Reference scale; the Phase 3 sort-insert work is now confirmed load-bearing for the 24 h exit criterion**, not optional.

## What changed

ADR-030 Phase 2 adds a broadcast channel between the primary-index GSPO scan and each secondary-index consumer. One producer Task drains GSPO once; N consumer Tasks (4 for Cognitive, 2 for Reference) read from their own channel and write to their own target index — ADR-020 single-writer-per-index preserved. Bounded capacity provides back-pressure; `CompleteWithException` propagates producer failures; `CancellationTokenSource` tears down siblings on any consumer failure.

The sequential rebuild of 1.7.34 (scan GSPO → insert GPOS; scan GSPO → insert Trigram; etc.) is gone. There is now one GSPO scan and N concurrent writers.

## Result — Reference profile

| Scale | Sequential (1.7.34) | Parallel (1.7.36) | Wall-clock Δ | CPU utilization |
|---|---:|---:|---:|---:|
| 10 M  | 30.6 s | **28.4 s** | −7 %   | 325 % (3.25 cores) |
| 100 M | 512 s (8 m 32 s) | **524 s (8 m 44 s)** | +2 %   | 319 % (3.19 cores) |

The 100 M sequential baseline is the [2026-04-21 Decision 5 gradient](adr-030-decision5-reference-refactor-2026-04-21.md). Same store, same input, same hardware.

**Reading the result honestly:** wall-clock is neutral at 100 M. CPU went up — parallelism is real, three cores are busy — but the wall-clock budget is capped by the slowest consumer. For Reference that's GPOS, which handles every triple and pays a random B+Tree insert per entry. Trigram finishes well inside GPOS's window. Running Trigram concurrently with GPOS doesn't reduce the GPOS dominated critical path.

At 10 M the whole working set fits in caches and Trigram's concurrent execution overlaps enough of GPOS's tail to give 7 %. At 100 M the working set exceeds cache and the critical path is GPOS's per-entry B+Tree insert cost, not Trigram's throughput.

## Why this doesn't sink the roadmap — sort-insert is the complement

The Phase 2 "3× wall-clock reduction" number in ADR-030 assumes a fan-out wide enough that consumers have roughly balanced work. Reference has two consumers with very unbalanced work (GPOS: N writes per entry; Trigram: ~half-N writes for literal objects only, and half-N is cheaper still). The theoretical ceiling for Reference parallel rebuild is:

```
wall_sequential / max(GPOS_time, Trigram_time)
= (GPOS + Trigram) / GPOS      (since GPOS > Trigram)
≈ 1.3×  at 100 M Reference
```

Measured ≈1.0×. The remaining 30 % is eaten by broadcast-channel plumbing (every quad written twice, once per consumer) and the `WaitToReadAsync().GetResult()` async-over-sync pattern at the per-entry hot path. Addressable via Phase 3 (sort-insert drops the per-entry cost by 3-5×, which changes the equation) and a later async-hot-path pass.

**The composed picture:** Phase 2 alone is neutral on Reference. Phase 3 sort-insert cuts GPOS time by 3-5×, which drops the parallel-rebuild wall-clock proportionally. Together they meet the roadmap's exit criterion — apart they don't.

Cognitive is different: 4 consumers (GPOS, GOSP, TGSP, Trigram) with less skew, so Phase 2 alone contributes a bigger fraction. We don't have a 100 M Cognitive post-Phase-2 number yet — that's a follow-up run. The roadmap's ADR-030 targeted ~3× for Cognitive specifically; the Reference result doesn't invalidate that target.

## Correctness held

Exact-match query results at every scale:

| Scale | Baseline rows | Parallel rebuild rows |
|---|---:|---:|
| 10 M  | 439,703   | 439,703   |
| 100 M | 3,212,485 | 3,212,485 |

New `ParallelRebuild_LargeDataset_QueryEquivalent` test — 2000 triples, small channel capacity to exercise back-pressure, mixed IRI/literal objects, asserts predicate-bound / subject-bound / full-scan row counts. All green. A broadcast/consumer race would fail this test with wrong counts rather than hanging or crashing.

## Listener contract amendment

`IRebuildMetricsListener.OnRebuildPhase` is now fired from consumer threads concurrently (it was sequential pre-Phase 2). Interface doc updated to say "implementations MUST be thread-safe"; `OnRebuildComplete` remains single-threaded at the end.

Test implementations switched from `List<>` to `ConcurrentBag<>` — the old code silently dropped events when two consumers fired at once. Caught by the first test run post-refactor; now regression-tested.

## Implications for Phase 5

- **Phase 5.1.b partial credit.** Parallel plumbing correct, back-pressure works, listener contract tightened. Wall-clock win modest for Reference, unmeasured for Cognitive.
- **Phase 3 sort-insert is required**, not optional. Today's GPOS random-insert per-entry cost is the critical path; sort-insert's structural O(1)-amortized per entry is what lets Phase 2's parallelism actually translate into wall-clock reduction.
- **Phase 5.2 dotTrace pass becomes more valuable.** With Phase 2 + 3 in hand, the remaining hot spots (channel overhead, async-over-sync in consumer loops, page-cache eviction patterns) are the micro-optimization surface.

## Reproduction

```bash
./tools/install-tools.sh   # 1.7.36 with parallel rebuild

# Reference gradient: time bulk + time rebuild separately
for N in 1000000 10000000 100000000; do
  store=wiki-$(( N / 1000000 ))m-ref
  rm -rf ~/Library/SkyOmega/stores/$store
  MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384 \
    mercury --store $store --profile Reference \
      --bulk-load ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt \
      --limit $N --min-free-space 50 --no-http --no-repl
  time mercury --store $store --rebuild-indexes --no-http --no-repl
done
```

## Provenance

Validation authored 2026-04-21. Two Reference-profile rebuilds (10 M and 100 M) timed and compared against the 2026-04-21 Decision 5 sequential baseline. Cognitive parallel-rebuild gradient is a follow-up run once a 100 M PrimaryOnly Cognitive store is available.

## References

- [ADR-030](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) Phase 2 — the design this validates
- [ADR-030 Decision 5 gradient 2026-04-21](adr-030-decision5-reference-refactor-2026-04-21.md) — sequential baseline
- [Production hardening roadmap 5.1](../roadmap/production-hardening-1.8.md) — Phase 5 gradient-before-scale discipline this is the second iteration of
