# ADR-033 Phase 5 Validation — Bulk-load radix external sort, gradient + 1B end-to-end

**Status:** ADR-033 implementation validated through 1B Reference end-to-end. Combined bulk + rebuild at 1B drops from ~3h57m baseline to **60m36s** (3.92× combined speedup), driven primarily by the rebuild side (ADR-032 Phases 3+4, 13.8×) with the bulk side contributing 6% on its own. The bulk path preserves architectural correctness for past-RAM scales (21.3B, ~2.3 TB working set vs 128 GB RAM) where random GSPO insert would thrash. Linear extrapolation to 21.3B: ~21h30m combined, within Phase 6's 24h target.

## Setup

- Mercury 1.7.43 (commit pending — ADR-033 implementation)
- Source: `/Users/bemafred/Library/SkyOmega/datasets/wikidata/full/latest-all.nt` (3.1 TB)
- Reference profile, BulkMode = true via `--profile Reference --bulk-load`
- iostat per Phase 5.2 protocol

## Result — gradient at 1M / 10M / 100M / 1B

| Scale | Bulk wall-clock | Bulk avg rate | Rebuild wall-clock | Combined |
|---|---:|---:|---:|---:|
| 1 M | 7.58 s | 194 K/sec | (skipped) | — |
| 10 M | 45.01 s | 247 K/sec | (skipped) | — |
| 100 M | 327.57 s (5m27s) | 309 K/sec | 41.51 s | 369 s (6m9s) |
| 1 B | **2822 s (47m02s)** | **354 K/sec** | **814 s (13m34s)** | **3636 s (60m36s)** |

Same correct output at 1B: 991,797,873 GPOS entries + 444,002,714 trigram entries.

Bulk rate increases with scale (194K → 247K → 309K → 354K triples/sec). The opposite of the random-insert pattern, which would degrade as the GSPO B+Tree grows past cache. Sequential append via the sorter is amortizing parser+atom-store fixed costs.

## Comparison to baseline at 1B

| Phase | Baseline (project memory) | 1.7.43 | Speedup |
|---|---:|---:|---:|
| Bulk-load | ~50 min (3000 s) | 47m02s (2822 s) | 1.06× |
| Rebuild | 3h07m (11240 s) | 13m34s (814 s) | 13.8× |
| **Combined** | **~3h57m (~14240 s)** | **60m36s (3636 s)** | **3.92×** |

The combined 3.92× speedup is the headline. The rebuild's 13.8× contribution is from ADR-032 Phases 3+4 (validated at 100M as 10.5× there; the larger gain at 1B reflects the bigger working-set-vs-cache disparity at scale). The bulk's 6% contribution is from ADR-033.

## Why ADR-033's bulk gain at 1B is modest

The bulk-load wall-clock is split across three cost components:

1. **Parser** — file I/O + tokenization, CPU-bound.
2. **Atom store interning** — hash table + data file writes, ~4 calls per triple. This is now what dominates at 1B based on the trace from Phase 5.2 (`ChangeCaseCommon`, atom store hash lookups).
3. **GSPO write** — the part ADR-033 changes. Today's random AddRaw vs new sequential AppendSorted via sorter.

At 1B Reference on 128 GB RAM:
- Atom store data: ~16 GB (resident)
- GSPO B+Tree actual data: ~32 GB (random pages touched across the 256 GB sparse file)
- Atom hash table: 16 GB (resident sparse — ~50% touched at 1B scale)
- ExternalSorter scratch: 1 GB LOH

Total resident: ~65 GB out of 128 GB. **The random-insert pattern's working set still fits comfortably in OS page cache.** Random vs sequential makes little difference when nothing is paging.

At 21.3B Reference:
- Atom store data: ~340 GB (well past RAM)
- GSPO actual data: ~680 GB (well past RAM)
- Atom hash: ~16 GB (capped at the 256M-bucket allocation)

Past-RAM working set means random insert would page-fault aggressively on cold pages. Sequential append (radix path) keeps the access pattern bandwidth-friendly. The structural advantage of ADR-033 is *defensive at scales below RAM, decisive past RAM.*

## v1 implementation: 25× regression, then root-caused

The first ADR-033 implementation allocated and disposed the sorter per-`BeginBatch`/`CommitBatch` cycle. RdfEngine's parser flushes every 100K triples — a 1B load would have been ~10K BeginBatch/CommitBatch round-trips, each with:

- 1 GB LOH allocation (sorter buffer + scratch)
- 100K AddCurrentBatched calls into the sorter
- Drain via AppendSorted (only valid for the first batch — subsequent batches violate AppendSorted's non-decreasing-keys contract against a populated tree)

Measured: 1M Reference bulk-load = 163 s (vs 7.6 s baseline). 21× slower. The first chunk's AppendSorted ran in ~1s; chunks 2-10 fell back to AddRaw against a tree partially populated by the first chunk's sorted run. The 1 GB LOH allocation per cycle plus the page-cache eviction it triggered explained the rest.

**The fix:** `_bulkSorter` allocates lazily on the first AddReferenceBulkTriple call when `_bulkLoadMode` is set, persists across **all** BeginBatch/CommitBatch cycles in the bulk session, and drains exactly once at FlushToDisk (the explicit "bulk-load complete" boundary). One LOH allocation, one drain, one sorted run into AppendSorted. Per-batch CommitBatch flushes are skipped while the sorter is active (single durability boundary at session end).

Result after fix: 1M = 7.58 s, matching baseline. v1's regression was 100% architectural debt in the lifecycle, not in the radix sort itself.

## Architectural takeaway

Three independent confirmations of the same Phase 5.2 hypothesis ("the bottleneck is access pattern, not CPU and not raw bandwidth") in three different code paths:

- ADR-032 Phase 3: GPOS rebuild, 3.5× faster, peak I/O 7.5× higher
- ADR-032 Phase 4: trigram rebuild, ~17× faster, sustained 200-400 MB/s
- ADR-033: bulk GSPO insert, sequential pattern preserved at 1B (defensive), 6% wall-clock gain

Each phase confirmed the random→sequential lever. The ADR-033 bulk gain is small at scales where everything fits in RAM, but the architecture is now correct for the scales where it matters (21.3B).

The combined 3.92× at 1B end-to-end is the practical headline. Combined with the 21.3B linear extrapolation (~21h30m) landing inside the 24h Phase 6 target, full-Wikidata becomes a routine operation rather than a multi-day ceremony.

## Hardware constraint reminder (per ADR-033)

RAID is not available on this hardware (Apple Silicon, soldered NVMe, no add-in cards). The architectural improvements have to come from access pattern, not hardware. ADR-032 + ADR-033 deliver exactly that.

## Artifacts

- `~/Library/SkyOmega/traces/bulk-ref-{1m,10m,100m}-v2.log` — gradient runs
- `~/Library/SkyOmega/traces/bulk-ref-1b-v2.log` — 1B end-to-end (bulk + rebuild)
- `~/Library/SkyOmega/traces/io-bulk-ref-{1m,10m,100m,1b}-v2.log` — iostat captures
- `~/Library/SkyOmega/traces/rebuild-100m-radixstore.log` — 100M rebuild on radix-bulk-loaded store

## Next steps

- Update ADR-033 status: Proposed → Accepted (validated at 100M and 1B)
- 21.3B Wikidata end-to-end (Phase 6) — the architecture is ready; the run is the validation
- Post-Phase-6 trace pass to identify the next optimization target (likely UTF-8 lowercase based on Phase 5.2 evidence)
