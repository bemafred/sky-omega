# Sky Omega Statistics

Codebase metrics are tracked over time. Update after significant changes.

**Last updated:** 2026-04-30 (1.7.47 — property-path hardening landed: parser refactor + zero-GC walker + Case 2 binding fix; ADR-006 MCP surface discipline + ADR-007 sealed substrate immutability shipped Proposed; WDBench cold baseline 1.7.47 sealed against `wiki-21b-ref` with 0 parser-fail and 100% cancellation-cap discipline at scale)

Scale-validation runs live in [`docs/validations/`](docs/validations/). Micro-benchmarks live in `benchmarks/Mercury.Benchmarks/`. This document tracks codebase metrics and W3C conformance counts.

## Line Counts

### Source Code

| Component | Lines | Description |
|-----------|------:|-------------|
| **Mercury (total)** | **82,887** | Knowledge substrate |
| ├─ Sparql | 45,468 | SPARQL parser, executor, protocol (1.7.47: parser refactor + zero-GC property-path walker) |
| ├─ Storage | 10,957 | B+Tree indexes (temporal + reference), atom stores (Hash + Sorted), RadixSort, ExternalSorter, AppendSorted, WAL, schema plumbing, bulk builders |
| ├─ JsonLd | 7,237 | JSON-LD parser and writer |
| ├─ Turtle | 4,108 | Turtle parser and writer |
| ├─ RdfXml | 3,032 | RDF/XML parser and writer |
| ├─ TriG | 2,836 | TriG parser and writer |
| ├─ Diagnostics | 2,742 | Observability infrastructure; ADR-035 Phase 7a metrics (bulk/rebuild progress, atom-store events + samplers, process-level state) |
| ├─ NQuads | 1,476 | N-Quads parser and writer |
| ├─ Compression | 1,453 | ADR-036 Phase 7b — BCL-only bzip2 streaming decompressor (CRC32, BitReader, RLE1, MTF, Huffman, BWT inverse) |
| ├─ NTriples | 1,341 | N-Triples parser and writer |
| ├─ Owl | 566 | OWL/RDFS reasoner |
| ├─ Rdf | 536 | Core RDF types |
| └─ (top-level + obj) | 1,235 | `RdfEngine`, `SparqlEngine` facades + generated files |
| **Mercury.Abstractions** | **974** | `StoreProfile`, `StoreSchema`, `IAtomStore`, `AtomStoreImplementation`, exceptions, shared types |
| **Mercury.Runtime** | **3,329** | Buffers, cross-process gate, temp paths |
| **Mercury.Solid (total)** | **4,385** | W3C Solid Protocol (WAC/ACP, N3 Patch, HTTP handlers) |
| **Mercury Tool Libraries** | **1,327** | Sparql.Tool + Turtle.Tool |
| **Mercury CLIs** | **1,626** | mercury, mercury-sparql, mercury-turtle, mercury-mcp |
| **Mercury.Pruning** | **1,231** | Copy-and-switch pruning + PruneEngine; ADR-007 Reference profile rejection at plan time |
| **Mercury.Mcp** | **460** | MCP server tools — query/stats/graphs/store/version/update; pruning intentionally NOT exposed (ADR-006) |
| **DrHook (total)** | **2,343** | Runtime observation substrate (EventPipe + DAP) |
| **Minerva.Core** | **33** | Thought substrate (planned, scaffolding only) |

ADR-028 + ADR-029 additions since 2026-04-17: `Storage` grew by ~2 K lines (`ReferenceQuadIndex`, schema plumbing, profile-aware `QuadStore`); `Mercury.Abstractions` grew to 721 lines from the new profile types and shared interfaces. `TemporalQuadIndex` is the rename of the former `QuadIndex`; the rename was tracked as git-rename (98 % / 95 % similarity) so `git log --follow` stitches history intact.

