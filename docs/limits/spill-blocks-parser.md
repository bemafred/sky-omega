# Limit: Single-threaded spill blocks parser during sort + write

**Status:**        Latent (Monitoring — cycle 6 21.3 B run is the evidence)
**Surfaced:**      2026-05-03, during the cycle 6 verification of 1 GB chunk sizing. Recent-rate dropped to ~21K /sec during sort bursts vs ~450K /sec sustained — visible parser pause during in-memory sort + chunk write.
**Last reviewed:** 2026-05-03

## Description

`SortedAtomBulkBuilder` runs the parser, atom-buffer accumulation, in-memory sort, and chunk-file write **on a single thread**. When the spill buffer reaches `chunkBufferBytes` (now 1 GB), the parser is paused while:

1. **Sort:** `List<T>.Sort(Comparison<T>)` over ~16 M records (BCL introsort with delegate-based comparer). Estimated 5-6 sec at 1 GB chunk size.
2. **Write:** sequential dump of sorted records to `chunk-NNNNNN.bin`. ~5 sec at typical SSD throughput.

Total per-spill parser pause: **~10-11 seconds**. While this happens, no triples are parsed, no atoms are interned, no chunk progress accrues toward the next billion.

Cycle 5 (256 MB chunks) had ~4× more spills but each pause was ~4× shorter; total parser-blocked time was approximately equal. Cycle 6 (1 GB chunks) redistributes the same total pause into fewer-but-longer bursts. The throughput average (~424K /sec measured early) suggests an ~8% reduction vs cycle-1 baseline (459K /sec) — likely attributable to this serialization, though full-run wall-clock metrics will confirm.

## Why this is a register entry

The current architecture serializes by design: one thread, simple control flow, no concurrency overhead. That was reasonable when chunk sizes were small (256 MB sort = ~1 sec, almost imperceptible). With chunk sizes now scaling to 1 GB+ to manage FD pressure, the sort pause becomes a measurable wall-clock cost.

The architectural axis is: **does the parser thread share the spill thread, or pipeline it?** Today: shared. The cost was hidden when spills were short. With 1 GB chunks the cost is visible. At hypothetical future scale (4 GB chunks for 100 B+ datasets, or richer per-record processing) the cost becomes binding.

This sits exactly in the limits-register charter: characterized cost, named alternatives, evidence pending, deferred decision.

## Trigger condition

This limit moves toward an ADR / Round 2 work when one of:

1. **Cycle 6 wall-clock confirms ≥5% cost from sort-pauses.** Compare 21.3 B end-to-end against the cycle-1 trajectory (446K /sec sustained × ~13.2 h projected parser). If the actual is >5% slower attributable to spill, mitigation becomes binding.
2. **Future scale moves chunk size higher.** A 100 B+ dataset or richer per-record processing pushes chunk size to 4 GB+; per-spill pause approaches 30+ sec, a substantial fraction of total wall-clock.
3. **Round 2 prefix-compression of intermediate chunks lands.** Prefix compression on the chunk format (sibling limits-register entry: `external-merge-intermediate-disk-pressure.md`) makes per-record sort cheaper but doesn't help if the pause is dominated by sort *coordination* rather than per-record cost.
4. **External benchmark comparison surfaces the gap.** When publishing wall-clock numbers vs systems with pipelined ingest (QLever, Blazegraph), the asymmetry becomes a comparison footnote.

## Current state

**Actual code path** (from `SortedAtomBulkBuilder.cs`):

- `AddTriple` → `AddOneAtomOccurrence` accumulates `(byte[], long)` records into `_spillBuffer` (a `List<>`)
- When `_spillBufferBytes >= _chunkBufferBytes` (1 GB), calls `SpillOneChunk` synchronously
- `SpillOneChunk` calls `buffer.Sort(comparison)` (introsort via `Comparison<T>` delegate), then writes records to disk
- Parser thread does not advance until `SpillOneChunk` returns

The `Comparison<T>` delegate in particular is worth noting: every comparison incurs a delegate-indirect call, defeating JIT inlining of the comparison body. SequenceCompareTo itself is SIMD-vectorized, but the dispatch overhead is not.

## Candidate mitigations

Listed cheapest-first; not mutually exclusive.

1. **Struct `IComparer<T>` instead of `Comparison<T>` delegate.** Switches dispatch from indirect-call to inlinable virtual via generic devirtualization. Typical 10-25% speedup on the sort phase. Single-commit change. No architectural impact.

2. **Parallel sort within the chunk.** Split the 1 GB buffer into N partitions (N = `Environment.ProcessorCount`), sort each on a worker thread, then k-way merge in memory. Wins ~3-4× sort time on multi-core (M5 Max has 14 cores). Reduces but does not eliminate the parser pause; eliminates *most* of it.

3. **Pipelined spill (double-buffered).** The structural fix. Parser fills buffer A; on threshold, hand A to a worker thread for sort+write; parser begins filling buffer B; when B fills and A is done, ping-pong. Parser never pauses for sort. Cost: working memory doubles (2 GB at 1 GB chunk size — trivial on hosts with 128 GB RAM). Synchronization is one atomic buffer-swap per spill, not per-record. Eliminates the per-spill pause entirely.

4. **Combination: pipelined spill + parallel sort.** Worker thread does parallel sort of its buffer; parser fills the other. Maximum overlap; both threads near full utilization. Most ambitious; probably highest absolute throughput.

The natural sequencing is **(3)** as the structural answer — it eliminates the parser-blocking property regardless of sort cost. **(1)** is a cheap independent win that compounds with (3). **(2)** becomes redundant once (3) is in place (sort happens off-parser-thread anyway). **(4)** is overkill until the worker thread itself becomes the bottleneck.

## Why this matters beyond throughput

Two secondary effects:

1. **Latency consistency.** The current 10-second pauses produce a bursty rate profile (visible in JSONL `triples_per_sec_recent`). Pipelined spill produces smooth rate. Better signal for monitoring; cleaner external comparisons.

2. **Composability with future ingest pipelines.** A pipelined ingest naturally extends to multi-stage pipelines (parse → buffer → sort → compress → write) with stages on separate threads. The serial-threaded design forces all-or-nothing changes; pipelined design composes incrementally.

## References

- `src/Mercury/Storage/SortedAtomBulkBuilder.cs` — `AddOneAtomOccurrence` + spill control
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `SpillOneChunk` (the sort + write surface)
- `docs/limits/external-merge-intermediate-disk-pressure.md` — sibling limit on the same merge phase
- 2026-05-03 cycle 6 21.3 B run — the surfacing observation (sort-burst recent-rate drop to ~21K /sec)
- Commit `4b7663c` — chunk size 256 MB → 1 GB (which made the pause visible)
- Commit `07102cb` — chunk-buffer constant unification
