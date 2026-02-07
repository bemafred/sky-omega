# Changelog

All notable changes to Sky Omega will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## What's Next

**Sky Omega 2.0.0** will introduce cognitive components: Lucy (semantic memory), James (orchestration), Sky (LLM interaction), and Minerva (local inference).

---

## [1.1.0] - 2026-02-07

Global tool packaging, persistent stores, and Microsoft MCP SDK integration.

### Added

#### Global Tool Packaging (ADR-019)
- **`mercury`** - SPARQL CLI installable as .NET global tool
- **`mercury-mcp`** - MCP server installable as .NET global tool
- **`mercury-sparql`** - SPARQL query engine demo as global tool
- **`mercury-turtle`** - Turtle parser demo as global tool
- **Install scripts** - `tools/install-tools.sh` (bash) and `tools/install-tools.ps1` (PowerShell)

#### Persistent Store Defaults
- **`MercuryPaths`** - Well-known persistent store paths per platform
  - macOS: `~/Library/SkyOmega/stores/{name}/`
  - Linux/WSL: `~/.local/share/SkyOmega/stores/{name}/`
  - Windows: `%LOCALAPPDATA%\SkyOmega\stores\{name}\`
- **`mercury`** defaults to persistent store at `MercuryPaths.Store("cli")`
- **`mercury-mcp`** defaults to persistent store at `MercuryPaths.Store("mcp")`

#### Claude Code Integration
- **`.mcp.json`** - Dev-time MCP config for Claude Code at repo root
- **User-scope install** - `claude mcp add --scope user mercury -- mercury-mcp`

### Changed

#### Microsoft MCP SDK Migration
- **Replaced hand-rolled `McpProtocol.cs`** (~494 lines) with official `ModelContextProtocol` NuGet package (0.8.0-preview.1)
- **`[McpServerToolType]`** attribute-based tool registration via `MercuryTools.cs`
- **Hosted service model** - PipeServer and SparqlHttpServer as `IHostedService` implementations
- **`Microsoft.Extensions.Hosting`** - Proper application lifecycle management

#### CLI Library Extraction (ADR-018)
- Extracted CLI logic into testable libraries (`Mercury.Sparql.Tool`, `Mercury.Turtle.Tool`)

### Documentation

- **ADR-019** - Global Tool Packaging and Persistent Stores
- **ADR-018** - CLI Library Extraction
- **Mercury ADR index** updated with all 20 ADRs and correct statuses

---

## [1.0.0] - 2026-01-31

Mercury reaches production-ready status with complete W3C SPARQL 1.1 conformance.

### Added

#### SPARQL Update Sequences
- **Semicolon-separated operations** - Multiple updates in single request (W3C spec [29])
- **`ParseUpdateSequence()`** - Returns `UpdateOperation[]` for batched execution
- **`UpdateExecutor.ExecuteSequence()`** - Static method for atomic sequence execution
- **Prologue inheritance** - PREFIX declarations carry across sequence operations

#### W3C Update Test Graph State Validation
- **Expected graph comparison** - Tests now validate resulting store state, not just execution success
- **Named graph support** - `ut:data` and `ut:graphData` parsing from manifests
- **`ExtractGraphFromStore()`** - Enumerate store contents for comparison
- **Blank node isomorphism** - Correct matching via `SparqlResultComparer.CompareGraphs()`

#### Service Description Enrichment
- **`sd:feature` declarations** - PropertyPaths, SubQueries, Aggregates, Negation
- **`sd:extensionFunction`** - text:match full-text search
- **RDF output formats** - Turtle, N-Triples, RDF/XML for CONSTRUCT/DESCRIBE

### Changed

#### W3C Conformance (100% Core Coverage)
- **SPARQL 1.1 Query**: 421/421 passing (100%)
- **SPARQL 1.1 Update**: 94/94 passing (100%)
- **All tests** now validate actual graph contents, not just success status

### Fixed

#### SPARQL 1.1 CONSTRUCT/Aggregate Gaps (3 tests)
- **`constructlist`** - RDF collection `(...)` syntax in CONSTRUCT templates now generates proper `rdf:first/rdf:rest` chains
- **`agg-empty-group-count-graph`** - COUNT without GROUP BY inside GRAPH ?g now correctly returns count per graph (including 0 for empty graphs)
- **`bindings/manifest#graph`** - VALUES inside GRAPH binding same variable as graph name now correctly filters/expands based on UNDEF vs specific values

