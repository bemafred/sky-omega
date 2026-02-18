# Sky Omega Statistics

Codebase metrics are tracked over time. Update after significant changes.

**Last updated:** 2026-02-18

## Line Counts

### Source Code

| Component | Lines | Description |
|-----------|------:|-------------|
| **Mercury (total)** | **74,600** | Knowledge substrate |
| ├─ Sparql | 44,743 | SPARQL parser, executor, protocol |
| ├─ JsonLd | 7,237 | JSON-LD parser and writer |
| ├─ Storage | 6,014 | B+Tree indexes, AtomStore, WAL |
| ├─ Turtle | 4,017 | Turtle parser and writer |
| ├─ RdfXml | 3,032 | RDF/XML parser and writer |
| ├─ TriG | 2,836 | TriG parser and writer |
| ├─ NQuads | 1,476 | N-Quads parser and writer |
| ├─ NTriples | 1,244 | N-Triples parser and writer |
| ├─ Facades | 793 | SparqlEngine, RdfEngine |
| ├─ Owl | 566 | OWL/RDFS reasoner |
| └─ Rdf | 490 | Core RDF types |
| **Mercury.Solid (total)** | **4,459** | W3C Solid Protocol |
| ├─ Http | 1,365 | Resource, Container, Patch handlers |
| ├─ N3 | 1,348 | N3 Patch parser and executor |
| ├─ AccessControl | 894 | WAC and ACP implementations |
| ├─ Models | 297 | SolidResource, SolidContainer |
| └─ SolidServer | 481 | HTTP server |
| **Mercury Runtime** | **3,540** | Runtime + Abstractions |
| **Mercury Tool Libraries** | **1,471** | Sparql.Tool + Turtle.Tool |
| **Mercury CLIs** | **1,416** | mercury, mercury-mcp, mercury-sparql, mercury-turtle |
| **Mercury.Pruning** | **1,275** | Copy-and-switch pruning + PruneEngine |
| **Minerva** | **—** | Thought substrate (planned) |

### Tests

| Project | Lines | Test Cases |
|---------|------:|----------:|
| Mercury.Tests | 49,810 | 3,970 |
| Mercury.Solid.Tests | 455 | 25 |
| Minerva.Tests | — | — |

### Benchmarks

| Project | Lines | Classes |
|---------|------:|--------:|
| Mercury.Benchmarks | 3,408 | 97 |
| Minerva.Benchmarks | — | — |

### Examples

| Project | Lines |
|---------|------:|
| Mercury.Examples | 851 |
| Minerva.Examples | — |

### Documentation

| Category | Lines |
|----------|------:|
| All docs (*.md, *.ttl) | 25,908 |
| CLAUDE.md | 881 |

## Totals

| Category | Lines |
|----------|------:|
| Source code | ~86,764 |
| Tests | ~50,265 |
| Benchmarks | ~3,408 |
| Examples | ~851 |
| Documentation | ~25,908 |
| **Grand total** | **~167,196** |

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

### Storage Performance (reference)

| Operation | Throughput | Notes |
|-----------|------------|-------|
| Single write | ~250-300/sec | fsync per write |
| Batch 1K | ~25,000+/sec | 1 fsync per batch |
| Batch 10K | ~100,000+/sec | Amortized fsync |

### Query Performance (reference)

| Operation | Throughput | Notes |
|-----------|------------|-------|
| Point-in-time (cached) | ~100K queries/sec | Hot page cache |
| Point-in-time (cold) | ~5K queries/sec | Disk access |
| Range scan | ~200K triples/sec | Sequential read |
| Evolution scan | ~500K triples/sec | Full history |

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
