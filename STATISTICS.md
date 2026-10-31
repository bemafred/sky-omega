# Sky Omega Statistics

Codebase metrics: line counts, test counts, W3C conformance and benchmark measurements. What changed in each release
is in [CHANGELOG.md](CHANGELOG.md) and [docs/releases/](docs/releases/); scale-validation runs are in
[docs/validations/](docs/validations/).

**Last measured:** 2026-10-30, Sky Omega 1.8.37 (`a57e1f8`). Line counts are of `.cs` files with `bin/` and `obj/`
left out; test counts are from one `dotnet test` run on omega (macOS arm64), read from its TRX reports.

## Line Counts

### Source Code

| Component | Lines | Description |
|-----------|------:|-------------|
| **Mercury (total)** | **66,873** | Knowledge substrate: RDF storage and SPARQL, BCL only |
| ├─ Sparql | 24,434 | SPARQL parser, query and update execution, protocol |
| ├─ Storage | 16,097 | B+Tree indexes, atom stores, WAL, bulk load and index rebuild, external sort, MPHF |
| ├─ JsonLd | 6,979 | JSON-LD parser and writer |
| ├─ Turtle | 3,962 | Turtle parser and writer |
| ├─ Diagnostics | 3,184 | Metrics and observability events |
| ├─ RdfXml | 3,148 | RDF/XML parser and writer |
| ├─ TriG | 2,448 | TriG parser and writer |
| ├─ Compression | 2,260 | bzip2 streaming decompressor, single and parallel block |
| ├─ NQuads | 1,454 | The N-Quads parser and writer, which N-Triples also reads and writes through |
| ├─ Rdf | 1,145 | Core RDF types and the shared escape, IRI, numeric, literal-form and name-character rules |
| ├─ Owl | 566 | OWL/RDFS reasoner |
| ├─ NTriples | 178 | N-Triples parser and writer, over the N-Quads ones |
| └─ (top-level files) | 1,018 | `RdfEngine` and `SparqlEngine` facades, `RdfDocumentWriter` |
| **Mercury.Abstractions** | **1,486** | Store profiles and schema, `IAtomStore`, metric event types, exceptions |
| **Mercury.Runtime** | **4,229** | `BTreeFile`, `PageCache`, buffers, cross-process gate, disk-space guard, memory probe, temp paths, sparse files |
| **Mercury.Solid** | **4,401** | W3C Solid Protocol: WAC/ACP, N3 Patch, HTTP handlers |
| **Mercury.Pruning** | **1,238** | Copy-and-switch pruning |
| **Mercury.Mcp** | **481** | MCP server, 6 tools (query, update, stats, graphs, store, version) |
| **Mercury tool libraries** | **1,287** | `Mercury.Sparql.Tool` 727, `Mercury.Turtle.Tool` 560 |
| **Mercury CLIs** | **1,187** | `mercury` 764, `mercury-sparql` 220, `mercury-turtle` 203 |
| **DrHook.Engine** | **10,233** | Runtime observation: ICorDebug through per-RID `libdbgshim`, EventPipe, BCL + P/Invoke + source-generated COM |
| **DrHook.Mcp** | **1,968** | MCP server, 25 tools |
| **DrHook.Wire** | **108** | NDJSON protocol for the debug-state transport |
| **DrHook.Viz** | **770** | BCL-only client library for debug-state views |
| **DrHook.Viz.Console** | **71** | `drhook-viz-console`, the console view |
| **DrHook.Viz.Tui** | **399** | `drhook-viz-tui`, the full-screen TUI view |
| **DrHook.Capture** | **199** | Persists a debug session's state stream to Mercury |
| **SkyOmega.Bcl** | **420** | Long-indexed collections, `BitVector`, `SplitMix64Hash` |
| **Minerva.Core** | **33** | Inference substrate, scaffolding only |

### Tests

| Project | Lines | Tests | Skipped on omega |
|---------|------:|------:|-----------------:|
| Mercury.Tests | 70,749 | 6,312 | 167 |
| Mercury.Solid.Tests | 407 | 25 | — |
| SkyOmega.Bcl.Tests | 140 | 12 | — |
| DrHook.Engine.Tests | 2,468 | 187 | 1 |
| DrHook.Engine.IntegrationTests | 2,302 | 19 | 1 |
| DrHook.Engine.IntegrationTargets (Mtp, Snapshot, Vstest) | 251 | 2 | — |
| DrHook.Viz.Tests | 852 | 42 | — |
| DrHook.Wire.Tests | 107 | 5 | — |
| DrHook.Capture.Tests | 85 | 1 | — |
| **Total** | **77,361** | **6,605** | **169** |

