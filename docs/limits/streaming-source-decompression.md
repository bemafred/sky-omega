# Limit: Intermediate uncompressed N-Triples / Turtle as workflow inefficiency

**Status:**        Latent
**Surfaced:**      2026-04-25, during Phase 6 21.3B Wikidata bulk-load. The 3.1 TB uncompressed `latest-all.nt` source file was contrasted with the 160 GB compressed `.bz2` Wikimedia dump — 2.94 TB of avoidable disk overhead was made visible by the contrast with the working state of the run.
**Last reviewed:** 2026-04-25
**Promotes to:**   ADR when any of (a) a target deployment has constrained disk and the 2.94 TB decompressed-intermediate overhead becomes load-bearing, OR (b) a workflow ships compressed-only (no uncompressed form available — common for cloud-distributed datasets), OR (c) measured BZip2 decompression throughput in `Mercury.Compression` is shown to bottleneck the parse pipeline at Mercury's per-second triple target

## Description

Mercury's bulk-load entry point (`RdfEngine.LoadFileAsync`) **already supports streaming decompression** for `.gz` extensions via BCL's `System.IO.Compression.GZipStream`. The parser receives decompressed bytes; the `.gz` archive is never materialized. BZip2 support is gated on the `Mercury.Compression` package (separate from the BCL-only Mercury core, per Sky Omega's substrate-independence ethos).

Despite this support, **Phase 6 ran against a 3.1 TB uncompressed `latest-all.nt`** because that's the form the file existed in on disk at the time of the run. The decompressed intermediate was a one-time setup cost paid earlier; running directly from the compressed source would have avoided that cost, and the disk it consumed.

**The trade-off, made concrete:**

| Asset | Size | Lifecycle | Avoidable? |
|---|---:|---|---|
| Source `.bz2` Wikidata dump | ~160 GB | Permanent (input artifact) | No |
| Decompressed `latest-all.nt` (Phase 6 input) | **3.1 TB** | One-time intermediate, used during parse only | **Yes** — replace with streaming decompression |
| Mercury working state at peak (sorter chunks + indexes + atom store) | ~2.5 TB | Phase-bounded, auto-cleaned at end | No (architectural) |

The 3.1 TB intermediate is **2.94 TB more disk than the source dump itself.** On a 7.3 TB-class consumer SSD, that's 40% of total capacity dedicated to a one-time intermediate that exists only because of historical workflow, not because of any architectural need.

## Decompression throughput required

Mercury's parse rate gives a tight upper bound on the decompression throughput needed. At the Phase 6 sustained ~80-95 K triples/sec average, with N-Triples averaging ~150 bytes/triple:

- **Source bytes consumed: ~12-14 MB/sec**

This is well below any modern streaming decompressor:

| Algorithm | Typical throughput | Margin over 12 MB/s | Notes |
|---|---:|---:|---|
| GZip (BCL `GZipStream`) | ~200-500 MB/s | 17-42× | Hardware-accelerated where available; already integrated. |
| BZip2 (Mercury.Compression) | ~10-50 MB/s | 0.8-4× | Tight on the slow end. **The measurement we need.** |
| Zstd (not in BCL) | ~500-2 GB/s | 42-167× | Best speed/ratio, but breaks BCL-only core invariant — would land in `Mercury.Compression`. |
| LZ4 (not in BCL) | ~1-5 GB/s | 83-417× | Fastest, but ratio worse; less suitable for archive distributions. |

**The only candidate that's tight is BZip2.** A poorly-tuned or pure-managed BZip2 implementation in Mercury.Compression *could* slow the parse pipeline by 10-40% at Wikidata scale; a fast native-backed implementation would be fine. The current Mercury.Compression package is unmeasured at the per-second-throughput-required scale; this entry's primary action item is **measure that** before any architectural change.

## Zero-GC compatibility

GZipStream in BCL is not zero-allocation by default — it allocates an internal inflate buffer at construction. But the pattern Mercury's bulk-load already uses (caller-provided `Span<byte>` reads into a pre-allocated buffer) wraps cleanly:

```csharp
using var fileStream = File.OpenRead(path);
using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
var readBuffer = new byte[64 * 1024];
int bytesRead;
while ((bytesRead = gzipStream.Read(readBuffer)) > 0)
{
    parser.Process(readBuffer.AsSpan(0, bytesRead));
}
```

The internal inflate buffer in `GZipStream` is allocated once at construction (LOH or Gen 0 depending on size — typically ~128 KB), not per-read. **The bulk-load's "zero per-triple allocation" invariant is preserved.** True zero-allocation requires inlining the decompressor, which is not justified.

For BZip2 via `Mercury.Compression`: the implementation's allocation behavior is currently unknown and should be characterized as part of the measurement work.

## Candidate mitigations (not yet characterized)

Ordered by leverage and effort:

1. **Document the workflow.** Update Mercury CLI documentation to explicitly recommend streaming-decompression usage: `mercury --bulk-load latest-all.nt.gz` and `mercury --bulk-load latest-all.ttl.bz2` (when `Mercury.Compression` is wired). Workflow change only — no code. Lowest-effort, highest-leverage change. Stops users from creating intermediate uncompressed files unnecessarily.
2. **Measure BZip2 throughput via `Mercury.Compression`.** Microbenchmark: 1 GB compressed sample → measure end-to-end decompression bandwidth in the bulk-load pipeline context (with the parser as consumer). Confirms whether BZip2 is bottlenecking or has comfortable margin. Trivial to implement, definitive answer.
3. **Wire BZip2 detection into `RdfFormatNegotiator.FromPathStrippingCompression`.** Currently throws `NotSupportedException` with a pointer to `Mercury.Compression`. Could conditionally register the package's decoder if loaded. Small but breaks BCL-only core invariant unless implemented as an opt-in plugin model. Architectural decision, not pure measurement.
4. **Add Zstd or LZ4 support in `Mercury.Compression`.** Only justified if BZip2 measurements show it as binding *and* a significantly-faster alternative becomes a real workflow request. Currently speculative.
5. **Pipelined decompression worker thread.** Decompress on one thread, parse on another, with a producer-consumer channel between. Hides decompression latency behind parsing. Only worthwhile if (3) shows BZip2 is binding. Adds threading complexity that the rest of the bulk-load pipeline avoids.

## Trigger condition

Promote to ADR when any of:

- Disk-constrained deployment (≤ 4 TB free) needs to ingest Wikidata-class datasets.
- A workflow ships compressed-only — e.g., a cloud blob store that returns `.gz` or `.bz2` without an uncompressed alternative.
- Measurement (2) above shows BZip2 throughput < 30 MB/sec sustained, putting it within ~2× of the parse-side consumption rate (creates a real bottleneck).
- The bulk-load pipeline gets a substantial CPU performance improvement (e.g., from removing the atom-store hash drift via SortedAtomStore) such that the source-read rate becomes binding.

## Current state

Latent at Phase 6 (21.3 B Wikidata, M5 Max with 7.3 TB SSD). The 3.1 TB intermediate fit comfortably (40% of capacity), so the workflow inefficiency was *visible but not load-bearing.* On smaller SSDs or with larger datasets it would become binding fast.

## References

- `RdfEngine.LoadFileAsync` source: `src/Mercury/RdfEngine.cs:114-136` — current implementation with auto-detection
- `RdfEngine.WrapWithDecompression` source: `src/Mercury/RdfEngine.cs:278-285` — the dispatch
- `RdfFormatNegotiator.FromPathStrippingCompression` — extension-based format detection
- `Mercury.Compression` package — separate from BCL-only core, hosts BZip2 (and could host Zstd/LZ4 in future)
- [Phase 6 validation doc](../validations/) — concrete numbers for the 3.1 TB / 160 GB ratio at Wikidata scale (lands on Phase 6 completion)
- Sister limits entries:
  - [bulk-load-memory-pressure](bulk-load-memory-pressure.md) — composes (smaller working set leaves more disk for compressed-source workflows)
  - [sorted-atom-store-for-reference](sorted-atom-store-for-reference.md) — composes (faster parse pipeline shifts where the bottleneck sits)
