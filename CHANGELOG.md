# Changelog

All notable changes to Sky Omega will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.5.0-beta.1]: https://github.com/anthropics/sky-omega/releases/tag/v0.5.0-beta.1