#### SPARQL 1.1 Update Edge Cases (10 tests)
- **USING clause dataset restriction** (4 tests) - USING without USING NAMED now correctly restricts named graph access
- **Blank node identity** (4 tests) - Same bnode label across statements now creates unique nodes per W3C scoping rules
- **DELETE/INSERT with mixed UNION branches** (2 tests) - UNION containing both GRAPH and default patterns now executes correctly via `_graphPatternFlags` tracking

### Documentation

- **ADR-002** status changed to "Accepted" - 1.0.0 operational scope achieved
- Release checklist complete per ADR-002 success criteria

---

## [0.6.2] - 2026-01-27

Critical stack overflow fix for parallel test execution.

### Fixed

#### Stack Overflow Resolution (ADR-011)
- **QueryResults reduced from 90KB to 6KB** (93% reduction)
  - Changed `TemporalResultEnumerator` from `ref struct` to `struct`
  - Pooled enumerator arrays in `MultiPatternScan` and `CrossGraphMultiPatternScan`
  - Boxed `GraphPattern` (~4KB) to move from stack to heap
- **All scan types dramatically reduced**:
  - `MultiPatternScan`: 18,080 → 384 bytes (98% reduction)
  - `DefaultGraphUnionScan`: 33,456 → 1,040 bytes (97% reduction)
  - `CrossGraphMultiPatternScan`: 15,800 → 96 bytes (99% reduction)
- **Parallel test execution restored** - Previously limited to single thread as workaround

### Changed

- Re-enabled parallel test execution in xunit.runner.json
- All 3,824 tests pass with parallel execution

### Documentation

- **ADR-011** completed - QueryResults Stack Reduction via Pooled Enumerators
- **StackSizeTests** added - Enforces size constraints to prevent regression

---

## [0.6.1] - 2026-01-26

Full W3C SPARQL 1.1 Query conformance achieved.

### Fixed

#### CONSTRUCT Query Fixes (5 tests now passing)
- **sq12** - Subquery computed expressions (CONCAT, STR) now propagate to CONSTRUCT output
  - Added `HasRealAggregates` to distinguish aggregates from computed expressions
  - Implemented per-row expression evaluation in subquery execution
- **sq14** - `a` shorthand (rdf:type) now correctly expanded in CONSTRUCT templates
- **constructwhere02** - Duplicate triple deduplication in CONSTRUCT WHERE
- **constructwhere03** - Blank node shorthand handling in CONSTRUCT WHERE
- **constructwhere04** - FROM clause graph context in CONSTRUCT WHERE

### Changed

#### W3C Conformance (100% core coverage)
- **SPARQL 1.1 Query**: 418/418 passing (previously 410/418)
- **SPARQL 1.1 Update**: 94/94 passing (unchanged)
- **Total W3C tests**: 1,872 passing

### Remaining Known Limitations
- `constructlist` - RDF collection syntax in CONSTRUCT templates (high complexity)
- `agg-empty-group-count-graph` - COUNT without GROUP BY inside GRAPH (high complexity)
- `bindings/manifest#graph` - VALUES binding GRAPH variable (high complexity)

---

## [0.6.0-beta.1] - 2026-01-26

Major W3C conformance milestone and CONSTRUCT/DESCRIBE content negotiation.

### Added

#### Content Negotiation for CONSTRUCT/DESCRIBE
- **RDF format negotiation** - Accept header parsing with quality values
- **Turtle output** (default) - Human-readable with prefix support
- **N-Triples output** - Canonical format for interoperability
- **RDF/XML output** - XML-based serialization

