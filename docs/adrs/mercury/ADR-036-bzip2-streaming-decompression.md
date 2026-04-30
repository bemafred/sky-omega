# ADR-036: BZip2 Streaming Decompression

## Status

**Status:** Accepted — 2026-04-27. Phase 1 (single-threaded decompressor) Completed 2026-04-27 (validated end-to-end at 1 B Reference, [adr-035-phase7a-1b-2026-04-27.md](../../validations/adr-035-phase7a-1b-2026-04-27.md)). Phase 2 (block-parallel decompressor) Completed 2026-04-30 (commit `a11f873`); measured 2.62× speedup ceiling on 50 MB / 58-block fixture, scanner-bound (commits `b39f186`, `0c3b1ae`).

## Context

Phase 7 performance rounds gate on validation runs at 1 B / 10 B / 21.3 B Reference scale. Each run requires a fresh bulk-load — exercising `LoadProgress`, `RebuildProgress`, and atom-store metrics requires loading from a source artifact, not querying an existing store. The conservative-disk path for that source artifact is `latest-all.ttl.bz2` (the canonical Wikidata distribution): 114 GB compressed, ~28× smaller than the uncompressed ~3.2 TB N-Triples form, ~6× less disk I/O per triple loaded.

GZip streaming has been supported transparently in `RdfEngine` since the early bulk-load work (the `.gz` extension dispatch in `LoadFileAsync`). BZip2 has not — Mercury today cannot ingest `latest-all.ttl.bz2` without an external decompression tool staging the uncompressed dump. At Wikidata scale that staging is operationally unworkable.

Three implementation paths were considered. **Two were rejected as substrate-debt:**

- **SharpZipLib NuGet in `Mercury.Cli` only.** Rejected: makes the CLI the de-facto bulk-load entry point and silently externalizes a substrate-level capability into dev tooling. Mercury core's BCL-only claim becomes aspirational. Future format work (zstd, lz4) inherits the same wrong shape. Once shipped, every test fixture, every CLI integration, every `--bulk-load .bz2` semantic gets re-cut when the substrate-level implementation finally lands.
- **P/Invoke to `libbz2`.** Rejected: an external runtime dependency for a capability that should be intrinsic to the substrate. ADR-027 / CLAUDE.md frame Sky Omega's substrates as BCL-only specifically to avoid this — hardware accessed via P/Invoke is acceptable when the alternative is "no access at all" (Metal, CUDA, EventPipe); compression algorithms have no such barrier. A pure-C# implementation removes a deployment dependency, removes a per-platform build/test matrix, and removes the "what version of libbz2 is on the operator's machine" failure mode.

**The substrate-level path is the only correct one:** a pure-C# BCL-only BZip2 decompressor in `src/Mercury/Compression/`, integrated transparently into `RdfEngine`'s extension dispatch alongside the existing `.gz` path.

This ADR scopes that implementation. Encoding (compression) is deferred — Wikidata distributes bz2; Mercury reads it. No consumer for bz2 production exists.

### Why a `Compression/` peer subsystem rather than a separate project

Mercury today has peer subsystems under `src/Mercury/`: `NTriples/`, `Rdf/`, `RdfXml/`, `Sparql/`, `Storage/`, `Turtle/`. Compression fits the same shape — a self-contained, BCL-only algorithm package consumed by `RdfEngine`'s loader. A separate `Mercury.Compression` project adds csproj overhead and cross-project version coupling for no semantic benefit; the algorithm has no external dependencies and no consumers outside Mercury. Subdirectory.

If a future consumer outside Mercury needs the decompressor (e.g. a future `Minerva` use case for compressed model weights), promotion to a separate project is cheap and non-breaking — the namespace stays.

## Decision

### 1 — `BZip2DecompressorStream : Stream`

A read-only forward `Stream` derivation in `SkyOmega.Mercury.Compression` that wraps an underlying compressed stream and emits the decompressed byte stream on `Read`. The single integration surface — every consumer reads through `Stream`, no specialized API.

```csharp
namespace SkyOmega.Mercury.Compression;

public sealed class BZip2DecompressorStream : Stream
{
    public BZip2DecompressorStream(Stream compressed, bool leaveOpen = false);

    // Standard Stream surface: Read, ReadAsync, CanRead=true, CanSeek=false,
    // CanWrite=false, Length/Position throw NotSupportedException.
}
```

