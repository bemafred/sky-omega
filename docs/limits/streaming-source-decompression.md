# Limit: Intermediate uncompressed N-Triples / Turtle as workflow inefficiency

**Status:**        Latent
**Surfaced:**      2026-04-25, during Phase 6 21.3B Wikidata bulk-load. The 3.1 TB uncompressed `latest-all.nt` source file was contrasted with the 160 GB compressed `.bz2` Wikimedia dump — 2.94 TB of avoidable disk overhead was made visible by the contrast with the working state of the run.
**Last reviewed:** 2026-04-26
**Promotes to:**   ADR when any of (a) a target deployment has constrained disk and the 2.94 TB decompressed-intermediate overhead becomes load-bearing, OR (b) a workflow ships compressed-only (no uncompressed form available — common for cloud-distributed datasets), OR (c) measured BZip2 decompression throughput in `Mercury.Compression` is shown to bottleneck the parse pipeline at Mercury's per-second triple target

## Phase 7 source-format recommendation: `.ttl.bz2`

**For all Phase 7 gradient runs and bulk-load work, the canonical source artifact is `latest-all.ttl.bz2`** (~114 GB, present locally at `~/Library/SkyOmega/datasets/wikidata/full/`). The `.nt` form is legacy and should be retired from gradient methodology.

The recommendation rests on three measurements already on record, surfaced here because they were not previously registered as a workflow conclusion:

| Format | Throughput (100M-1B) | Bytes/triple on disk | Source artifact size |
|---|---:|---:|---:|
| `latest-all.nt` | 331 K/sec (1B run, 1.7.22, 2026-04-19) | ~150 | 3.1 TB uncompressed |
| `latest-all.ttl` | 292 K/sec (100M run, 1.7.23, 2026-04-20) | ~25 (prefix-abbreviated) | 912 GB uncompressed / **114 GB `.bz2`** |

Wall-clock parse throughput is ~12% slower for Turtle (parser pays for prefix resolution and `;`/`,` continuation handling). **This is dominated by two larger effects in the opposite direction:**

1. **Disk I/O per triple is ~6× lower** for Turtle because the source is far more compact (`wd:Q42` vs the full IRI). On any disk-bound or I/O-mixed workload, this advantage compounds.
2. **Source artifact size is ~28× smaller compressed** (114 GB `.ttl.bz2` vs 3.1 TB uncompressed `.nt`). Combined with streaming decompression (mitigation 3 below), this collapses the disk-staging cost from "cannot fit on a 4 TB SSD" to "fits in 1.5% of a consumer SSD."

The composability is sharp: **streaming `.bz2` decompression + Turtle compactness + `--limit` = gradient runs from a 114 GB single source artifact, no staging cost, any scale on demand.** That's the disk-preparation priority for Phase 7 resolved by one piece of infrastructure plus a workflow recommendation.

Source: [`docs/validations/turtle-at-wikidata-scale-2026-04-20.md`](../validations/turtle-at-wikidata-scale-2026-04-20.md), [`docs/validations/parser-at-wikidata-scale-2026-04-17.md`](../validations/parser-at-wikidata-scale-2026-04-17.md).

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

1. **Document the workflow.** Update Mercury CLI documentation to recommend `latest-all.ttl.bz2` as the canonical source artifact for gradient runs and Phase 7 work, with `mercury --bulk-load latest-all.ttl.bz2 --limit N` as the standard invocation pattern (once `Mercury.Compression` is wired). The `.nt` and uncompressed `.ttl` forms are legacy and need not be staged. Workflow change only — no code. Lowest-effort, highest-leverage change. Stops users from creating intermediate uncompressed files unnecessarily and establishes a single source-of-truth artifact.
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

Latent at Phase 6 (21.3 B Wikidata, M5 Max with 7.3 TB SSD). The 3.1 TB intermediate fit comfortably (40% of capacity), so the workflow inefficiency was *visible but not load-bearing.*

**Phase 7 changes the picture.** Gradient runs across multiple scales, parallel Cognitive and Reference profile work, plus the existing 2.5 TB occupied by `wiki-21b-ref`, push the disk budget close to binding. The `.ttl.bz2`-as-canonical-source recommendation above resolves this without code changes; the streaming-decompression wiring (mitigation 3) closes it out architecturally. Together, these promote the entry from "characterized but not load-bearing" to "first-priority Phase 7 enabling infrastructure."

## References

- `RdfEngine.LoadFileAsync` source: `src/Mercury/RdfEngine.cs:114-136` — current implementation with auto-detection
- `RdfEngine.WrapWithDecompression` source: `src/Mercury/RdfEngine.cs:278-285` — the dispatch
- `RdfFormatNegotiator.FromPathStrippingCompression` — extension-based format detection
- `Mercury.Compression` package — separate from BCL-only core, hosts BZip2 (and could host Zstd/LZ4 in future)
- [Phase 6 validation doc](../validations/) — concrete numbers for the 3.1 TB / 160 GB ratio at Wikidata scale (lands on Phase 6 completion)
- Sister limits entries:
  - [bulk-load-memory-pressure](bulk-load-memory-pressure.md) — composes (smaller working set leaves more disk for compressed-source workflows)
  - [sorted-atom-store-for-reference](sorted-atom-store-for-reference.md) — composes (faster parse pipeline shifts where the bottleneck sits)
