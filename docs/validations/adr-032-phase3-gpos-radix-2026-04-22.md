# ADR-032 Phase 3 Validation — GPOS rebuild via radix external sort

**Status:** Phase 3 architectural integration complete. 100M Reference rebuild on Mercury 1.7.41 (commit pending) drops wall-clock 11% (511 s → 457 s) and the GPOS portion alone drops ~3× (76 s → ~24 s). The headline is the access-pattern change: GPOS write I/O peaks at **2463 MB/s** (vs 327 MB/s baseline, 7.5× higher), confirming the SSD bandwidth ceiling Phase 5.2 said was available is now being used. Trigram is still random-amplified and dominates the remaining wall-clock — Phase 4 target.

## Setup

- Mercury 1.7.41 (commit pending — radix sort + ExternalSorter + AppendSorted integrated)
- Store: `~/Library/SkyOmega/stores/wiki-100m-ref` (100 M Reference profile, Ready state)
- Command: `mercury --store wiki-100m-ref --rebuild-indexes --no-http --no-repl`
- Capture: `iostat -d -w 2 disk0` and `vm_stat 2` (per Phase 5.2 protocol)
- Baseline: 1.7.38 = same architecture as 1.7.34, post-revert; 511 s wall, 327 MB/s peak iostat

## Result

| Metric | 1.7.38 baseline | 1.7.41 (Phase 3) | Delta |
|---|---:|---:|---:|
| Total wall-clock | 511 s | **457 s** | -54 s (-11%) |
| GPOS rebuild time (estimated) | ~76 s | **~24 s** | ~3× faster |
| Trigram rebuild time (estimated) | ~435 s | ~430 s | unchanged |
| GPOS phase peak iostat | ~327 MB/s | **2463 MB/s** | 7.5× higher |
| GPOS phase sustained iostat | 100-300 MB/s | 500-2000 MB/s | ~5-7× higher |
| Trigram phase iostat | 100-300 MB/s | 10-30 MB/s | (page cache hits — see below) |
| GPOS entries written | 99,166,092 | 99,166,092 | identical |
| Trigram entries written | 45,667,806 | 45,667,806 | identical |

The trigram MB/s drop relative to the baseline is likely page-cache effects from repeated rebuilds during this session, not a structural change — the trigram code path is byte-for-byte the same in 1.7.38 and 1.7.41.

## What changed mechanically

**GPOS rebuild loop (1.7.38, random-insert):**
```
for each entry in GSPO scan:
    _gposReference.AddRaw(g, p, o, s)   // walks B+Tree, ~3-5 random page touches per entry
```

**GPOS rebuild loop (1.7.41, radix external sort + sequential append):**
```
sorter = ExternalSorter<ReferenceKey, ReferenceKeyChunkSorter>(tempDir, chunkSize=16M)
for each entry in GSPO scan:
    sorter.Add(remap_to_GPOS_layout(entry))   // buffers in-memory, spills to disk on chunk fill
sorter.Complete()
_gposReference.BeginAppendSorted()
while sorter.TryDrainNext(out var key):
    _gposReference.AppendSorted(key)          // appends to rightmost leaf, O(1) per entry
_gposReference.EndAppendSorted()
```

The B+Tree leaves are touched once each, in physical order, instead of N times each in random order. Disk writes go from random ~3× write-amplified to sequential append.

## Why only 11% total improvement

Trigram dominates. The 1.7.34 trace (Phase 5.2) showed:
- Trigram inclusive: 448 s (87% of 511 s rebuild)
- GPOS inclusive: 76 s (15%)

GPOS reduction of 52 s = the entire 54 s wall-clock improvement. The trigram code path was not touched in this phase — it remains random-write-amplified on its hash-bucket posting list pages.

## Why the GPOS peak hit 2463 MB/s

Phase 5.2 measured the SSD's idle headroom: ~7% of bandwidth (327 / 5000 MB/s) and ~2% of IOPS at the rebuild's old random-write rate. The hardware was capable of 5 GB/s sequential; the access pattern was the bottleneck.

Sort-insert append writes B+Tree leaves in physical order — sequential, OS-coalescable into large multi-page writes. The 2463 MB/s peak is real disk bandwidth being utilized for the first time during a rebuild. This is the same pattern that lets `mercury --convert` hit ~5 GB/s: convert is sequential, GPOS-via-radix is now sequential.

## Architectural validation

The Phase 5.2 hypothesis was: **the rebuild is I/O-bound by access pattern, not by CPU and not by raw bandwidth — and the lever is converting random to sequential.** Phase 3 tests that hypothesis on the GPOS path:

- Predicted: GPOS portion drops dramatically because random→sequential
- Measured: ~3× faster GPOS, peak I/O 7.5× higher
- Predicted: SSD bandwidth utilization rises sharply
- Measured: 327 MB/s → 2463 MB/s peak
- Predicted: Total wall-clock drops only modestly because trigram dominates and is unchanged
- Measured: 11% total improvement, all attributable to GPOS

The mechanism behaves as the model said it would. The radix external sort architecture works.

## What this means for Phase 4 and 21.3 B

If Phase 4 applies the same pattern to trigram, projecting linearly:

- Trigram: ~448 s → ~60-90 s (estimated, similar 5-8× speedup as GPOS)
- Total: 457 s → ~80-120 s at 100 M Reference

Linear extrapolation to 21.3 B Reference (213× the data):
- 100 s × 213 = ~21,000 s ≈ **6 hours** for the rebuild alone

That's well under the 24 h Phase 6 budget, with headroom for combined bulk + rebuild and the past-RAM penalty. The 24 h target stops being a stretch.

These are estimates — Phase 4 needs to be measured before any of this is real. But Phase 3's clean architectural confirmation gives the projection plausible foundation.

## Artifacts

- `~/Library/SkyOmega/traces/io-100m-phase3.log` — iostat capture
- `~/Library/SkyOmega/traces/vm-100m-phase3.log` — vm_stat capture
- `~/Library/SkyOmega/traces/mercury-100m-phase3-stdout.log` — Mercury output