Rationale: streaming-only matches the consumer's access pattern (parser reads forward), and `CanSeek = false` mirrors `GZipStream`'s shape — consumers that mistakenly attempt random access fail loudly at construction-of-intent, not silently mid-load.

### 2 — Algorithm components, all BCL primitives only

The bzip2 stream format (Seward 1996; reference: `bzip2-1.0.8` source) decomposes into:

- **Bit-level reader** over `Stream` — consumes bytes, exposes a forward `ReadBits(int count)` API. ~50 lines.
- **Huffman decoder** — bzip2 uses 1–6 dynamic Huffman tables per block, switched every 50 input symbols by a stored selector sequence. Canonical-code construction; bit-by-bit decode (the small alphabet — at most 258 symbols — does not justify a table-based decoder). ~300 lines.
- **Move-to-front (MTF) inverse** — maintains a 256-entry list; each input index `k` outputs the byte at position `k`, then moves it to position 0. Trivial. ~40 lines.
- **Run-length encoding inverses** — bzip2 applies RLE1 (pre-BWT byte-level RLE for very long runs) and RLE2 (post-MTF symbolic RLE for runs of zero). Two distinct algorithms; both ~80 lines each.
- **Burrows-Wheeler Transform inverse** — the load-bearing piece. The fast inverse builds a `T[]` array where `T[i]` is the index of the position whose preceding character is the i-th occurrence of its rune in the sorted suffix array. Walk from the stored origin pointer, emitting each preceding character. `O(n)` time, `O(n)` space (one `int[]` sized to the block length). Maximum block size is 900,000 → 3.6 MB transient buffer per active stream. ~150 lines.
- **CRC32 (non-reflected variant)** — bzip2 uses CRC-32-IEEE polynomial `0x04C11DB7` *without* bit reflection. .NET's `System.IO.Hashing.Crc32` is reflected (matches gzip/zlib); not compatible. Fresh implementation: precomputed 256-entry table, ~50 lines. Required for both per-block CRC verification and the combined stream CRC.
- **Multi-stream support** — bzip2 files may be catenations of independent streams (each starts with `'BZh'` magic). Reader continues across boundaries until EOF. ~40 lines integration.

Total estimate: ~1500–2200 lines of substantive code, ~200–400 lines of in-stream state management (block reassembly, buffer pump).

### 3 — Memory profile

Per active stream, peak resident:

- Input bit buffer: 64 KB
- BWT inverse `int[]` array: 3.6 MB (one block at the 900 KB max block size)
- MTF list: 256 bytes
- Output ring buffer: 64 KB (RLE1 may emit up to 255-byte runs from each 4-byte input cluster, so a small smoothing buffer is appropriate)
- Huffman table state: ~10 KB
- **Total peak: ~3.7 MB per stream**, ~64 KB after construction before first block reads.

Steady-state read path is zero-allocation by construction: all buffers are owned by the stream instance, no per-`Read` allocations. Validated by an allocation-counting test (`GC.GetTotalAllocatedBytes` delta over a sustained Read loop).

### 4 — `RdfEngine` integration via extension dispatch

`RdfEngine.LoadFileAsync` already dispatches on extension for `.gz`. Extend the same dispatch:

```csharp
Stream OpenSource(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".gz"  => new GZipStream(File.OpenRead(path), CompressionMode.Decompress),
    ".bz2" => new BZip2DecompressorStream(File.OpenRead(path), leaveOpen: false),
    _      => File.OpenRead(path),
};
```

`mercury --bulk-load latest-all.ttl.bz2 --limit 1000000000` works transparently. No flag, no opt-in — the extension is the contract, mirroring the existing `.gz` semantics.

Format auto-detection (the `.ttl.bz2` → Turtle case) extends `RdfFormat` detection to look at the *inner* extension after stripping `.bz2`/`.gz`. The existing `.ttl.gz` path already handles this; the same logic generalizes.

### 5 — CRC verification is mandatory, not optional

Every block carries a 32-bit CRC of the decompressed block content; the stream carries a combined CRC at the end. The decompressor verifies both — a CRC mismatch throws `InvalidDataException` at the point of detection. **No "ignore CRC" flag.** A substrate that silently accepts corrupt data is unacceptable; if a workload genuinely needs to ingest a partially-truncated bz2 (rare; the operational answer is "re-download"), it can wrap the stream and catch the exception explicitly.