ADR-032 + ADR-033 additions (2026-04-21 → 2026-04-23, versions 1.7.38 → 1.7.44): `Storage` grew another ~1.2 K lines for `RadixSort` (LSD radix sort with 8-bit digits, signed-long bias, skip-trivial-passes optimization), `ExternalSorter<T, TSorter>` (chunked spill + k-way merge via binary heap), `TrigramEntry` (12-byte sort key for the trigram rebuild), `AppendSorted` (sort-insert fast path for `ReferenceQuadIndex`), and the bulk-load + rebuild integration points in `QuadStore`. Phases 5.1.b and 5.1.c (parallel rebuild via broadcast channel; sort-insert via comparator) were shipped, validated as wall-clock-neutral, then **reverted** when Phase 5.2 dotnet-trace + iostat showed they had traded compute for overhead. The reverts retired ~600 lines from `QuadStore` + the `BroadcastChannel.cs` file. The radix external-sort architecture replaced both, preserving the architectural goal (sequential I/O via sort-insert) without the implementation cost. Reference 100M rebuild dropped from 511 s baseline to **48.64 s** (10.5× faster) after ADR-032 Phase 4; 1B end-to-end (bulk + rebuild) dropped from ~3h57m baseline to **60m36s** (3.92× faster). 21.3B Reference end-to-end (Phase 6) **completed 2026-04-25 22:32 at 85 h end-to-end** — full Wikidata Reference profile bulk + rebuild on a single M5 Max laptop, BCL-only. Sealed artifact validated query-side 2026-04-26 (`docs/validations/21b-query-validation-2026-04-26.md`): both GSPO and GPOS indexes return correct results at 21.3 B, cold-cache `LIMIT 10` queries in tens of milliseconds. The capacity dimension of production hardening is empirical, not estimated.

ADR-035 + ADR-036 additions (2026-04-26 → 2026-04-27, versions 1.7.44 → 1.7.45): `Diagnostics` (2,742 lines) grew with the four Phase 7a metric channels — `LoadProgress` (bulk-load progress per chunk-flush), `RebuildProgress` (per-index sub-phase identification), atom-store discrete events (`AtomStoreRehash`, `AtomStoreFileGrowth`) + state samplers (`AtomStoreState` — intern rate, load factor, probe distance), and `ProcessState` (GC, LOH, RSS, disk-free). `Compression` (1,453 lines) is wholly new: a BCL-only bzip2 streaming decompressor (`BZip2DecompressorStream`) implementing CRC32, BitReader, RLE1, MTF, Huffman, BWT inverse from the bzip2 spec. Validated end-to-end at 1 B Reference on 2026-04-27: bz2 source decompression at 33 MB/s steady-state with 4× headroom over the parser's ~8 MB/s consumption, full metrics emission across 22,256 JSONL records, bulk-load 55m22s @ 300 K triples/sec — first production-scale exercise of 7a + 7b together (`docs/validations/adr-035-phase7a-1b-2026-04-27.md`).

ADR-034 SortedAtomStore (2026-04-27, version 1.7.45+, in flight): `Storage` gained an `IAtomStore` interface, the renamed `HashAtomStore`, and a new `SortedAtomStore` (mmap-backed `{base}.atoms` + `{base}.offsets` files, dense alphabetical IDs, binary-search lookup) backed by two builders — an in-memory `SortedAtomStoreBuilder` (validation/test surface) and an external `SortedAtomStoreExternalBuilder` (chunked spill + k-way merge for past-RAM vocabularies). `SortedAtomBulkBuilder` orchestrates the two-pass deferred-resolution flow during bulk-load: buffer atoms in input order, sort vocabulary at finalize, replay resolved (G,S,P,O) IDs into the GSPO external sorter. `QuadStore.Open` dispatches on `StoreSchema.AtomStore` (default `Hash` for backward compat); the Reference profile + Sorted schema combination opens with a placeholder over an empty vocab and routes `BeginBatch` / `AddCurrentBatched` / `CommitBatch` through the bulk builder. Phase 1A through Phase 1B-5b shipped; Phase 1B-5d (disk-backed AssignedIds for >100 M scale) and Phase 1B-6 (gradient validation 1 M / 10 M / 100 M against `HashAtomStore` baseline) remaining.

