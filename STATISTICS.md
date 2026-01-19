# Sky Omega Statistics

Codebase metrics tracked over time. Update after significant changes.

**Last updated:** 2026-01-19

## Line Counts

### Source Code

| Component | Lines | Description |
|-----------|------:|-------------|
| **Mercury (total)** | **58,066** | Knowledge substrate |
| ├─ Sparql | 29,742 | SPARQL parser, executor, protocol |
| ├─ JsonLd | 7,237 | JSON-LD parser and writer |
| ├─ Storage | 5,410 | B+Tree indexes, AtomStore, WAL |
| ├─ Turtle | 3,944 | Turtle parser and writer |
| ├─ RdfXml | 3,032 | RDF/XML parser and writer |
| ├─ TriG | 2,836 | TriG parser and writer |
| ├─ NQuads | 1,476 | N-Quads parser and writer |
| ├─ NTriples | 1,229 | N-Triples parser and writer |
| ├─ Owl | 566 | OWL/RDFS reasoner |
| └─ Rdf | 442 | Core RDF types |
| **Mercury.Pruning** | **1,152** | Copy-and-switch pruning |
| **Mercury CLIs** | **317** | Turtle and SPARQL CLI demos |
| **Minerva** | **—** | Thought substrate (planned) |

### Tests

| Project | Lines | Test Cases |
|---------|------:|----------:|
| Mercury.Tests | 41,499 | ~1,950 |
| Minerva.Tests | — | — |

### Benchmarks

| Project | Lines |
|---------|------:|
| Mercury.Benchmarks | 2,966 |
| Minerva.Benchmarks | — |

### Examples

| Project | Lines |
|---------|------:|
| Mercury.Examples | 779 |
| Minerva.Examples | — |

### Documentation

| Category | Lines |
|----------|------:|
| All docs (*.md) | 15,025 |
| CLAUDE.md | 764 |

## Totals

| Category | Lines |
|----------|------:|
| Source code | ~59,535 |
| Tests | ~41,499 |
| Benchmarks | ~2,966 |
| Examples | ~779 |
| Documentation | ~15,025 |
| **Grand total** | **~119,804** |

## W3C Conformance

Target: 100% conformance across all RDF formats and JSON-LD.

See [ADR-010](docs/adrs/mercury/ADR-010-w3c-test-suite-integration.md) for integration details.

| Format | Passing | Total | Coverage | Notes |
|--------|--------:|------:|---------:|-------|
| Turtle 1.2 | 309 | 309 | **100%** | Full conformance |
| TriG 1.2 | 352 | 352 | **100%** | Full conformance |
| JSON-LD 1.1 | 461 | 461 | **100%** | 6 skipped: 1.0-only (4), generalized RDF (2) |
| RDF/XML 1.1 | 166 | 166 | **100%** | Full conformance |
| N-Quads 1.2 | 87 | 87 | **100%** | Full conformance |
| N-Triples 1.2 | 70 | 70 | **100%** | Full conformance |
| SPARQL 1.1 Syntax | 73 | 103 | **71%** | 63/63 positive, 10/40 negative |
| SPARQL 1.1 Query | 100 | 224 | **46%** | 8 skipped, 116 failing (see [ADR-012](docs/adrs/mercury/ADR-012-conformance-fix-plan.md)) |
| SPARQL 1.1 Update | 94 | 94 | **100%** | Full conformance |
| **Total** | **1,712** | **1,866** | **92%** | SPARQL Query conformance in progress |

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