### 6 — Test strategy: reference fixtures, frozen

Test data is generated out-of-band from a canonical `bzip2` tool and committed under `tests/Mercury.Tests/Compression/Fixtures/`. Each fixture is a triple: (uncompressed source, bz2 file, SHA-256 of uncompressed). Test cases:

- **Empty input** (`bzip2 < /dev/null > empty.bz2`) — header-only stream, decompressor produces zero bytes
- **Single byte** — exercises minimum-block-size edge
- **One full block** (~900 KB random data, repeated bytes, ASCII text) — three fixtures covering different entropy regimes
- **Multi-block** (~3 MB random, ~3 MB ASCII)
- **Multi-stream** (two catenated bz2 files)
- **Large stream** (50 MB source) — validates streaming behavior, checks zero-alloc steady state via `GC.GetTotalAllocatedBytes`
- **Corruption** (flip one byte in a known-good fixture) — must throw `InvalidDataException`, not silent corruption
- **Truncation** (cut the stream mid-block) — must throw at end-of-stream

Fixtures are checked in. Regenerating them requires a documented procedure (out-of-band `bzip2` invocation) and a deliberate commit — not a build-time step.

End-to-end validation: after Phase 5 of the implementation plan (RdfEngine integration), run the existing `wiki-1m-ref` and `wiki-10m-ref` gradient bulk-loads from a freshly-bzip2-compressed N-Triples file, comparing output stores byte-for-byte against runs from the uncompressed source. Identical store output is the correctness criterion.

### 7 — Decompression-only scope

`BZip2CompressorStream` (encoding) is *not* in scope. No consumer exists today — Mercury reads bz2, doesn't produce it. Adding compression doubles the algorithmic scope (BWT forward, suffix-array sort, multi-table Huffman code generation) for capability nobody asked for. Promote to a separate ADR if a consumer surfaces.

### 8 — Public API surface stays minimal

Only `BZip2DecompressorStream` is `public`. All algorithm components — bit reader, Huffman decoder, MTF, RLE inverses, BWT inverse, CRC32 — are `internal sealed`. No reusable algorithm primitives leak; the substrate's compression surface is one class. This keeps future maintenance scoped: refactoring the BWT inverse cannot break a downstream consumer because there is no downstream consumer of the BWT inverse.

## Consequences

### Positive

- **Wikidata-scale validation runs become disk-conservative.** `mercury --bulk-load latest-all.ttl.bz2 --limit N` runs end-to-end without staging the uncompressed dump. Phase 7c rounds, ADR-035 close-out, ADR-034 validation all unblock.
- **Substrate independence preserved.** Mercury core remains BCL-only; no NuGet creeps into the dependency graph; no `libbz2` runtime dependency on operator machines. The semantic-sovereignty claim stays real.
- **`Compression/` becomes the home for future format work.** zstd, lz4, brotli — same architectural shape, same test discipline. The investment compounds.
- **Operational simplicity.** One artifact in, one artifact out. No staging script, no out-of-band decompression tool, no "did you remember to `bunzip2 -k` first" footgun.
- **Forward-stream-only contract is honest.** No misleading `Position`/`Length` semantics on a decompressing stream; consumers that need random access work against the decompressed output explicitly.
- **CRC enforcement protects every downstream substrate from silent corruption.** A truncated bz2 fails loudly at decompress time, not as bizarre N-Triples parser errors 50 GB into a load.

### Negative

- **~1500–2200 lines of new substantive code + ~10 KB of test fixtures.** Larger than dependency-add alternatives; correct relative to the problem. The test-fixture maintenance is bounded — fixtures are frozen and re-generated only on documented schema changes.
- **One-time correctness validation cost.** The BWT inverse must be validated against real-world bz2 streams at scale, not just synthetic fixtures. Phase 5 of the implementation plan covers this; the cost is unavoidable for any decompressor that intends to be load-bearing.
- **No compression path.** A future workload requiring bz2 production (writing reference dumps?) needs a separate ADR. Acceptable — no such workload exists.

### Risks