Property-path hardening (2026-04-29 → 2026-04-30, versions 1.7.46 → 1.7.47): the WDBench cold-baseline run on `wiki-21b-ref` surfaced and resolved three latent property-path defects across the SPARQL substrate. **1.7.46** patched 12 cancellation-token gaps across `Operators/TriplePatternScan.cs`, `Operators/SlotBasedOperators.cs`, `Operators/MultiPatternScan.cs`, and `QueryResults.Patterns.cs` — every property-path inner loop now samples `QueryCancellation.ThrowIfCancellationRequested()` per `MoveNext()`, bounding worst-case unbounded-hang to one B+Tree node walk plus a token check. **1.7.47** restructured the parser around a unified `ApplyPathExprModifiers` composition stage so inverse primaries (`^iri`, `^(X)`) reach the same trailing-modifier path as base-term primaries — closes 12 grammar gaps (`^(P){q}`, `^((A|B)){q}`, `(^A/B)`, nested variants) the W3C SPARQL 1.1 conformance suite did not exercise. The runtime walker (`WalkPathContentInto` in `TriplePatternScan.cs`) replaces three near-duplicate methods (`ExecuteGroupedSequence`, `ExecuteInverseGroupedSequence`, `DiscoverGroupedSequenceStartNodes`) with a single zero-GC implementation: span-only parsing into `_source`, `stackalloc int[32]` range tables, `HashSet<string>.GetAlternateLookup<ReadOnlySpan<char>>` for dedupe-without-allocation, reusable frontier sets. The Case 2 binding fix (`_startedFromObject` flag) closes a latent silent-failure mode where `?x path <obj>` queries returned 0 rows in <1 ms instead of the actual ancestor set; on `wiki-21b-ref` this turned 5 WDBench queries from silent zero-row completion into legitimate 39 K-row computations. `ComposeQuantifiers` algebraic collapse handles SPARQL transitive-closure idempotence (`((P)*)*` → `P*`), preserving W3C pp37 conformance. ADR-006 (MCP surface discipline) + ADR-007 (sealed substrate immutability) operationalize the governance principle the arc surfaced: pruning is no longer exposed via MCP, and the Reference profile rejects pruning at plan time with bulk-load re-creation guidance. WDBench 1.7.47 cold baseline against `wiki-21b-ref` (full Wikidata, 21.3 B triples): 1,199 queries, **0 parser failures**, p25=4ms, p50=45ms, p95=29.85s, p99=49.50s; every one of 655 timeouts closed between 60.000 s and 63.620 s — cancellation contract honored at scale (`docs/validations/wdbench-paths-21b-2026-04-29-1747.jsonl` + `wdbench-c2rpqs-21b-2026-04-29-1747.jsonl`).

### Tests

| Project | Lines | Test Cases | Notes |
|---------|------:|----------:|-------|
| Mercury.Tests | 58,657 | 4,335 | +4 since 1.7.45: PropertyPathRegressionTests (`SequenceWithZeroOrMore_DoesNotThrow`, `TransitivePath_HonorsCancellationToken`, `PropertyPathShapes_ParseAndExecuteCorrectly` covering all three grammar shapes + Case 2) + PruneEngineTests `Execute_ReferenceProfile_IsRejectedWithGuidance` |
| Mercury.Solid.Tests | 407 | 25 | |
| Minerva.Tests | — | — | |

### Benchmarks

| Project | Lines | Methods |
|---------|------:|--------:|
| Mercury.Benchmarks | 3,352 | 76 |
| Minerva.Benchmarks | — | — |

### Examples

| Project | Lines |
|---------|------:|
| Mercury.Examples | 779 |
| drhook-target.cs | 155 |
| drhook-verify.cs | 38 |
| Minerva.Examples | — |

### Documentation

| Category | Lines |
|----------|------:|
| All docs (*.md, *.ttl) | 37,924 |
| CLAUDE.md | 291 |

## Totals

| Category | Lines |
|----------|------:|
| Source code | ~98,265 |
| Tests | ~59,064 |
| Benchmarks | ~3,352 |
| Examples | ~972 |
| Documentation | ~37,924 |
| **Grand total** | **~199,577** |