#### W3C Test Infrastructure
- **Graph isomorphism** - Backtracking search for blank node mapping
- **RDF result parsing** - Support for .ttl, .nt, .rdf expected results
- **CONSTRUCT test validation** - Previously skipped tests now enabled

### Changed

#### W3C Conformance (99% coverage)
- **Total tests**: 1,872 → 3,464 (W3C + internal)
- **SPARQL 1.1 Query**: 96% (215/224) - 9 skipped for SERVICE/entailment
- **SPARQL 1.1 Update**: 100% (94/94)
- **All RDF formats**: 100% conformance maintained

### Fixed

#### SPARQL Conformance Fixes
- **Unicode handling** - Supplementary characters (non-BMP) via System.Text.Rune
- **Aggregate expressions** - COUNT, AVG error propagation, HAVING multiple conditions
- **BIND scoping** - Correct variable visibility in nested groups
- **EXISTS/NOT EXISTS** - Evaluation in ExecuteToMaterialized path
- **CONCAT/STRBEFORE/STRAFTER** - Language tag and datatype handling
- **GRAPH parsing** - Nested group pattern handling
- **IN/NOT IN** - Empty patterns and expressions
- **GROUP BY** - Expression type inference

#### Parser Fixes
- **Turtle Unicode escapes** - \U escape sequences beyond BMP
- **Named blank node matching** - Consistent across parsers
- **Empty string literals** - Correct handling in result comparison

### Documentation

- **ADR-002** - Sky Omega 1.0.0 Operational Scope defined
- **ADR-010** - W3C conformance status updated
- **ADR-012** - Conformance fixes documented

---

## [0.5.0-beta.1] - 2026-01-01

First versioned release of Sky Omega Mercury - a semantic-aware storage and query engine with zero-GC performance design.

### Added

#### Storage Layer
- **QuadStore** - Multi-index quad store with GSPO ordering and named graph support
- **B+Tree indexes** - Page-cached indexes with LRU eviction (clock algorithm)
- **Write-Ahead Logging (WAL)** - Crash-safe durability with hybrid checkpoint triggering
- **AtomStore** - String interning with memory-mapped storage
- **Batch write API** - High-throughput bulk loading (~100,000 triples/sec)
- **Bitemporal support** - ValidFrom/ValidTo/TransactionTime on all quads
- **Disk space enforcement** - Configurable minimum free disk space checks

#### RDF Parsers (6 formats)
- **Turtle** - RDF 1.2 with RDF-star support, zero-GC handler API
- **N-Triples** - Zero-GC handler API + async enumerable
- **N-Quads** - Zero-GC handler API + async enumerable
- **TriG** - Full named graph support
- **RDF/XML** - Streaming parser
- **JSON-LD** - Near zero-GC with context handling

#### RDF Writers (6 formats)
- **Turtle** - With prefix support and subject grouping
- **N-Triples** - Streaming output
- **N-Quads** - Named graph serialization
- **TriG** - Named graph serialization with prefixes
- **RDF/XML** - Full namespace support
- **JSON-LD** - Compact output with context

#### SPARQL Engine
- **Query types** - SELECT, ASK, CONSTRUCT, DESCRIBE
- **Graph patterns** - Basic, OPTIONAL, UNION, MINUS, GRAPH (IRI and variable)
- **Subqueries** - Single and multiple nested SELECT
- **Federated queries** - SERVICE clause with ISparqlServiceExecutor
- **Property paths** - `^iri`, `iri*`, `iri+`, `iri?`, `path/path`, `path|path`
- **Filtering** - FILTER, VALUES, EXISTS, NOT EXISTS, IN, NOT IN
- **40+ built-in functions**:
  - String: STR, STRLEN, SUBSTR, CONTAINS, STRSTARTS, STRENDS, CONCAT, UCASE, LCASE, etc.
  - Numeric: ABS, ROUND, CEIL, FLOOR
  - DateTime: NOW, YEAR, MONTH, DAY, HOURS, MINUTES, SECONDS, TZ, TIMEZONE
  - Hash: MD5, SHA1, SHA256, SHA384, SHA512
  - UUID: UUID, STRUUID (time-ordered UUID v7)
  - Type checking: isIRI, isBlank, isLiteral, isNumeric, BOUND
  - RDF terms: LANG, DATATYPE, LANGMATCHES, IRI, STRDT, STRLANG, BNODE