- **BWT inverse correctness on edge cases.** Block-size-1 inputs, all-same-byte inputs, randomized-bit blocks (the rarely-seen randomization flag in pre-1.0 bzip2 streams) all need explicit fixture coverage. The reference implementation handles these; ours must too. **Mitigation:** the reference-fixture test suite covers each edge case; fixtures are generated from `bzip2-1.0.8` and frozen.
- **Performance against the reference implementation.** A naive C# port can land 2–3× slower than `libbz2` on the BWT inverse hot path. At Wikidata scale (114 GB compressed → 3.2 TB uncompressed) that's the difference between a 4-hour decompression and an 8–12-hour decompression, on top of the 65–85 hour bulk-load. **Mitigation:** the BWT inverse is the only hot path; benchmark it against `libbz2` (out-of-band, on the same hardware) and tune until within 1.5× — the gap is dominated by `Span<byte>` vs raw pointer access, addressable with `unsafe` and `MemoryMarshal.Cast`. If a 1.5× target proves unreachable in pure C#, document the gap and accept it; the alternative is the substrate-debt path that this ADR rejects.
- **CRC32 polynomial subtlety.** bzip2's non-reflected CRC32 is structurally distinct from the reflected variant in `System.IO.Hashing.Crc32`. Confusing the two produces silent-corruption-undetected. **Mitigation:** dedicated CRC test fixtures verifying byte-exact CRC values from the reference implementation; type-named `BZip2Crc32` so it cannot be accidentally substituted for the reflected variant.
- **Multi-stream catenation is rare in practice but valid.** Most `latest-all.ttl.bz2` distributions are single-stream; some operational pipelines produce catenations. **Mitigation:** explicit multi-stream fixture in the test suite; the reader must continue reading after each stream's terminator without consumer-visible discontinuity.
- **Memory allocation regression risk.** The stream is designed to be zero-alloc on the steady-state read path. Future refactoring could regress this silently — the BWT inverse's `int[]` is the one large buffer; any helper that allocates per-block on the hot path is a regression. **Mitigation:** allocation-counting test in CI gates against regressions; documented as part of the steady-state contract.

## Implementation plan

**Phase 1 — Foundations**
- Bit-level reader (`internal sealed class BitReader`) + tests covering boundary alignment, multi-byte reads, end-of-stream behavior.
- BZip2 CRC32 (non-reflected variant) + tests against reference values from `bzip2-1.0.8`.
- Move-to-front inverse + tests against hand-computed sequences.
- Run-length encoding inverses (RLE1, RLE2) + tests.

**Phase 2 — Algorithmic core**
- Huffman decoder + tests with hand-built code tables and known input/output pairs.
- BWT inverse + tests against small hand-computed BWT outputs from the original Burrows-Wheeler 1994 paper.
- Block reader: integrates bit reader, Huffman, MTF, RLE, BWT into the per-block decode pipeline. Tests against single-block fixtures.
- Status: Proposed → Accepted after Phase 2 lands and single-block fixtures decode byte-exact.

**Phase 3 — Stream integration**
- `BZip2DecompressorStream : Stream` shell. Multi-block reading. Multi-stream catenation. Stream-level CRC verification.
- Tests against multi-block, multi-stream, large-stream fixtures.
- Allocation-counting test for the steady-state read path.
- Corruption + truncation tests.

**Phase 4 — RdfEngine integration**
- Extension dispatch in `RdfEngine.LoadFileAsync` for `.bz2` and the inner-format detection (`.ttl.bz2` → Turtle, `.nt.bz2` → N-Triples).
- Tests: a 1 M-triple bz2 fixture loads byte-identical to the same source uncompressed.

**Phase 5 — Real-world validation**
- Out-of-band: download `latest-all.ttl.bz2`. Compute SHA-256 of the source.
- `mercury --bulk-load latest-all.ttl.bz2 --limit 1000000` → compare to the same data loaded from the staged uncompressed N-Triples.
- Repeat at 10 M, 100 M, 1 B (limited by disk for the uncompressed-comparison baseline; full 21.3 B can be validated by stream-only run against the existing `wiki-21b-ref` store, comparing post-bulk metrics for byte-exact-equivalent ingestion).
- Bench BWT inverse on a 100 MB representative N-Triples chunk; document throughput vs `libbz2` reference.
- Status: Accepted → Completed after a 1 B bz2-streamed bulk-load matches a 1 B uncompressed bulk-load byte-for-byte.

**Phase 6 — Documentation**
- `docs/architecture/technical/compression.md` documenting the algorithm, the API contract, the CRC variant, and the test-fixture regeneration procedure.
- `docs/limits/streaming-source-decompression.md` updated to link this ADR as the resolution.

