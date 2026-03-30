# Sky Omega Statistics

Codebase metrics are tracked over time. Update after significant changes.

**Last updated:** 2026-03-30

## Line Counts

### Source Code

| Component | Lines | Description |
|-----------|------:|-------------|
| **Mercury (total)** | **74,753** | Knowledge substrate |
| ├─ Sparql | 44,773 | SPARQL parser, executor, protocol |
| ├─ JsonLd | 7,237 | JSON-LD parser and writer |
| ├─ Storage | 6,132 | B+Tree indexes, AtomStore, WAL |
| ├─ Turtle | 4,017 | Turtle parser and writer |
| ├─ RdfXml | 3,032 | RDF/XML parser and writer |
| ├─ TriG | 2,836 | TriG parser and writer |
| ├─ NQuads | 1,476 | N-Quads parser and writer |
| ├─ NTriples | 1,244 | N-Triples parser and writer |
| ├─ Facades | 792 | SparqlEngine, RdfEngine |
| ├─ Owl | 566 | OWL/RDFS reasoner |
| └─ Rdf | 490 | Core RDF types |
| **Mercury.Solid (total)** | **4,459** | W3C Solid Protocol |
| ├─ Http | 1,365 | Resource, Container, Patch handlers |
| ├─ N3 | 1,348 | N3 Patch parser and executor |
| ├─ AccessControl | 894 | WAC and ACP implementations |
| ├─ Models | 297 | SolidResource, SolidContainer |
| └─ SolidServer | 481 | HTTP server |
| **Mercury Runtime** | **3,503** | Runtime + Abstractions |
| **Mercury Tool Libraries** | **1,471** | Sparql.Tool + Turtle.Tool |
| **Mercury CLIs** | **1,516** | mercury, mercury-mcp, mercury-sparql, mercury-turtle |
| **Mercury.Pruning** | **1,276** | Copy-and-switch pruning + PruneEngine |
| **DrHook (total)** | **2,167** | Runtime observation substrate |
| ├─ Core | 1,802 | EventPipe observation + DAP stepping + breakpoint registry |
| └─ DrHook.Mcp | 365 | MCP server shim (19 tools, incl. step_test) |
| **Minerva** | **—** | Thought substrate (planned) |

### Tests

| Project | Lines | Test Cases |
|---------|------:|----------:|
| Mercury.Tests | 49,961 | 3,978 |
| Mercury.Solid.Tests | 455 | 25 |
| DrHook.Tests | 277 | 23 |
| Minerva.Tests | — | — |

### Benchmarks

| Project | Lines | Methods |
|---------|------:|--------:|
| Mercury.Benchmarks | 3,408 | 74 |
| Minerva.Benchmarks | — | — |

### Examples

| Project | Lines |
|---------|------:|
| Mercury.Examples | 851 |
| drhook-target.cs | 155 |
| drhook-verify.cs | 21 |
| Minerva.Examples | — |

### Documentation

| Category | Lines |
|----------|------:|
| All docs (*.md, *.ttl) | 29,558 |
| CLAUDE.md | 271 |

## Totals

| Category | Lines |
|----------|------:|
| Source code | ~88,850 |
| Tests | ~50,693 |
| Benchmarks | ~3,408 |
| Examples | ~1,027 |
| Documentation | ~29,558 |
| **Grand total** | **~173,536** |

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
| Turtle parse (zero-GC) | ~1.1M triples/sec | 57 KB total allocation |
| Batch write 10K | ~1,043 triples/sec | Single fsync, SSD-bound |

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