The integration targets are debuggees for the DrHook integration tests; only the Mtp target has tests of its own. On
omega the 169 skips are 160 W3C known failures (154 RDF 1.2, 6 SPARQL 1.0), 6 JSON-LD tests that do not apply to a
JSON-LD 1.1 processor, 2 Windows-only DrHook tests, and 1 storage test that needs a disk with less than 1 TB free.
Other platforms skip a different set: see [Test Runs by Platform](#test-runs-by-platform).

### Benchmarks

| Project | Lines | Methods |
|---------|------:|--------:|
| Mercury.Benchmarks | 3,588 | 74 |

### Examples and Probes

| Project | Lines |
|---------|------:|
| Mercury.Examples | 779 |
| examples/drhook-target.cs | 155 |
| examples/drhook-verify.cs | 38 |
| Probes and proofs of concept (`poc/`) | 18,775 |

### Documentation

| Category | Lines |
|----------|------:|
| `docs/` (363 `.md` and `.ttl` files) | 55,090 |
| Root documents (README, CLAUDE, AI, MERCURY, DRHOOK, CONTRIBUTING, CHANGELOG) | 4,277 |

## Totals

| Category | Lines |
|----------|------:|
| Source code | 95,383 |
| Tests | 77,361 |
| Benchmarks | 3,588 |
| Examples | 972 |
| Probes | 18,775 |
| Documentation | 59,367 |
| **Grand total** | **255,446** |

## Test Runs by Platform

The whole solution's `dotnet test` at 1.8.37 (`a57e1f8`), 2026-10-30, on the machines Sky Omega is tested on.
omega-ubuntu and omega-windows are virtual machines on omega; they ran at the same time, after omega's run.

| Machine | OS, architecture | Hardware | .NET runtime | Tests | Failed | Skipped | Mercury.Tests |
|---------|------------------|----------|--------------|------:|-------:|--------:|--------------:|
| omega | macOS 27.0.1, arm64 | Apple M5 Max, 18 cores, 128 GB | 10.0.9 | 6,605 | 0 | 169 | 2 m 34 s |
| zbook | Ubuntu 26.04.1, x64 | Intel Core i7-6700HQ, 8 threads, 60 GB | 10.0.14 | 6,605 | 0 | 168 | 1 m 48 s |
| elite | Ubuntu 26.04.1, x64 | AMD Athlon II X4 640, 4 cores, 15 GB | — | — | — | — | — |
| omega-ubuntu | Ubuntu 26.04.1, arm64 | Parallels VM, 4 vCPU, 7.2 GB | 10.0.13 | 6,605 | 0 | 168 | 56 s |
| omega-windows | Windows 11 Pro 26100, arm64 | Parallels VM, 4 vCPU, 8 GB | 10.0.14 | 6,605 | 0 | 176 | 2 m 36 s |

elite went off the network during its run and is not measured at 1.8.37. At 1.8.36 (`cf6d320`) it ran 6,234 tests
with 0 failed and 14 skipped; its Mercury.Tests took 15 m 38 s.

Every test project passes on every machine that ran, the DrHook integration tests included. The skips differ by
platform:

- **Every machine:** the 160 W3C known failures and the 6 JSON-LD tests that do not apply.
- **macOS and Linux:** the 2 Windows-only DrHook tests (the Windows launch primitive and the Job Object).
- **macOS only:** the sparse-file test, which needs a disk with less than 1 TB free.
- **Windows only:** 10 DrHook tests of POSIX or Linux-and-macOS behavior. 8 cover features Windows does not have
  (POSIX spawn, parent reaping, the debug-transport attach check, deleting a running executable); 2 have POSIX-only
  fixtures and are not yet written for Windows ([ADR-007](docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md)
  Phase 9).

### GitHub Actions

The same `dotnet test` for the push of 1.8.39 (`232afce`), on the six GitHub-hosted runners of
`.github/workflows/test.yml`, with .NET 10.0.14. The run, 41052511778, was deleted from GitHub on 2026-10-30, so this
table is its record:

| Runner | OS, architecture | Tests | Failed | Skipped | Mercury.Tests |
|--------|------------------|------:|-------:|--------:|--------------:|
| `macos-latest` | macOS 26.6.2, arm64 | 6,660 | 1 | 168 | 3 m 38 s |
| `macos-26-intel` | macOS 26.6.1, x64 | 6,660 | 0 | 168 | 11 m 36 s |
| `ubuntu-24.04` | Ubuntu 24.04.5, x64 | 6,660 | 0 | 168 | 4 m 45 s |
| `ubuntu-24.04-arm` | Ubuntu 24.04.5, arm64 | 6,660 | 0 | 168 | 4 m 40 s |
| `windows-latest` | Windows Server 2025 (10.0.26100), x64 | 6,660 | 0 | 176 | 41 m 51 s |
| `windows-11-arm` | Windows 11 (10.0.26200), arm64 | 6,660 | 0 | 176 | 33 m 25 s |

The failure on macOS arm64 is the DrHook integration test
`AttachAndOwn_VstestTestHost_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly`, which hit the suite's 120 s limit.
At 1.8.38 a sibling test, `AttachAndOwn_VstestTestHost_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak`, timed out
the same way on macOS x64; both pass on omega in about a second. Their cause is unknown. Investigating them found a
different hang, [finding 100](poc/drhook-engine/findings/100-orphaned-transport-read-holds-dac-lock.md), after which a
test host can never attach or launch again;
[ADR-016](docs/adrs/drhook/ADR-016-debugger-resilience-and-recovery.md) plans the work on each platform before
DrHook's teardown changes. The macOS runners run the sparse-file test, which omega skips.

## W3C Conformance

Each row counts the tests of the W3C manifests the suite runs. See
[ADR-010](docs/adrs/mercury/ADR-010-w3c-test-suite-integration.md) for the integration.

### RDF formats (RDF 1.2 test suites, `rdf/rdf12`, which include the RDF 1.1 suites)

| Format | Passing | Total | Known failures |
|--------|--------:|------:|---------------:|
| Turtle | 371 | 419 | 48 |
| TriG | 368 | 418 | 50 |
| N-Triples | 130 | 145 | 15 |
| N-Quads | 144 | 155 | 11 |
| RDF/XML | 167 | 197 | 30 |
| **RDF formats total** | **1,180** | **1,334** | **154** |

The N-Triples and N-Quads rows each include 41 canonical-form tests. Every RDF 1.1 test passes (993). Each RDF 1.2
manifest includes its RDF 1.1 manifest, so each test runs once
([ADR-059](docs/adrs/mercury/ADR-059-every-w3c-test-runs.md)).

### SPARQL

| Suite | Passing | Total | Known failures |
|-------|--------:|------:|---------------:|
| SPARQL 1.0 query syntax | 196 | 199 | 3 |
| SPARQL 1.0 query evaluation | 285 | 288 | 3 |
| SPARQL 1.1 query syntax | 106 | 106 | — |
| SPARQL 1.1 query evaluation | 247 | 247 | — |
| SPARQL 1.1 update syntax | 63 | 63 | — |
| SPARQL 1.1 update evaluation | 94 | 94 | — |
| **SPARQL total** | **991** | **997** | **6** |

### JSON-LD

| Suite | Passing | Total | Not applicable |
|-------|--------:|------:|---------------:|
| JSON-LD 1.1 toRdf | 461 | 467 | 6 |

### Known failures and tests that do not apply

| Tests | Count | Reason |
|-------|------:|--------|
| RDF 1.2 tests in the five format suites | 154 | RDF 1.2 features not read yet: triple terms outside Turtle, reified triples, annotations, base direction, `VERSION`, and RDF/XML's `rdf:annotation` and `rdf:dir` ([ADR-059](docs/adrs/mercury/ADR-059-every-w3c-test-runs.md) phase 2) |
| SPARQL 1.0 `distinct-2`, `distinct-9`, `dawg-lang-3` | 3 | Term identity: a simple literal and its `xsd:string` form are two terms; a pattern constant matches a language tag by case ([ADR-053](docs/adrs/mercury/ADR-053-one-rdf-term-one-atom.md)) |
| SPARQL 1.0 `syn-bad-26`, `syn-bad-29`, `syn-bad-30` | 3 | Malformed expressions accepted ([ADR-057](docs/adrs/mercury/ADR-057-expression-read-once.md)) |
| JSON-LD `te014`, `te026`, `te038`, `te071`, `t0118` | 5 | `specVersion` json-ld-1.0: for JSON-LD 1.0 processors; the 1.1 versions run |
| JSON-LD `te075` | 1 | Generalized RDF (a blank node as predicate), an optional, obsolete feature |

Each known failure is listed with its cause in `W3CTestContext.KnownFailures` and is skipped, not failed.

The test data holds W3C tests that are not loaded yet: SPARQL 1.2, the rest of SPARQL 1.1 (federation, result
formats, entailment, protocol, Graph Store), the RDF semantics tests and the JSON-LD API manifests. ADR-059 phases 3
to 6 load them.

### Summary

| Metric | Value |
|--------|-------|
| RDF formats, RDF 1.1 tests | 100% (993/993) |
| RDF formats, RDF 1.2 suites with RDF 1.1 | 88.5% (1,180/1,334) |
| SPARQL 1.0 and 1.1 | 99.4% (991/997) |
| JSON-LD 1.1 toRdf, applicable tests | 100% (461/461) |
| **All W3C tests run** | **2,632 passing of 2,798** (160 known failures, 6 not applicable) |

## NCrunch (Windows) Compatibility

Measured 2026-02-28, when the suite had 2,146 tests; not re-run since. All tests passed under NCrunch parallel test
execution on Windows:

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

Query execution structs optimized for stack safety. **Historical record:** `MultiPatternScan`, `DefaultGraphUnionScan`, and `CrossGraphMultiPatternScan` were **deleted by ADR-047** (2026-06-19) along with the rest of the old slot-operator engine — the unified `TreeJoinExecutor` is now the sole execution path, joining over `List<MaterializedRow>` and the kept `TriplePatternScan`. `QueryResults` survives (its streaming machinery was carved out in the ADR-047 deletion phase). The ADR-011 figures below are retained as the record of the original stack-safety work.

| Struct | Before | After | Reduction | Note |
|--------|-------:|------:|----------:|------|
| QueryResults | 89,640 bytes | 6,128 bytes | **93%** | kept (streaming surface since removed) |
| MultiPatternScan | 18,080 bytes | 384 bytes | **98%** | deleted (ADR-047) |
| DefaultGraphUnionScan | 33,456 bytes | 1,040 bytes | **97%** | deleted (ADR-047) |
| CrossGraphMultiPatternScan | 15,800 bytes | 96 bytes | **99%** | deleted (ADR-047) |

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

Profile attribution per [ADR-008 — Workload Profiles and Validation Attribution](docs/adrs/ADR-008-workload-profiles-and-validation-attribution.md). After the ADR-031 Dispose gate (2026-04-21), every run is Reference except the Cognitive gradient of 2026-05-09 (1.7.50). Mercury's semantic memory runs on Cognitive, and a Reference run is not evidence of Cognitive durability, temporal behavior or query latency: what validates Cognitive at 1.8.39, and what does not, is in [docs/limits/cognitive-profile-validation-drought.md](docs/limits/cognitive-profile-validation-drought.md#evidence-at-1839-2026-10-30).

| Date | Profile | Subject | Scope | Key measurement |
|------|---------|---------|-------|-----------------|
| 2026-04-17 | Parser-only | [Turtle parser at Wikidata scale](docs/validations/parser-at-wikidata-scale-2026-04-17.md) | Parser + NT writer only; no store | 21.3B triples at 2.7M/sec sustained, flat throughput over 2h 11m |
| 2026-04-17 | Cognitive | [Bulk-load gradient](docs/validations/bulk-load-gradient-2026-04-17.md) | NT bulk-load 1M → 100M Cognitive | 5 bug classes caught and fixed across the 1.7.13 → 1.7.22 arc |
| 2026-04-19 | Cognitive | [Full-pipeline gradient](docs/validations/full-pipeline-gradient-2026-04-19.md) | Bulk + rebuild through 1 B Cognitive | 1 B rebuild 3 h 7 m, 14.8 M predicate-bound rows in 49 s, 3 rebuild-path bugs fixed. The most recent Cognitive measurement at 1 B. |
| 2026-04-20 | Cognitive | [Turtle at Wikidata scale](docs/validations/turtle-at-wikidata-scale-2026-04-20.md) | Turtle bulk-load at 100 M | 292 K triples/sec, ~12 % slower than NT at source-triple level, zero parser errors |
| 2026-04-20 | Cognitive | [Dispose profile](docs/validations/dispose-profile-2026-04-20.md) | 1 B read-only Dispose | 14 min attributed to `CollectPredicateStatistics` from `CheckpointInternal`, not msync — ADR-031 Piece 2 mechanism rewritten |
| 2026-04-20 | Cognitive | [ADR-028 rehash gradient](docs/validations/adr-028-rehash-gradient-2026-04-20.md) | 1 M / 10 M / 100 M with forced 16 K initial hash | 8 / 11 / 14 rehashes per scale, exact-match query rows to baseline, 100 M past the 58 M Bug-5 ceiling cleanly |
| 2026-04-20 | Both | [ADR-029 Reference gradient](docs/validations/adr-029-reference-gradient-2026-04-20.md) | Reference vs Cognitive at 1 M / 10 M / 100 M | **~5× index reduction (thesis validated)**; bulk rate collapse 210K → 31K triples/sec exposes inline-secondary-write cost (ADR-030 Decision 5 amendment) |
| 2026-04-21 | Cognitive | [ADR-031 Dispose gate](docs/validations/adr-031-dispose-gate-2026-04-21.md) | 1 B Cognitive read-only Dispose | 14 min → 0.84 s, mutation-tracked Dispose gates the `CollectPredicateStatistics` work — ADR-031 Pieces 1+2 shipped |
| 2026-04-21 | Reference | [ADR-030 Decision 5 Reference refactor](docs/validations/adr-030-decision5-reference-refactor-2026-04-21.md) | Reference 100 M end-to-end with bulk/rebuild split | Reference matches Cognitive bulk rate; rebuild path lands ahead of full secondary-inline cost |
| 2026-04-21 | Reference | [ADR-030 Phase 2 parallel rebuild](docs/validations/adr-030-phase2-parallel-rebuild-2026-04-21.md) | Reference 100 M parallel rebuild via broadcast channel | Wall-clock NEUTRAL at 100 M (524 s vs 512 s sequential) — shipped, then reverted after Phase 5.2 trace exposed the hidden GC + lock cost |
| 2026-04-21 | Reference | [ADR-030 Phase 3 sort-insert](docs/validations/adr-030-phase3-sort-insert-2026-04-21.md) | Reference 100 M sort-insert via Array.Sort comparator | Wall-clock NEUTRAL at 100 M — shipped, then reverted; the *concept* (sort-insert) was right but the *implementation* (comparator-sort + 3.2 GB monolithic buffer) cost as much as it saved |
| 2026-04-21 | Reference | [Phase 5.2 trace + I/O measurement](docs/validations/adr-030-phase52-trace-2026-04-21.md) | dotnet-trace + iostat + 1.7.34 vs 1.7.37 A/B at 100 M | **Architectural pivot point.** Wall-clock equality was hiding a structural cost shift. 1.7.37 had 453 s GC.RunFinalizers + 552 s Monitor.Enter_Slowpath that 1.7.34 did not. Bottleneck identified as write amplification (~3× useful I/O), not CPU and not bandwidth. Drove the revert + the radix external-sort architecture |
| 2026-04-22 | Reference | [ADR-032 Phase 3 GPOS radix](docs/validations/adr-032-phase3-gpos-radix-2026-04-22.md) | GPOS rebuild via radix external sort at 100 M | Wall-clock 511 s → 457 s; GPOS portion ~3× faster (76 s → 24 s); peak iostat **2463 MB/s** (7.5× the baseline 327 MB/s) — sequential GPOS append finally hitting NVMe bandwidth |
| 2026-04-22 | Reference | [ADR-032 Phase 4 trigram radix](docs/validations/adr-032-phase4-trigram-radix-2026-04-22.md) | Trigram rebuild via radix at 100 M | Wall-clock 457 s → **48.64 s** (9.4× faster); trigram portion 17× faster; both indexes now sequential. Total rebuild speedup vs 1.7.38 baseline: **10.5×** |
| 2026-04-22 | Reference | [ADR-033 Phase 5 bulk radix](docs/validations/adr-033-phase5-bulk-radix-2026-04-22.md) | Bulk-load + rebuild gradient 1M → 1B | 1B end-to-end ~3h57m → **60m36s** (**3.92× combined**); rebuild contributes 13.8× while bulk holds steady at 1B (defensive at this scale). Three independent confirmations of the Phase 5.2 hypothesis across three code paths |
| 2026-04-25 | Reference | Phase 6 21.3B Wikidata end-to-end | Full `latest-all.nt` bulk + rebuild on a single M5 Max laptop, BCL-only | **Completed** 2026-04-25 22:32 at **85 h end-to-end** wall-clock. 21,260,051,924 triples ingested + sealed; ~2.5 TB physical on disk, 4.1 TB logical mmap (sparse on APFS); past Blazegraph WDQS reference ceiling (~12-13B) by ~63 % |
| 2026-04-26 | Reference | [21.3 B query-side validation](docs/validations/21b-query-validation-2026-04-26.md) | First measured queries against `wiki-21b-ref` | GSPO `LIMIT 10` 17 ms (6.5 ms parse + 10.6 ms exec); GPOS `wdt:P31` `LIMIT 10` 20 ms; both indexes correct at 21.3 B; cold-cache. **Capacity dimension of production hardening is empirical, sound finding** |
| 2026-04-27 | Reference | [ADR-035 Phase 7a 1 B](docs/validations/adr-035-phase7a-1b-2026-04-27.md) | First end-to-end test of Phase 7a metrics + Phase 7b bz2 streaming together at 1 B Reference | Bulk-load 55m22s @ 300 K triples/sec from `latest-all.ttl.bz2` (114 GB); GPOS rebuild 1m54s @ 8.66 M entries/sec; Trigram rebuild 11m44s @ 630 K entries/sec; bz2 decompression 33 MB/s with 4× headroom over parser; 22,256 JSONL records emitted across all four metric channels. **Phase 7a Completed.** |
| 2026-04-27 | Reference | [WDBench cold baseline (pre-fix)](docs/validations/wdbench-cold-baseline-21b-2026-04-27.jsonl) | First WDBench cold baseline against `wiki-21b-ref` | Surfaced two distinct issues: (a) executor cancellation gap — one c2rpqs query consumed 4h 51m wall-clock under a 60 s timeout cap, ~547 of 660 paths events silently lost (`docs/limits/cancellable-executor-paths.md`); (b) property-path parser grammar gaps — 12 of 1,199 queries (1.0 %) hitting three combinations the W3C SPARQL 1.1 conformance suite does not exercise (`docs/limits/property-path-grammar-gaps.md`). Both characterized in 48 hours; both fixed in 1.7.46/1.7.47 |
| 2026-04-29 | Reference | WDBench 1.7.47 cold baseline | Hardened-substrate WDBench against `wiki-21b-ref` (full Wikidata, 21.3 B triples) | 1,199 queries (660 paths + 539 c2rpqs). 11h 30m total wall. **0 parser failures**, **655/655 timeouts honored 60 s cap** (max 63.62 s, min 60.00 s — contract honored at scale). p25=4ms, p50=45ms, p75=1.39s, p90=12.82s, p95=29.85s, p99=49.50s, max=59.82s. Disclosure-marked baseline for Phase 7c optimization rounds; running on full Wikidata is intentional (substrate-capability claim, not the truthy subset that QLever/Virtuoso WDBench numbers use — see [memos/2026-04-30-latent-assumptions-from-qlever-comparison.md](memos/2026-04-30-latent-assumptions-from-qlever-comparison.md)). Sealed: `docs/validations/wdbench-paths-21b-2026-04-29-1747.jsonl` + `docs/validations/wdbench-c2rpqs-21b-2026-04-29-1747.jsonl` |
| 2026-05-06 | Reference | [Cleanup-hook 1M/10M/100M smoke](docs/validations/intermediate-cleanup-gradient-2026-05-06.md) | 1.7.49 phase-transition cleanup hook | All scales: `chunks_deleted == chunk_count` (1, 2, 18); `chunk_bytes_reclaimed` 164 MB / 1.6 GB / 16.4 GB; filesystem confirms `occurrences/` empty post-merge. Reframed as smoke + observability check (not perf gradient — change is `File.Delete` in a `finally`, off the hot path). Production claim deferred to cycle 9 |
| 2026-05-06 | Reference | [ADR-037 pipelined-spill gradient](docs/validations/adr-037-pipelined-spill-gradient-2026-05-06.md) | 1.7.49 sequential vs 1.7.50 pipelined A/B at 1 M / 10 M / 100 M | **100 M parser_blocked 95.0 s → 0.44 ms (216,000× reduction)**; end-to-end 285 s → 212 s (−25.6 %); queue_depth_at_handoff = 0 across all 18 handoffs (bound-1 sufficient); merge phase 30,149 → 31,057 ms (+3 %, within ±5 %). Per-spill sort+write costs rose 43–60 % under cross-thread contention but stay hidden behind parser-fill — net wall-clock drops. Memory cost ~2.5× working set (3.1 → 7.7 GB) from holding worker's snapshot concurrent with parser's accumulator. Hypothesis confirmed at every gradient scale; production validation = cycle 9 |
| 2026-05-09 | Cognitive | [Cycle 10 Phase 0 Cognitive gradient](docs/validations/cycle10-phase0-cognitive-gradient-2026-05-09.md) | Bulk-load 1 M / 10 M / 100 M from `latest-all.ttl.bz2` on 1.7.50 | 5.5 s / 51 s / 6 m 30 s; **256 K triples/sec at 100 M** from bzip2 Turtle; atom-store max probe distance 4 at 25 M atoms; smoke queries only (bounded SELECT 18 ms, subject lookup 1 ms). **Most recent Cognitive measurement**; it predates the current SPARQL engine (ADR-047) |
| 2026-05-09 | Reference | [Cycle 9 21.3 B production validation](docs/validations/adr-037-cycle9-21b-2026-05-09.md) | 1.7.50 (pipelined spill + cleanup hook) full Wikidata Reference + Sorted | **Completed** 2026-05-08 06:54 UTC at **35 h 35 m end-to-end** (vs cycle 8's 46 h with intervention, −22.6 %). Parser 9 h 18 m (vs 14 h 15 m, −4 h 57 m measured = ADR-037 production validation); merge 15 h 21 m (wash); drain ~1 h 40 m; Phase B (GPOS + trigram rebuild) 9 h 15 m. `parser_blocked_on_spill_ms = 78.9 / 0.000236 %` at production scale — falsifiable hypothesis confirmed. Cleanup hook reclaimed 3.96 TB at end-of-merge (vs cycle 8's manual `rm -rf` requirement, eliminated). Substrate identity vs cycle 8: byte-identical (4,005,235,528 atoms, 17,029,283,265 GPOS entries, 7,472,855,623 trigram entries; total ~2.13 TB). [ADR-037](docs/adrs/mercury/ADR-037-pipelined-spill-bulk-builder.md) advanced to **Completed**. Limits [`spill-blocks-parser`](docs/limits/spill-blocks-parser.md) and [`intermediate-cleanup-deferred-to-run-end`](docs/limits/intermediate-cleanup-deferred-to-run-end.md) Resolved at production scale |
| 2026-05-09 | Reference | [WDBench cycle 9 cold baseline](docs/validations/wdbench-paths-21b-2026-05-08-cycle9.jsonl) + [c2rpqs](docs/validations/wdbench-c2rpqs-21b-2026-05-08-cycle9.jsonl) | 1.7.50 cold-cache run against `wiki-21b-ref-r2` | 1,199 queries (660 paths + 539 c2rpqs), 60 s timeout. **588 completed (vs cycle 8's 544 — 44 hard queries that timed out in cycle 8 completed under 60 s in cycle 9)**. **10 h 44 m total wall-clock (vs cycle 8's 11 h 30 m, −7 %)**. Aggregated completed-only: p25=2.69 ms, p50=65 ms, p75=966 ms, p90=8.68 s, p95=23.13 s, p99=48.24 s, max=58.57 s. p50 shift +44 % is sample-composition (44 newly-completed slow queries); p75–p99 all dropped 23–32 %. 0 query failures, 0 parser failures across 2,398 queries (cycle 8 + cycle 9). Substrate `wiki-21b-ref-r2` deleted post-baseline to free disk for cycle 10 |
| 2026-05-13 | Reference | [Cycle 10 Phase 3 r4 21.3 B production validation](docs/validations/cycle10-phase3-r4-21b-2026-05-12.md) | 1.7.57 (ADR-038 + ADR-039 + MPHF instrumentation) full Wikidata Reference + Sorted | **Completed** 2026-05-12 18:49 UTC at **23 h 56 m 50 s end-to-end** (vs cycle 9's 35 h 35 m on 1.7.50, **−11 h 38 m / −32.7 %**). Phase breakdown: parse 9 h 17 m (cycle 9 parity), merge 2 h 41 m, MPHF construction **54 m 29 s** (first production-scale instrumented measurement on 4.005 B atoms — 25 levels, 0 dense fallback, placement_ratio 0.6065 = BBHash theoretical for γ=2.0), GSPO drain ~1 h 38 m, GPOS rebuild 55 m 27 s, Trigram rebuild 8 h 30 m 30 s. Substrate output identical to cycle 9 (4,005,235,528 atoms, 17,029,283,265 GPOS entries, 7,472,855,623 trigram entries) plus new MPHF surface (1.75 GB `atoms.mphf` blob + 16.0 GB `atoms.idx` translation table). [ADR-038](docs/adrs/mercury/ADR-038-merge-phase-read-side.md) and [ADR-039](docs/adrs/mercury/ADR-039-mphf-on-sealed-atom-set.md) both advanced to **Completed**. Three open follow-ups for cycle 11: `ExternalSorter` FD pool integration (trigram rebuild held 8,192 chunks at 17 % headroom to the ~10K launchd ceiling), ADR-041 cleanup-on-exception (third orphaning occurrence), ADR-040 readahead memory adaptive sizing. Cycle 10 retrospective: three abort/retry attempts (1.7.53 int32 / 1.7.54 BBHash non-convergence / 1.7.55 Claude-induced deploy-during-run crash) before clean r4 — discipline-rule memorialized as `feedback_no_deploy_during_long_running_process.md` |
| 2026-05-14 | Reference | [Truthy r1 8.17 B production validation](docs/validations/truthy-r1-2026-05-14.md) | 1.7.57 truthy Wikidata bulk-load + rebuild against `wiki-truthy-ref-r1` | **Completed** 2026-05-14 09:21 local at **14 h 13 m 28 s end-to-end** (vs cycle 10 r4 full's 23 h 56 m 50 s on the same 1.7.57 substrate, **59.4 % of full's wall-clock at 38.3 % of full's triple count**). Phase breakdown: parse 4 h 40 m (485 K/sec, 8,171,214,990 triples), merge 55 m 47 s, MPHF 13 m 56 s (1,794,118,956 atoms, **23 levels, 0 dense fallback engaged**, placement_ratio 0.6065 = BBHash theoretical for γ=2.0), GSPO drain ~25 m, GPOS rebuild 24 m 06 s (8,165,609,185 entries), Trigram rebuild 7 h 24 m 07 s (**6,776,819,228 entries — 90.7 % of full's 7,472,855,623 at only 38.3 % triple-count ratio**). Final store ~1.0 TB on disk. **Key finding**: trigram entries truthy/full = 90.7 % at triple-count ratio 38.3 % = ~2.4× more literal-density per triple in truthy. Trigram-phase prediction needs literal-volume scaling, not triple-count scaling. **Dump-date confounder**: truthy is 2026-05-08, full is 2026-04-03 (~5 weeks newer) — some of the 90.7 % uplift is Wikidata growth in that interval; same-dated comparison would give ~75-83 %. parser_blocked_on_spill_ms 6.87 = 0.00000041 % blocked (41× cleaner than full's 282.9). Apples-to-apples companion to cycle 10 r4 full — paired publication-ready record for external Wikidata-community comparison vs QLever/Virtuoso/WDBench published numbers (which use truthy) |
| 2026-05-16 | Reference | WGPB step C — 850 queries × 17 patterns against MillenniumDB 2018 reduced-truthy substrate | 1.7.57 bulk-load + 17 wdbench runs against `wiki-wgpb-ref-r1` | **Substrate built in 4m 30s** (bulk-load 3m 40s + rebuild-indexes 50s) for the 593 MB filtered Wikidata dump (~100-200 M triples estimate). **17 WGPB pattern categories run in ~5 min cumulative**: **849/850 completed** (only J4 had 1 timeout); 0 failures. Per-category median latency 59-61 ms; p95 typically 2-3 s. Dataset: `wikidata-wcg-filtered.nt.bz2` from Zenodo 4035223 (November 2018 reduced-truthy Wikidata, rare + very-common predicates excluded). **NOT cross-comparable with truthy r1 / cycle 10 r4 wall-clock** (different dump date + filtered scope); this is the apples-to-apples measurement vs published WGPB / MillenniumDB numbers. Substrate cancellation discipline cumulative across the 1.7.x line: 0 failures across 8,564 unique query × substrate executions (WDBench paired matrix 5,316 + cycle 8 + cycle 9 prior 2,398 + WGPB 850) |
| 2026-05-16 | Reference | WDBench extended coverage — paired matrix (5 categories × 2 substrates) | 1.7.57 cold-cache all-categories run | **Coverage gap closed**: cycle 8 → cycle 9 → cycle 10 r4 → truthy r1 originally ran only paths + c2rpqs (1,199 of 2,658 queries, 45%); 2026-05-14 → 2026-05-16 extended coverage ran the missing 1,459 queries (single_bgps + multiple_bgps + opts) on both substrates. **Paired-substrate matrix complete**: 5,316 total queries (2,658 × 2 substrates), **0 failures**. Per-substrate totals: truthy r1 1,391/2,658 (52.3%) in 22h 48m cumulative; cycle 10 r4 full 1,384/2,658 (52.1%) in 23h 02m cumulative. **Key finding**: substrates are nearly indistinguishable on WDBench — completion counts within 0.5% across every category, wall-clocks within 2% per category. Data-scale difference (21.3 B full vs 8.17 B truthy) doesn't materially affect query performance; WDBench's 60s timeout cap is what gates completion in both. Per category: single_bgps 272/271 (15m/20m), multiple_bgps 220/220 (497m/500m), paths 396/395 (275m/276m), c2rpqs 225/221 (342m/345m), opts 278/277 (239m/241m). Cumulative cancellation-contract discipline: **0 failures across 8,564 unique query × substrate executions** on the cumulative 1.7.x substrate line (5,316 here + cycle 8 + cycle 9 prior 2,398 + WGPB 850) |
| 2026-05-13 | Reference | [WDBench cycle 10 r4 cold baseline (paths + c2rpqs only)](docs/validations/wdbench-paths-21b-2026-05-13-cycle10r4.jsonl) + [c2rpqs](docs/validations/wdbench-c2rpqs-21b-2026-05-13-cycle10r4.jsonl) | 1.7.57 cold-cache run against `wiki-21b-ref-r4` | 1,199 queries (660 paths + 539 c2rpqs), 60 s timeout. **616 completed (vs cycle 9's 588 — 28 more queries finished under 60 s)**. **10 h 21 m 32 s total wall-clock (vs cycle 9's 10 h 44 m on 1.7.50, −22 m 28 s / −3.5 %)**. paths: 4 h 36 m 24 s, 395 completed (cycle 9: 4 h 45 m, 390 completed), p50=13.3 ms, p95=9.38 s, p99=47.34 s. c2rpqs: 5 h 45 m 8 s, 221 completed (cycle 9: 5 h 59 m, 198 completed), p50=145 ms, p95=42.77 s, p99=57.14 s. 0 query failures, 0 parser failures across **3,597 paths+c2rpqs (query × substrate) executions combined cycle 8 + cycle 9 + cycle 10 r4** (1,199 unique queries × 3 substrate generations) — cancellation contract honored at scale across three substrate generations. **Disclosure (per [memos/2026-04-30-latent-assumptions-from-qlever-comparison.md](docs/memos/2026-04-30-latent-assumptions-from-qlever-comparison.md)):** Mercury substrate is full Wikidata; WDBench query set is authored for truthy. Internally cycle-9-comparable; NOT directly comparable to published WDBench numbers (which run truthy + truthy-queries). The chain `&&` between paths and c2rpqs broke after paths (runner exits exit=0 clean, cause unidentified) — c2rpqs ran in a fresh process with cold mmap views (incl. the new MPHF + idx files), no measurable regression. Open follow-up: investigate the chain breakage |

## Maintenance Instructions

Update the counts before a release, or after a change that moves them. This file holds measurements only: what
changed and why goes in the release notes and the CHANGELOG, not here.

```bash
# Lines of C# in a folder, without bin/ and obj/
cnt() { find "$@" -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" -print0 | xargs -0 cat | wc -l; }
cnt src/Mercury                     # Mercury total; also per subfolder: cnt src/Mercury/Sparql …
for p in src/*/ tests/*/; do echo "$p $(cnt $p)"; done

# Documentation lines
find docs \( -name '*.md' -o -name '*.ttl' \) -print0 | xargs -0 cat | wc -l

# Test counts per project and per W3C suite: two runs with TRX reports into one directory.
# xunit projects take --report-xunit-trx; the MSTest projects (DrHook.Engine.IntegrationTests,
# DrHook.Engine.IntegrationTargets.Mtp) take --report-trx. In each run the projects of the other kind exit
# with code 5 and run nothing, so each run reports a non-success exit code: read the TRX files, not the summary.
dotnet test --report-xunit-trx --results-directory /tmp/trx
dotnet test --report-trx --results-directory /tmp/trx
```

Each TRX file names its test assembly in the `storage` attribute of every `UnitTest`. The W3C counts are the
results of each conformance test method (`Turtle_Eval`, `NTriples_CanonicalForm`, `Sparql11_QueryEval`, …); a SPARQL
test whose id contains `data-r2` is from the SPARQL 1.0 suite.