## Phase 2 — Block-parallel decompression (added 2026-04-30)

### Motivation

The single-threaded decompressor (Phase 1) was originally framed as the architectural enabler for "two-pass-over-source" pipeline shapes — re-decompressing the source on a second pass would be cheap if decompression were fast enough. The limits-register entry `bz2-decompression-single-threaded.md` projected a ~9× ceiling for parallel decompression (33 MB/s single-threaded → ~300 MB/s parallel-block decode on M5 Max).

### Decisions

**1. Pipeline shape.** A producer thread runs `BZip2BlockBoundaryScanner` to find bit-aligned block boundaries; N worker threads each own a `BZip2BlockReader` and decode independent blocks pulled from a bounded `Channel<WorkItem>`. Results land in a lock-protected `PriorityQueue<BlockResult, int>` keyed by ordinal; the consumer drains in ordinal order, accumulates per-block CRC into the stream-combined CRC, and verifies against the trailer at end-of-stream.

**2. Worker-count default.** `Math.Max(1, (Environment.ProcessorCount * 4) / 5)` — ~14 on M5 Max. Originally chosen as ~80% of cores under the projected ~9× ceiling. Measurements (see below) revised the *useful* ceiling to ~4 workers; the default is preserved for forward-compatibility but workloads that care should pin to 4.

**3. Single-stream input only.** Concatenated bzip2 streams (handled transparently by the single-threaded decompressor) are out of scope for the parallel path. Wikidata's `latest-all.ttl.bz2` is single-stream; this is sufficient for production.

### Measured throughput (Epistemics gate, 2026-04-30)

Three measurements decomposed the convert-path stages on a 50 MB / 6.6 MB-compressed / ~58-block fixture (`/tmp/multiblock-large.txt.bz2`, generated locally, not committed):

**Direct decompression** (`LargeFixtureMeasurement`, commit `b39f186`):

| Configuration | wall (ms) | output MB/s | speedup |
|---|---:|---:|---:|
| single-threaded | 1685.5 | 29.7 | 1.00× |
| parallel (1 workers) | 1713.1 | 29.2 | 0.98× |
| parallel (2 workers) | 914.9 | 54.7 | 1.84× |
| parallel (4 workers) | 642.6 | 77.8 | **2.62×** |
| parallel (8 workers) | 641.4 | 77.9 | 2.63× |
| parallel (14 workers) | 644.0 | 77.6 | 2.62× |

**Contention trace** (`ParallelBZip2ContentionTrace`, commit `b39f186`) refuted the memory-bandwidth-contention hypothesis. Per-block decode time stayed flat (29.25 ms at N=1, 31.31 ms at N=14, 1.07× — within noise). Workers idled 80% of wall-clock at N=14: parallel-efficiency dropped from 99.4% to 20.3%. The bottleneck is the **producer**: `BZip2BlockBoundaryScanner` walks the compressed bitstream bit-by-bit looking for 48-bit magic sequences and feeds at ~90 blocks/sec — workers can consume at ~466 blocks/sec, so beyond 4 workers everyone idles.

**Convert-path attribution** (`ConvertPathThroughputMeasurement`, commit `0c3b1ae`) decomposed Turtle → N-Triples by isolating each stage:

| Configuration | wall (s) | source MB/s | triples/sec |
|---|---:|---:|---:|
| single-threaded bz2 → parser → NT | 3.22 | 15.5 | 207K |
| parallel (4) bz2 → parser → NT | 1.53 | 32.8 | 438K |
| pre-decompressed → parser → NT | 1.41 | 35.5 | 474K |

Pre-decompressed sets the parser+writer ceiling at 35.5 MB/s. Parallel-(4) bz2 reaches 32.8 MB/s (within 7% of ceiling — bz2 no longer the bottleneck). Single-threaded bz2 reaches 15.5 MB/s (44% of ceiling — bz2 IS the bottleneck, costing ~57% of convert wall-clock).

### Workload-separated verdict

The parallel decoder's value is workload-dependent — same component, opposite verdict per workload:

| Workload | Downstream rate | Single-threaded sufficient? | Parallel verdict |
|---|---:|:---:|---|
| Convert (Turtle → NT) | ~35 MB/s | No (30 MB/s output is cap) | **Useful — cuts ~57% of wall-clock** |
| Bulk-load (parser + atom intern + spill) | ~17.5 MB/s | Yes (30 MB/s exceeds) | Irrelevant — single-threaded sufficient |