## W3C Conformance

Target: 100% conformance on core features. Optional/deprecated features documented separately.

See [ADR-010](docs/adrs/mercury/ADR-010-w3c-test-suite-integration.md) for integration details.

### Core Conformance (100%)

| Format | Passing | Total | Coverage | Notes |
|--------|--------:|------:|---------:|-------|
| Turtle 1.2 | 309 | 309 | **100%** | Full conformance |
| TriG 1.2 | 352 | 352 | **100%** | Full conformance |
| RDF/XML 1.1 | 166 | 166 | **100%** | Full conformance |
| N-Quads 1.2 | 87 | 87 | **100%** | Full conformance |
| N-Triples 1.2 | 70 | 70 | **100%** | Full conformance |
| SPARQL 1.1 Syntax | 103 | 103 | **100%** | Full conformance |
| SPARQL 1.1 Update | 94 | 94 | **100%** | Full conformance |
| **Core Total** | **1,181** | **1,181** | **100%** | |

### Extended Conformance

| Format | Passing | Total | Coverage | Skipped |
|--------|--------:|------:|---------:|---------|
| JSON-LD 1.1 | 461 | 467 | **100%** | 6: JSON-LD 1.0 legacy (4), generalized RDF (2) |
| SPARQL 1.1 Query | 421 | 421 | **100%** | — |
| **Extended Total** | **882** | **888** | **99%** | |

### Skipped Test Categories

| Category | Count | Reason | Status |
|----------|------:|--------|--------|
| JSON-LD 1.0 | 4 | Legacy behavior superseded by 1.1 | Intentional |
| Generalized RDF | 2 | Non-standard (blank node predicates) | Intentional |

### Summary

| Metric | Value |
|--------|-------|
| **Core conformance** | 100% (1,181/1,181) |
| **SPARQL 1.1 Query** | 100% (421/421) |
| **SPARQL 1.1 Update** | 100% (94/94) |
| **With optional extensions** | 99% (2,063/2,069) |
| **Remaining gaps** | 0 (all high-complexity gaps resolved) |

## NCrunch (Windows) Compatibility

All tests pass under NCrunch parallel test execution on Windows:

| Metric | Value |
|--------|-------|
| **Total tests** | 2,146 |
| **Passed** | 2,146 |
| **Failed** | 0 |

