# Limit: BZip2 source decompression is single-threaded

**Status:**        Latent
**Surfaced:**      2026-04-30, via the QLever-comparison conversation captured in `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`. The discussion of pipeline-shape trade-offs (Shape A two-pass over compressed input vs Shape B one-pass with staged intermediates) implicitly assumed parallel decompression; the assumption did not hold. The 2026-04-27 ADR-035 Phase 7a 1B Reference validation measured the actual throughput.
**Last reviewed:** 2026-04-30
**Promotes to:**   ADR for parallel BZip2 decompression once (a) a measured pipeline trace identifies decompression as the binding constraint at Phase 7c gradient runs, OR (b) a deployment surfaces where multi-core is available but I/O is not the bottleneck (cloud nodes, etc.).

## Description

Mercury 1.7.45's `BZip2DecompressorStream` (ADR-036, `src/Mercury/Compression/`) is a BCL-only single-threaded streaming bzip2 decoder. It is correct and zero-GC; the limit is not the implementation, it is the architecture: bzip2 is decoded as a single sequential stream of blocks, each ~900 KB compressed, decoded in order on one thread.

Measured at the 2026-04-27 1B Reference validation (`docs/validations/adr-035-phase7a-1b-2026-04-27.md`):

- **Decompression throughput:** ~33 MB/s steady-state from `latest-all.ttl.bz2`
- **Parser consumption:** ~8 MB/s of decompressed Turtle
- **Effective headroom:** ~4× — i.e. decompression is not currently the bottleneck on a single-NVMe path with one parser thread

Bzip2 is *block-structured by design*: each ~900 KB block has independent state (Huffman tables, MTF, BWT) and depends only on its own header for decoding. A parallel implementation walks the bitstream identifying block boundaries (the magic number `0x314159265359` marks each block's pi prefix), dispatches blocks to a worker pool, and concatenates the output in order. Reference implementations:

- `lbzip2` (Lacos): block-level parallelism, ~N× speedup on N cores up to I/O saturation.
- `pbzip2` (Gilchrist): same shape, slightly different scheduling.
- `mt-bzcat` (various): single-file parallel decoders sustaining 300–400+ MB/s on commodity hardware.

On the M5 Max (18 cores, NVMe), parallel decompression should saturate at the lower of (a) NVMe sequential read bandwidth (~14 GB/s — far above the bzip2 work), and (b) the BWT-inverse algorithmic ceiling on the available cores. Empirical estimate: 250–400 MB/s sustained on this hardware.

On the full Wikidata `latest-all.ttl.bz2` (~110 GB compressed), the wall-clock comparison:

- **Single-threaded** (current): 110 GB / 33 MB/s ≈ **55 minutes** of producer-side work
- **Parallel ceiling** (~300 MB/s on M5 Max): 110 GB / 300 MB/s ≈ **6 minutes**
- **Recovered wall-clock:** ~50 minutes per full-Wikidata run

## Trigger condition

This limit moves from Latent to Monitoring or Triggered when one of:

1. **Phase 7c gradient runs identify decompression as binding.** The trace currently shows 4× headroom over the parser; if Phase 7c work shifts the parser bottleneck (faster parsing, parallel parsing, atom-store improvements), the producer side could become binding without warning.
2. **A deployment with N cores >> I/O bandwidth surfaces.** Cloud blobs read from S3-equivalent are the prime example — multi-core is cheap, sequential read bandwidth is not.
3. **A use case where the source is bz2 and the wall-clock matters.** Currently `mercury --bulk-load latest-all.ttl.bz2` users absorb the 33 MB/s rate as a one-time cost; if a workflow re-loads the same source repeatedly (re-builds, alternate-profile builds), the cost becomes recurrent.

## Current state

`BZip2DecompressorStream` is BCL-only, zero-GC, correct against the bzip2 reference suite. ADR-036 closed Phase 7b on the *capability* axis: bz2 streams are decoded without staging an intermediate uncompressed file. The *throughput* axis was measured but not optimized — 33 MB/s with 4× headroom is "good enough for now," not "good enough at any scale."

The current implementation reads the bzip2 bitstream linearly, decoding one block at a time. The architectural pivot to parallel is straightforward but non-trivial: a block-boundary scanner that dispatches blocks to a worker pool, with an in-order concatenation gate at the output. The BWT-inverse hot path (Adler/Malbrain) is the per-block algorithmic core; it is single-threaded today but inherently parallelizable across blocks.

## Candidate mitigations

In rough order of cost / payoff:

1. **Block-level parallel decoder.** Add a `ParallelBZip2DecompressorStream` alongside the existing class. Worker pool size configurable (default `Environment.ProcessorCount / 2` to leave cores for the parser). API-compatible with the existing class so the bulk-loader can swap implementations via configuration. Estimated 1-2 days at AI pace; the BCL primitives (`Channel<T>`, `Task.Run`, `MemoryPool<byte>`) compose cleanly.

2. **Lazy block scan.** Read the input forward, identify block boundaries on the producer thread, dispatch blocks to a worker pool. Output writer enforces in-order concatenation. Lower memory ceiling than (1) because workers can drop their decoded buffer once it's consumed downstream.

3. **Profile-driven decision.** Measure pipeline trace at Phase 7c. Decide which mitigation to implement based on observed bottleneck. The point of the limits register is to not pre-commit to a fix when the cost/benefit isn't yet binding.

## Why this matters beyond the immediate

The 15-25h trajectory implicitly budgets for SSD-saturated sequential reads through the pipeline. Single-threaded bz2 caps the producer side at well below NVMe throughput. As Phase 7c performance rounds compose (sorted atom store, prefix compression, parallel rebuild proper, parser optimizations), the bottleneck WILL shift to the producer side at some point. Naming the limit now means we don't get blindsided when the trace identifies it.

It also reframes the recent architectural-shape discussion (Shape A vs Shape B): Shape A (two-pass over compressed input) costs more decompression wall-clock if decompression is single-threaded; Shape B (one-pass with staged intermediates) trades disk I/O for fewer decompression passes. The trade-off math depends on bz2 throughput. **Today's math is wrong** for any analysis that assumes parallel decompression is in place.

## References

- `src/Mercury/Compression/BZip2DecompressorStream.cs` — current single-threaded implementation
- ADR-036 (mercury) — bzip2 streaming substrate decision
- `docs/validations/adr-035-phase7a-1b-2026-04-27.md` — measured 33 MB/s throughput
- `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md` — surfacing memo
- `docs/limits/streaming-source-decompression.md` — sibling entry covering source-format choice (the canonical bz2 vs nt vs ttl decision); this entry is about parallelism within the chosen bz2 format