The earlier framing — *"parallel bz2 is the architectural enabler that makes two-pass-over-source viable"* — was based on the unmeasured ~9× projection. The measured 2.62× ceiling, combined with the parser-bound 17.5 MB/s on the bulk-load path, removes parallel bz2 from the bulk-load architectural conversation. Two-pass-over-source vs single-pass-with-resolve-sorter is now a parser-walks-twice (~36 hours at 21.3 B) vs intermediate-disk (~5 TB chunk-spill) trade-off, not a bz2-throughput trade-off.

### Why the projection was off — and the lesson

The 9× projection assumed CPU-bound parallelism on the BWT inverse hot path. The actual bottleneck is the *producer-side* boundary scanner, which is single-threaded by design. Per-worker decode rate is constant; adding workers beyond ~4 just creates idle workers. The lesson generalizes: in any worker-pool architecture, instrument the producer's feed rate vs worker capacity. If feed rate is the lower number, more workers don't help.

This finding is recorded in `docs/limits/bz2-decompression-single-threaded.md` (status: Resolved by ADR-036 Phase 2 with measurement caveat) and is the seed observation for a sister entry on scanner optimization (vectorized magic search) — *deferred*, since on the bulk-load production path the parallel decoder isn't load-bearing.

### Production-path recommendation

- **Bulk-load** (`mercury --bulk-load latest-all.ttl.bz2`): use `BZip2DecompressorStream` (Phase 1, single-threaded). Sufficient for the parser-bound 17.5 MB/s consumption rate.
- **Convert** (`mercury convert in.ttl.bz2 out.nt`): use `ParallelBZip2DecompressorStream` (Phase 2, parallel) with `workerCount: 4`. Cuts convert wall-clock by ~57% on M5 Max class hardware.

### Phase 2 status transitions

- **Proposed** — 2026-04-30. Implementation commit `a11f873`.
- **Accepted** — 2026-04-30. Correctness validated (14 tests, multi-block, up to 14 workers, random reads, CRC corruption detection).
- **Completed** — 2026-04-30. Throughput measured (commits `b39f186`, `0c3b1ae`); workload-separated verdict documented; production-path recommendations recorded in this ADR.

## Open questions

- **Output buffering size.** The 64 KB ring buffer is a first guess. Benchmark on real N-Triples ingestion (the parser reads in chunks); tune at Phase 5.
- **`Position` and `Length` semantics.** Throwing `NotSupportedException` is the conservative choice. Reporting `Position` as "decompressed bytes emitted so far" is technically tractable and would help operators reason about progress, but it is not a `Stream` semantic that any consumer depends on. Default: throw, revisit if a consumer asks for it.
- **`async` story.** `ReadAsync` will likely just call `Read` synchronously and wrap in `Task.FromResult` — bzip2 decompression is CPU-bound, not I/O-bound. Truly-async decompression (reading the underlying stream asynchronously while decoding) requires interleaving the two and is non-trivial; defer until a consumer surfaces a need.
- **Future format work.** When zstd / lz4 / brotli surface, do they live in the same `Compression/` subdirectory or earn their own subprojects? Defer; the bridge is short either way.

## References

- Seward, J. (1996). *bzip2 and libbzip2, version 1.0.8*. Reference C implementation; the canonical algorithmic source.
- Burrows, M. & Wheeler, D. (1994). *A Block-Sorting Lossless Data Compression Algorithm*. SRC Research Report 124. The BWT itself.
- bzip2 file format specification: <https://en.wikipedia.org/wiki/Bzip2#File_format> and the Seward source.
- [`docs/limits/streaming-source-decompression.md`](../../limits/streaming-source-decompression.md) — original limits entry, this ADR resolves
- [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) — Wikidata-scale streaming pipeline, the bulk-load consumer
- [ADR-035](ADR-035-phase7a-metrics-infrastructure.md) — metrics infrastructure whose validation runs gate on this
- [`docs/roadmap/production-hardening-1.8.md`](../../roadmap/production-hardening-1.8.md) — Phase 7b position in the substrate sequence
- `RdfEngine.LoadFileAsync` (`src/Mercury/RdfEngine.cs`) — the integration point
- `latest-all.ttl.bz2` — Wikidata canonical distribution, the source artifact this ADR enables
