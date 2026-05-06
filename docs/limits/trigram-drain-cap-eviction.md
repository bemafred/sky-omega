# Limit: Trigram drain hits FD cap; ~23% eviction overhead at 21.3 B

**Status:**        Latent (Monitoring — cycle 8 evidence-justified)
**Surfaced:**      2026-05-06, during cycle 8 trigram drain phase. With 10,456 chunks (each 192 MB, 16 M `TrigramEntry` records) and the merge-pool hard-cap of 8K, the LRU pool evicts/reloads ~23% of chunk accesses. Drain completed cleanly in 8 h 24 m at sustained 7.5 M/s.
**Last reviewed:** 2026-05-06

## Description

`ExternalSorter<TrigramEntry>.Merge` during the trigram-rebuild drain phase produces ~10,456 chunks at 21.3 B Wikidata scale. Pool is capped at 8K (commit `880bfe1` — the cap that prevents EMFILE on macOS launchd children). At chunkCount > cap, the pool's LRU mechanism evicts least-recently-used streams to make room for new opens.

Empirically observed at 21.3 B (cycle 8):
- 10,456 chunks total
- 8,000 streams in pool at any moment
- ~2,456 chunks rotate in/out via eviction
- Open-FD count sustained at 8,356 (well under the 10K crash threshold)
- ~23% miss rate (chunks not in pool when accessed)
- Each miss = open() + seek() ≈ 12 μs
- Total miss cost: 167 B records × 23% × 12 μs ≈ **3.4 h overhead** added to drain phase

Drain completed in 8 h 24 m vs ~5 h projected if 100% pool hit rate. The eviction overhead is not catastrophic — but it's the largest single Round 2 win available on the rebuild phase.

## Why this is a register entry

This is the architectural successor to the FD-ceiling crash (`urn:sky-omega:incident:cycle8-trigram-fd-crash-2026-05-05`). The crash fix was lowering the cap from 32K to 8K. That trade is: avoid abort, accept eviction overhead. The eviction overhead is now characterized; mitigations are clear.

This sits between Emergence (the trigram drain having 10K+ chunks was unknown until cycle 8) and Engineering (the mitigation work for Round 2).

## Trigger condition

This limit moves toward an ADR / Round 2 work when one of:

1. **Round 2 substrate planning targets the rebuild as a wall-clock priority.** ~3-4 h of eviction overhead is the largest single rebuild-phase optimization available.
2. **Future scale pushes chunk count substantially above 8K.** At 100 B Wikidata-shape ingest, projected chunk count > 30K → 70%+ miss rate → drain time would dominate end-to-end.
3. **External benchmark publication frames query latency vs ingest wall-clock.** Eviction overhead inflates total ingest, affecting the comparison frame.

## Current state

- Mercury 1.7.47 (cycle 8): drain completes at 21.3 B with ~23% miss rate, no abort. ~3.4 h eviction overhead.
- 8K cap is structurally safe; the crash mode is gone.
- Drain wall-clock dominates the rebuild phase (8 h 24 m of the 9 h 25 m total rebuild).

## Candidate mitigations

Listed cheapest-first; not mutually exclusive.

1. **Larger chunk size for trigram drain.** Bump from 192 MB → 1 GB (matches the atom-merge chunk size). 10,456 → ~2,000 chunks. Pool=2,000 ≪ 8K cap → 100% hit rate. Single-line constant change. Memory cost: 1 GB sort buffer during emission (vs 192 MB) — fine on 128 GB host.
2. **Hierarchical merge (cascade).** First pass merges 100 chunks at a time → 105 super-chunks. Second pass merges 105 → final. Two passes over data (2× total bytes read) but each pass has 100% pool hit rate. May be slower than (1) due to 2× I/O, but doesn't change chunk-size policy.
3. **Runtime FD-limit detection** (sibling entry: `runtime-fd-detection.md`). Cap auto-sized to `getrlimit(RLIMIT_NOFILE)` minus headroom. On Linux servers (typical limit 65K-1M), pool can hold all 10K trigram chunks. Substantially reduces the cap-driven eviction class on non-macOS hosts.

The natural sequencing is (1) immediately — same-day commit, same-day measurement on next 21.3 B run. (2) and (3) become Round 2 work if (1) is insufficient.

## Why this matters beyond rebuild wall-clock

Two secondary effects:

1. **The trigram emission produces 10× the records the atom-merge does.** Trigram fan-out (each atom → ~10 grams) means trigram intermediate (2 TB) exceeds atom-merge intermediate (1 TB) at 21.3 B. The same architectural pattern (large k-way merge) hits cap pressure earlier in the rebuild than in the bulk-load proper.
2. **Cap pressure is workload-dependent.** A workload with shorter atom values produces fewer trigram records → fewer chunks → no eviction. Wikidata's literal-heavy vocabulary stresses this path.

## References

- `urn:sky-omega:incident:cycle8-trigram-fd-crash-2026-05-05` (Mercury) — the crash that surfaced this
- `docs/validations/adr-034-21b-2026-05-06.md` — cycle 8 validation report with all measurements
- `docs/limits/external-merge-intermediate-disk-pressure.md` — sibling, focuses on intermediate volume
- Commits `dddfda3` (pool wired into ExternalSorter), `880bfe1` (cap 32K → 8K)
- `src/Mercury/Storage/TrigramEntry.cs` — record format (12 bytes, packed uint+long)
- `src/Mercury/Storage/TrigramIndex.cs` — emission logic