- **Aggregation** - GROUP BY, HAVING, COUNT, SUM, AVG, MIN, MAX, GROUP_CONCAT, SAMPLE
- **Modifiers** - DISTINCT, REDUCED, ORDER BY (ASC/DESC), LIMIT, OFFSET
- **Dataset clauses** - FROM, FROM NAMED with cross-graph join support
- **SPARQL-star** - Quoted triples with automatic reification expansion
- **SPARQL EXPLAIN** - Query execution plan analysis

#### SPARQL Update
- INSERT DATA, DELETE DATA
- DELETE WHERE, DELETE/INSERT WHERE (WITH clause)
- CLEAR, DROP, CREATE
- COPY, MOVE, ADD
- LOAD (with size and triple limits)

#### Temporal SPARQL Extensions
- **AS OF** - Point-in-time queries
- **DURING** - Range queries for overlapping data
- **ALL VERSIONS** - Complete history retrieval

#### Query Optimization
- **Statistics-based join reordering** - 10-100x improvement on multi-pattern queries
- **Predicate pushdown** - 5-50x improvement via FilterAnalyzer
- **Plan caching** - LRU cache with statistics-based invalidation
- **Cardinality estimation** - Per-predicate statistics collection

#### Full-Text Search
- **TrigramIndex** - UTF-8 trigram inverted index (opt-in)
- **text:match()** - SPARQL FILTER function
- **Unicode case-folding** - Supports Swedish å, ä, ö and other languages

#### OWL/RDFS Reasoning
- **Forward-chaining inference** - Materialization with fixed-point iteration
- **10 inference rules**:
  - RDFS: subClassOf, subPropertyOf, domain, range
  - OWL: TransitiveProperty, SymmetricProperty, inverseOf, sameAs, equivalentClass, equivalentProperty

#### SPARQL Protocol
- **HTTP Server** - W3C SPARQL 1.1 Protocol (BCL HttpListener)
- **Content negotiation** - JSON, XML, CSV, TSV result formats
- **Service description** - Turtle endpoint metadata

#### Pruning System
- **PruningTransfer** - Dual-instance copy-and-switch compaction
- **Filtering** - GraphFilter, PredicateFilter, CompositeFilter
- **History modes** - FlattenToCurrent, PreserveVersions, PreserveAll
- **Verification** - DryRun, checksums, audit logging

#### Infrastructure
- **ILogger abstraction** - Zero-allocation hot path, NullLogger for production
- **IBufferManager** - Unified buffer allocation with PooledBufferManager
- **Content negotiation** - RdfContentNegotiator for format detection

### Architecture

- **Zero external dependencies** - Core Mercury library uses BCL only
- **Zero-GC design** - ref struct parsers, ArrayPool buffers, streaming APIs
- **Thread-safe** - ReaderWriterLockSlim with documented locking patterns
- **.NET 10 / C# 14** - Modern language features and runtime

### Testing

- **1,785 passing tests** across 62 test files
- **Component coverage**: Storage, SPARQL, parsers, writers, temporal, reasoning, concurrency
- **Zero-GC compliance tests** - Allocation validation

### Benchmarks

- **8 benchmark classes** - BatchWrite, Query, SPARQL, Temporal, Parsers, Filters, Concurrent
- **Performance baselines established** - Documented in CLAUDE.md

### Known Limitations

- SERVICE clause does not yet support joining with local patterns
- Multiple SERVICE clauses in single query not yet supported
- TrigramIndex uses full rebuild on delete (lazy deletion not implemented)

[1.1.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.1.0
[1.0.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.0.0
[0.6.2]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.2
[0.6.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.1
[0.6.0-beta.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.0-beta.1
[0.5.0-beta.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.5.0-beta.1