Key fixes for NCrunch compatibility:
- MSBuild-embedded W3C submodule paths via `$(NCrunchOriginalProjectDir)` (NCrunch workspaces don't copy git submodules)
- Stack frame reduction in SPARQL parser (eliminated intermediate `GraphPattern` locals ~5-11 KB each)
- TOCTOU-resilient `CrossProcessStoreGate` tests (concurrent NCrunch runner processes)

## Stack Size (ADR-011)

Query execution structs optimized for stack safety:

| Struct | Before | After | Reduction |
|--------|-------:|------:|----------:|
| QueryResults | 89,640 bytes | 6,128 bytes | **93%** |
| MultiPatternScan | 18,080 bytes | 384 bytes | **98%** |
| DefaultGraphUnionScan | 33,456 bytes | 1,040 bytes | **97%** |
| CrossGraphMultiPatternScan | 15,800 bytes | 96 bytes | **99%** |

Key optimizations:
- Pooled enumerator arrays via `ArrayPool<T>.Shared`
- Boxed `GraphPattern` (~4KB) moved from stack to heap
- Changed `TemporalResultEnumerator` from `ref struct` to `struct`

## Benchmark Summary

Run benchmarks with:
```bash
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*ClassName*"
```

Results written to `BenchmarkDotNet.Artifacts/results/` (gitignored).

### Hardware Comparison: M1 Pro vs M5 Max

Benchmarks run on identical .NET 10.0.0 / Arm64 RyuJIT AdvSIMD. M1 results from 2026-02-07, M5 Max from 2026-03-30.

#### Query Performance

| Query | M1 Pro | M5 Max | Speedup | Allocated |
|-------|-------:|-------:|--------:|----------:|
| QueryBySubject | 647 ns | 488 ns | **1.33x** | 8.02 KB |
| QueryByObject | 25,542 ns | 15,679 ns | **1.63x** | 8.02 KB |
| QueryByPredicate | 256,178 ns | 163,536 ns | **1.57x** | 8.02 KB |
| FullScan | 2,309,110 ns | 1,501,273 ns | **1.54x** | 8.02 KB |

Allocations are byte-identical across hardware. Zero-GC design makes performance portable — speedup comes purely from faster cores.

#### Turtle Parser Performance

| Parser | M1 Pro | M5 Max | Speedup | Allocated |
|--------|-------:|-------:|--------:|----------:|
| 100K triples (zero-GC) | 145.3 ms | 88.4 ms | **1.64x** | 57.26 KB |
| 100K triples (IAsyncEnumerable) | 217.6 ms | 124.6 ms | **1.75x** | 263,181 KB |
| 10K triples (zero-GC) | 14.5 ms | 8.8 ms | **1.65x** | 57.26 KB |
| 10K prefixed (zero-GC) | 6.1 ms | 3.6 ms | **1.68x** | 1,621 KB |

Zero-GC path: 57 KB allocated regardless of input size or hardware. IAsyncEnumerable path: same allocations, but M1 needed 4,333 gen0 collections vs 1,400 on M5 Max.

#### Storage Write Performance

| Write | M1 Pro | M5 Max | Notes |
|-------|-------:|-------:|-------|
| Single 1K (fsync each) | 5,623 ms | 6,499 ms | M1 SSD has lower fsync latency |
| Batch 1K (single fsync) | 885 ms | 1,045 ms | ~same |
| Single 10K (fsync each) | 50,374 ms | 119,666 ms | M5 Max SSD fsync variance |
| Batch 10K (single fsync) | 5,926 ms | 9,589 ms | SSD controller difference |

Write performance is fsync-dominated — measures SSD controller behavior, not CPU. M5 Max SSD has higher and more variable fsync latency. Batch writes (the production path) amortize this.

### Throughput Summary (M5 Max)

| Operation | Throughput | Notes |
|-----------|------------|-------|
| Subject lookup | ~2M queries/sec | 488 ns per query |
| Object lookup | ~64K queries/sec | B+Tree index scan |
| Predicate scan | ~6K queries/sec | Broader index traversal |
| Full scan | ~667 scans/sec | Complete store traversal |
| Turtle parse (zero-GC, micro) | ~1.1M triples/sec | 57 KB total allocation, 100K triples |
| Turtle parse + NT write (real-world, 912 GB) | **2.7M triples/sec** | Full Wikidata convert, 21.3B triples sustained — see [docs/validations/parser-at-wikidata-scale-2026-04-17.md](docs/validations/parser-at-wikidata-scale-2026-04-17.md) |
| Batch write 10K | ~1,043 triples/sec | Single fsync, SSD-bound |

### Scale-validation runs

| Date | Subject | Scope | Key measurement |
|------|---------|-------|-----------------|
| 2026-04-17 | [Turtle parser at Wikidata scale](docs/validations/parser-at-wikidata-scale-2026-04-17.md) | Parser + NT writer only; no store | 21.3B triples at 2.7M/sec sustained, flat throughput over 2h 11m |
| 2026-04-17 | [Bulk-load gradient](docs/validations/bulk-load-gradient-2026-04-17.md) | NT bulk-load 1M → 100M Cognitive | 5 bug classes caught and fixed across the 1.7.13 → 1.7.22 arc |
| 2026-04-19 | [Full-pipeline gradient](docs/validations/full-pipeline-gradient-2026-04-19.md) | Bulk + rebuild through 1 B Cognitive | 1 B rebuild 3 h 7 m, 14.8 M predicate-bound rows in 49 s, 3 rebuild-path bugs fixed |
| 2026-04-20 | [Turtle at Wikidata scale](docs/validations/turtle-at-wikidata-scale-2026-04-20.md) | Turtle bulk-load at 100 M | 292 K triples/sec, ~12 % slower than NT at source-triple level, zero parser errors |
| 2026-04-20 | [Dispose profile](docs/validations/dispose-profile-2026-04-20.md) | 1 B read-only Dispose | 14 min attributed to `CollectPredicateStatistics` from `CheckpointInternal`, not msync — ADR-031 Piece 2 mechanism rewritten |
| 2026-04-20 | [ADR-028 rehash gradient](docs/validations/adr-028-rehash-gradient-2026-04-20.md) | 1 M / 10 M / 100 M with forced 16 K initial hash | 8 / 11 / 14 rehashes per scale, exact-match query rows to baseline, 100 M past the 58 M Bug-5 ceiling cleanly |
| 2026-04-20 | [ADR-029 Reference gradient](docs/validations/adr-029-reference-gradient-2026-04-20.md) | Reference vs Cognitive at 1 M / 10 M / 100 M | **~5× index reduction (thesis validated)**; bulk rate collapse 210K → 31K triples/sec exposes inline-secondary-write cost (ADR-030 Decision 5 amendment) |
| 2026-04-21 | [ADR-031 Dispose gate](docs/validations/adr-031-dispose-gate-2026-04-21.md) | 1 B Cognitive read-only Dispose | 14 min → 0.84 s, mutation-tracked Dispose gates the `CollectPredicateStatistics` work — ADR-031 Pieces 1+2 shipped |
| 2026-04-21 | [ADR-030 Decision 5 Reference refactor](docs/validations/adr-030-decision5-reference-refactor-2026-04-21.md) | Reference 100 M end-to-end with bulk/rebuild split | Reference matches Cognitive bulk rate; rebuild path lands ahead of full secondary-inline cost |
| 2026-04-21 | [ADR-030 Phase 2 parallel rebuild](docs/validations/adr-030-phase2-parallel-rebuild-2026-04-21.md) | Reference 100 M parallel rebuild via broadcast channel | Wall-clock NEUTRAL at 100 M (524 s vs 512 s sequential) — shipped, then reverted after Phase 5.2 trace exposed the hidden GC + lock cost |
| 2026-04-21 | [ADR-030 Phase 3 sort-insert](docs/validations/adr-030-phase3-sort-insert-2026-04-21.md) | Reference 100 M sort-insert via Array.Sort comparator | Wall-clock NEUTRAL at 100 M — shipped, then reverted; the *concept* (sort-insert) was right but the *implementation* (comparator-sort + 3.2 GB monolithic buffer) cost as much as it saved |
| 2026-04-21 | [Phase 5.2 trace + I/O measurement](docs/validations/adr-030-phase52-trace-2026-04-21.md) | dotnet-trace + iostat + 1.7.34 vs 1.7.37 A/B at 100 M | **Architectural pivot point.** Wall-clock equality was hiding a structural cost shift. 1.7.37 had 453 s GC.RunFinalizers + 552 s Monitor.Enter_Slowpath that 1.7.34 did not. Bottleneck identified as write amplification (~3× useful I/O), not CPU and not bandwidth. Drove the revert + the radix external-sort architecture |
| 2026-04-22 | [ADR-032 Phase 3 GPOS radix](docs/validations/adr-032-phase3-gpos-radix-2026-04-22.md) | GPOS rebuild via radix external sort at 100 M | Wall-clock 511 s → 457 s; GPOS portion ~3× faster (76 s → 24 s); peak iostat **2463 MB/s** (7.5× the baseline 327 MB/s) — sequential GPOS append finally hitting NVMe bandwidth |
| 2026-04-22 | [ADR-032 Phase 4 trigram radix](docs/validations/adr-032-phase4-trigram-radix-2026-04-22.md) | Trigram rebuild via radix at 100 M | Wall-clock 457 s → **48.64 s** (9.4× faster); trigram portion 17× faster; both indexes now sequential. Total rebuild speedup vs 1.7.38 baseline: **10.5×** |
| 2026-04-22 | [ADR-033 Phase 5 bulk radix](docs/validations/adr-033-phase5-bulk-radix-2026-04-22.md) | Bulk-load + rebuild gradient 1M → 1B | 1B end-to-end ~3h57m → **60m36s** (**3.92× combined**); rebuild contributes 13.8× while bulk holds steady at 1B (defensive at this scale). Three independent confirmations of the Phase 5.2 hypothesis across three code paths |
| 2026-04-25 | Phase 6 21.3B Wikidata Reference end-to-end | Full `latest-all.nt` bulk + rebuild on a single M5 Max laptop, BCL-only | **Completed** 2026-04-25 22:32 at **85 h end-to-end** wall-clock. 21,260,051,924 triples ingested + sealed; ~2.5 TB physical on disk, 4.1 TB logical mmap (sparse on APFS); past Blazegraph WDQS reference ceiling (~12-13B) by ~63 % |
| 2026-04-26 | [21.3 B query-side validation](docs/validations/21b-query-validation-2026-04-26.md) | First measured queries against `wiki-21b-ref` | GSPO `LIMIT 10` 17 ms (6.5 ms parse + 10.6 ms exec); GPOS `wdt:P31` `LIMIT 10` 20 ms; both indexes correct at 21.3 B; cold-cache. **Capacity dimension of production hardening is empirical, sound finding** |
| 2026-04-27 | [ADR-035 Phase 7a 1 B Reference](docs/validations/adr-035-phase7a-1b-2026-04-27.md) | First end-to-end test of Phase 7a metrics + Phase 7b bz2 streaming together at 1 B Reference | Bulk-load 55m22s @ 300 K triples/sec from `latest-all.ttl.bz2` (114 GB); GPOS rebuild 1m54s @ 8.66 M entries/sec; Trigram rebuild 11m44s @ 630 K entries/sec; bz2 decompression 33 MB/s with 4× headroom over parser; 22,256 JSONL records emitted across all four metric channels. **Phase 7a Completed.** |
| 2026-04-27 | [WDBench cold baseline (pre-fix)](docs/validations/wdbench-cold-baseline-21b-2026-04-27.jsonl) | First WDBench cold baseline against `wiki-21b-ref` | Surfaced two distinct issues: (a) executor cancellation gap — one c2rpqs query consumed 4h 51m wall-clock under a 60 s timeout cap, ~547 of 660 paths events silently lost (`docs/limits/cancellable-executor-paths.md`); (b) property-path parser grammar gaps — 12 of 1,199 queries (1.0 %) hitting three combinations the W3C SPARQL 1.1 conformance suite does not exercise (`docs/limits/property-path-grammar-gaps.md`). Both characterized in 48 hours; both fixed in 1.7.46/1.7.47 |
| 2026-04-29 | WDBench 1.7.47 cold baseline | Hardened-substrate WDBench against `wiki-21b-ref` (full Wikidata, 21.3 B triples) | 1,199 queries (660 paths + 539 c2rpqs). 11h 30m total wall. **0 parser failures**, **655/655 timeouts honored 60 s cap** (max 63.62 s, min 60.00 s — contract honored at scale). p25=4ms, p50=45ms, p75=1.39s, p90=12.82s, p95=29.85s, p99=49.50s, max=59.82s. Disclosure-marked baseline for Phase 7c optimization rounds; running on full Wikidata is intentional (substrate-capability claim, not the truthy subset that QLever/Virtuoso WDBench numbers use — see [memos/2026-04-30-latent-assumptions-from-qlever-comparison.md](memos/2026-04-30-latent-assumptions-from-qlever-comparison.md)). Sealed: `docs/validations/wdbench-paths-21b-2026-04-29-1747.jsonl` + `docs/validations/wdbench-c2rpqs-21b-2026-04-29-1747.jsonl` |

## Maintenance Instructions

Update this file when:
- Adding new components or significant features
- After milestone completions
- Before releases

Quick update commands:
```bash
# Mercury source lines
find src/Mercury -name "*.cs" -exec wc -l {} + | tail -1

# Test lines
find tests -name "*.cs" -exec wc -l {} + | tail -1

# Documentation lines
find docs -name "*.md" -exec wc -l {} + | tail -1
```
