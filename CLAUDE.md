# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build entire solution
dotnet build SkyOmega.sln

# Build specific project
dotnet build src/Mercury/Mercury.csproj

# Release build (enables optimizations)
dotnet build -c Release

# Run tests (xUnit)
dotnet test

# Run specific test
dotnet test --filter "FullyQualifiedName~BasicSelect"

# Run benchmarks (BenchmarkDotNet)
dotnet run --project benchmarks/Mercury.Benchmarks -c Release

# Run specific benchmark class
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*Storage*"

# List available benchmarks
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --list

# Run examples
dotnet run --project examples/Mercury.Examples
dotnet run --project examples/Mercury.Examples -- storage
dotnet run --project examples/Mercury.Examples -- temporal
dotnet run --project examples/Mercury.Examples -- demo
```

## In-Flight Work: ADRs

Architecture Decision Records track planning and progress for complex features:

```bash
ls docs/adrs/             # Cross-cutting ADRs (e.g., ADR-000 repo structure)
ls docs/adrs/mercury/     # Mercury ADRs
ls docs/adrs/minerva/     # Minerva ADRs
```

**ADR workflow:** Plan in ADR → implement → check off success criteria → update status to "Accepted".

See individual ADRs for current implementation status. Don't duplicate progress tracking in CLAUDE.md.

## Project Overview

Sky Omega is a semantic-aware cognitive assistant with zero-GC performance design. The codebase targets .NET 10 with C# 14. The core library (Mercury) has **no external dependencies** (BCL only).

### Solution Structure

**IDE Views:** Visual Studio, Rider, and VS Code support both *Solution View* (virtual folders defined in `.sln`) and *Filesystem View* (actual directory structure). This solution uses virtual folders to provide logical grouping for developers:

- **Solution View**: ADRs appear under their substrate (Mercury/Minerva), architecture docs under Documentation
- **Filesystem View**: All docs live in `docs/` with consistent paths for linking

Both views are valid and useful. Solution View is optimized for browsing by role (architect, developer), while Filesystem View reflects the actual repository structure.

```
SkyOmega.sln
├── docs/
│   ├── adrs/                # Architecture Decision Records
│   │   ├── mercury/         # Mercury-specific ADRs
│   │   └── minerva/         # Minerva-specific ADRs
│   ├── specs/               # External format specifications
│   │   ├── rdf/             # RDF specs (future: SPARQL, Turtle, etc.)
│   │   └── llm/             # LLM specs (GGUF, SafeTensors, Tokenizers)
│   ├── architecture/        # Conceptual documentation
│   └── api/                 # API documentation
├── src/
│   ├── Mercury/             # Knowledge substrate - RDF storage and SPARQL (BCL only)
│   │   ├── NTriples/        # Streaming N-Triples parser
│   │   ├── Rdf/             # Triple data structures
│   │   ├── RdfXml/          # Streaming RDF/XML parser
│   │   ├── Sparql/          # SPARQL parser and query execution
│   │   │   ├── Execution/   # Query operators, executor, LoadExecutor
│   │   │   ├── Parsing/     # SparqlParser, RdfParser (zero-GC parsing)
│   │   │   └── Patterns/    # PatternSlot, QueryBuffer (Buffer+View pattern)
│   │   ├── Storage/         # B+Tree indexes, atom storage, WAL
│   │   └── Turtle/          # Streaming RDF Turtle parser
│   ├── Mercury.Cli.Turtle/  # Turtle parser CLI demo
│   ├── Mercury.Cli.Sparql/  # SPARQL engine CLI demo
│   ├── Mercury.Pruning/     # Dual-instance pruning with copy-and-switch
│   │
│   ├── Minerva/             # Thought substrate - tensor inference (BCL only)
│   │   ├── Weights/         # GGUF and SafeTensors readers
│   │   ├── Tokenizers/      # BPE, SentencePiece tokenizers
│   │   ├── Tensors/         # Tensor operations
│   │   └── Inference/       # Model inference
│   ├── Minerva.Cli/         # Minerva CLI (future)
│   └── Minerva.Mcp/         # Minerva MCP server (future)
├── tests/
│   ├── Mercury.Tests/       # Mercury xUnit tests
│   └── Minerva.Tests/       # Minerva xUnit tests (future)
├── benchmarks/
│   ├── Mercury.Benchmarks/  # Mercury BenchmarkDotNet tests
│   └── Minerva.Benchmarks/  # Minerva benchmarks (future)
└── examples/
    ├── Mercury.Examples/    # Mercury usage examples
    └── Minerva.Examples/    # Minerva usage examples (future)
```

## API Usage Examples

For detailed code examples of all APIs, see **[docs/api/api-usage.md](docs/api/api-usage.md)**.

## Architecture

### Component Layers

```
Sky (Agent) → James (Orchestration) → Lucy (Semantic Memory) → Mercury (Storage)
                                   ↘ Mira (Surfaces) ↙
```

- **Sky** - Cognitive agent with reasoning and reflection
- **James** - Orchestration layer with pedagogical guidance
- **Lucy** - RDF triple store with SPARQL queries
- **Mira** - Presentation surfaces (CLI, chat, IDE extensions)
- **Mercury** - B+Tree indexes, append-only stores, memory-mapped files

For the vision, methodology (EEE), and broader context, see [docs/architecture/sky-omega-convergence.md](docs/architecture/sky-omega-convergence.md).

### Storage Layer (`SkyOmega.Mercury.Storage`)

| Component | Purpose |
|-----------|---------|
| `QuadStore` | Multi-index quad store (GSPO ordering) with named graph support |
| `QuadIndex` | Single B+Tree index with bitemporal + graph support |
| `AtomStore` | String interning with memory-mapped storage |
| `PageCache` | LRU cache for B+Tree pages (clock algorithm) |
| `TrigramIndex` | Full-text search via trigram inverted index (opt-in) |

### Durability Design

Sky Omega uses Write-Ahead Logging (WAL) for crash safety:

1. **Write path**: WAL append → fsync → apply to indexes
2. **Recovery**: Replay uncommitted WAL entries after last checkpoint
3. **Checkpointing**: Hybrid trigger (size OR time, whichever first)

**Design decisions:**

- **AtomStore has no separate WAL**: Append-only by design. On recovery, validate tail and rebuild hash index.
- **WAL stores atom IDs, not strings**: Atoms persisted before WAL write (we need IDs to write the record).
- **Batch-first design**: TxId in WAL records enables batching. Amortizing fsync across N triples is critical for performance.
- **Hybrid checkpoint trigger**: Size-based (16MB) adapts to bursts; time-based (60s) bounds recovery during idle.

### Batch Write API

Use the batch API for high-throughput bulk loading (~100,000 triples/sec vs ~300/sec for single writes). See [docs/api/api-usage.md#batch-write-api](docs/api/api-usage.md#batch-write-api).

**Performance characteristics:**
- Single writes: ~250-300/sec (fsync per write)
- Batch of 1,000: ~25,000+/sec (1 fsync per batch)
- Batch of 10,000: ~100,000+/sec

### Named Graphs (Quads)

QuadStore supports RDF named graphs for domain isolation. See [docs/api/api-usage.md#named-graphs-quads](docs/api/api-usage.md#named-graphs-quads).

**Design notes:**
- **GSPO ordering**: B+Tree keys ordered by Graph first for efficient graph-scoped queries
- **Graph isolation**: Default graph (atom 0) and named graphs are fully isolated
- **TemporalKey**: 56 bytes (GraphAtom + SubjectAtom + PredicateAtom + ObjectAtom + ValidFrom + ValidTo + TransactionTime)
- **WAL record**: 72 bytes (includes GraphId for crash recovery)

### Pruning (`SkyOmega.Mercury.Pruning`)

Dual-instance pruning system using copy-and-switch pattern. Transfers quads between QuadStore instances with filtering, enabling:
- Soft deletes → hard deletes (physical removal)
- History flattening or preservation
- Graph/predicate-based filtering

**Components:**

| Component | Purpose |
|-----------|---------|
| `PruningTransfer` | Orchestrates transfer from source to target store |
| `IPruningFilter` | Filter interface for inclusion criteria |
| `GraphFilter` | Filter by graph IRI(s) - include/exclude modes |
| `PredicateFilter` | Filter by predicate IRI(s) |
| `CompositeFilter` | AND/OR composition of filters |
| `HistoryMode` | FlattenToCurrent, PreserveVersions, PreserveAll |
| `TransferOptions` | Batch size, progress interval, verification flags |

**Basic usage:**
```csharp
using var target = new QuadStore("/path/to/new/store");
var result = new PruningTransfer(source, target).Execute();
// Soft-deleted quads are now physically gone
```

**With filtering:**
```csharp
var options = new TransferOptions {
    Filter = CompositeFilter.All(
        GraphFilter.Exclude("<http://temp.data>"),
        PredicateFilter.Exclude("<http://internal/debug>")),
    HistoryMode = HistoryMode.FlattenToCurrent,
    VerifyAfterTransfer = true
};
var result = new PruningTransfer(source, target, options).Execute();
```

**Verification options:**
- `DryRun` - Preview what would transfer without writing
- `VerifyAfterTransfer` - Re-enumerate and verify counts match
- `ComputeChecksum` - FNV-1a checksum for content verification
- `AuditLogPath` - Write filtered-out quads to N-Quads file

### Concurrency Design

QuadStore uses `ReaderWriterLockSlim` for thread-safety:

1. **Single writer, multiple readers**: Write operations acquire exclusive write lock
2. **Explicit read locking**: Callers use `AcquireReadLock()`/`ReleaseReadLock()` around query enumeration
3. **ref struct constraint**: `TemporalResultEnumerator` cannot hold locks internally (stack-only lifetime)

**Critical pattern - always wrap queries with locks:**
```csharp
store.AcquireReadLock();
try
{
    var results = store.QueryCurrent(subject, predicate, obj);
    while (results.MoveNext()) { /* process */ }
    results.Dispose();  // Return pooled buffer
}
finally
{
    store.ReleaseReadLock();
}
```

### Zero-GC Design Principles

All parsers use aggressive zero-allocation techniques:
- `ref struct` parsers that live entirely on the stack
- `ArrayPool<T>` for all buffer allocations
- `ReadOnlySpan<char>` for string operations
- String interning via AtomStore to avoid duplicate allocations
- Streaming enumerators that yield results without materializing collections

**Key insight: Zero-GC ≠ "everything on stack"**

Zero-GC means **no uncontrolled allocations**, not "avoid heap entirely". Pooled heap memory is equally zero-GC as stack memory, but without size limits.

**The Buffer + View pattern** (used by `Span<T>`, `Utf8JsonReader`, `System.IO.Pipelines`):
- Tiny handle/view struct (just a `Span<byte>` or pointer + length)
- Caller owns/provides storage (stackalloc for small, pooled array for large, mmap for persistence)
- Typed access via `MemoryMarshal.AsRef<T>()` for discriminated unions

Implemented in `PatternSlot` (`src/Mercury/Sparql/Patterns/PatternSlot.cs`) - a 64-byte cache-aligned slot with discriminator byte and typed views over raw bytes.

**Stack safety for large ref structs:** Large ref structs like `QueryResults` (~22KB) can cause stack overflow in complex query paths. The solution is to materialize results to heap (`List<MaterializedRow>`) early, returning only the pointer through the call chain. See [ADR-003: Buffer Pattern for Stack Safety](docs/adrs/mercury/ADR-003-buffer-pattern.md) for details.

**Critical patterns:**

1. **Parser callback API (zero-GC)** - spans valid only during callback:
```csharp
await parser.ParseAsync((subject, predicate, obj) =>
{
    store.AddCurrent(subject, predicate, obj);
});
```

2. **Query result disposal** - always call `Dispose()`:
```csharp
var results = executor.Execute();
try { while (results.MoveNext()) { /* process */ } }
finally { results.Dispose(); }
```

**Zero-GC compliance by component:**

| Component | Status | Notes |
|-----------|--------|-------|
| SPARQL Parser | ✓ Zero-GC | ref struct, no allocations |
| Query Executor | ✓ Zero-GC | ref struct operators, call Dispose() |
| QuadStore Query | ✓ Zero-GC | Pooled buffer, call Dispose() |
| Turtle Parser (Handler) | ✓ Zero-GC | Use TripleHandler callback |
| Turtle Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| N-Triples Parser (Handler) | ✓ Zero-GC | Use TripleHandler callback |
| N-Triples Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| N-Quads Parser (Handler) | ✓ Zero-GC | Use QuadHandler callback |
| N-Quads Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| TriG Parser (Handler) | ✓ Zero-GC | Use QuadHandler callback |
| TriG Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| TriG Writer | ✓ Zero-GC | Streaming output, no allocations |
| JSON-LD Parser (Handler) | Near Zero-GC | Uses System.Text.Json, allocates for context |
| JSON-LD Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| JSON-LD Writer | Allocates | Collects quads, outputs on flush |
| RDF/XML Parser | Near Zero-GC | Allocates for namespace dictionary + async boundaries |

### Turtle Parser (`SkyOmega.Mercury.Turtle`)

`TurtleStreamParser` is a `partial class` split across files:
- `TurtleStreamParser.cs` - Main parser logic and `ParseAsync()` entry points
- `TurtleStreamParser.Buffer.cs` - Buffer management
- `TurtleStreamParser.Structures.cs` - RDF structure parsing (blank nodes, collections)
- `TurtleStreamParser.Terminals.cs` - Terminal parsing (IRIs, literals, prefixed names)

Supports RDF-star (RDF 1.2) syntax - reified triples converted to standard RDF reification for storage/query.

### RDF Writers

| Feature | N-Triples | Turtle | RDF/XML |
|---------|-----------|--------|---------|
| Prefix/namespace support | No | Yes | Yes |
| Subject grouping | No | Yes (`;`) | Yes (`rdf:Description`) |
| `rdf:type` shorthand | No | Yes (`a`) | No |
| Language tags | Yes | Yes | Yes (`xml:lang`) |
| Typed literals | Yes | Yes | Yes (`rdf:datatype`) |
| Blank nodes | Yes | Yes | Yes (`rdf:nodeID`) |

### SPARQL Engine (`SkyOmega.Mercury.Sparql`)

`SparqlParser` is a `ref struct` that parses SPARQL queries from `ReadOnlySpan<char>`.

Key components:
- `SparqlParser` - Zero-GC query parser
- `QueryExecutor` - Zero-GC query execution with specialized operators
- `FilterEvaluator` - SPARQL FILTER expression evaluation
- `RdfParser` - N-Triples parsing utilities

**Supported SPARQL features:**

| Category | Features |
|----------|----------|
| Query types | SELECT, ASK, CONSTRUCT, DESCRIBE |
| Graph patterns | Basic patterns, OPTIONAL, UNION, MINUS, GRAPH (IRI and variable, multiple), Subqueries (single and multiple), SERVICE |
| Federated query | SERVICE \<uri\> { patterns }, SERVICE SILENT, SERVICE ?variable (requires ISparqlServiceExecutor) |
| Property paths | ^iri (inverse), iri* (zero+), iri+ (one+), iri? (optional), path/path, path\|path, !(iri\|iri) (negated set) |
| Filtering | FILTER, VALUES, EXISTS, NOT EXISTS, IN, NOT IN |
| Filter functions | BOUND, IF, COALESCE, REGEX, REPLACE, sameTerm, text:match |
| Type checking | isIRI, isURI, isBlank, isLiteral, isNumeric |
| String functions | STR, STRLEN, SUBSTR, CONTAINS, STRSTARTS, STRENDS, STRBEFORE, STRAFTER, CONCAT, UCASE, LCASE, ENCODE_FOR_URI |
| Numeric functions | ABS, ROUND, CEIL, FLOOR, RAND |
| RDF term functions | LANG, DATATYPE, LANGMATCHES, IRI, URI, STRDT, STRLANG, BNODE |
| Hash functions | MD5, SHA1, SHA256, SHA384, SHA512 |
| UUID functions | UUID, STRUUID (uses time-ordered UUID v7) |
| DateTime functions | NOW, YEAR, MONTH, DAY, HOURS, MINUTES, SECONDS, TZ, TIMEZONE |
| Computed values | BIND (arithmetic expressions) |
| Aggregation | GROUP BY, HAVING, COUNT, SUM, AVG, MIN, MAX, GROUP_CONCAT, SAMPLE |
| Modifiers | DISTINCT, REDUCED, ORDER BY (ASC/DESC), LIMIT, OFFSET |
| Dataset | FROM, FROM NAMED (cross-graph joins supported) |
| Temporal queries | AS OF (point-in-time), DURING (range), ALL VERSIONS (history) |
| SPARQL-star | Quoted triples (`<< s p o >>`), expanded to reification at parse time |
| SPARQL Update | INSERT DATA, DELETE DATA, DELETE WHERE, DELETE/INSERT WHERE (WITH clause), CLEAR, DROP, CREATE, COPY, MOVE, ADD, LOAD |

**Query execution model:**
1. Parse query → `Query` struct with patterns, filters, modifiers
2. Build execution plan → Stack of operators (TriplePatternScan, MultiPatternScan)
3. Execute → Pull-based iteration through operator pipeline

**Operator pipeline:**
- `TriplePatternScan` - Scans single pattern, binds variables from matching triples
- `MultiPatternScan` - Nested loop join for up to 12 patterns with backtracking (supports SPARQL-star expansion)
- `SlotTriplePatternScan` - Slot-based variant reading from 64-byte PatternSlot
- `SlotMultiPatternScan` - Slot-based multi-pattern join reading from byte[] buffer
- `SubQueryScan` - Executes nested SELECT subquery, projects selected variables
- `SubQueryJoinScan` - Joins subquery results with outer patterns via nested loop
- `ServiceScan` - Executes SERVICE clause against remote SPARQL endpoint via ISparqlServiceExecutor
- Filter/BIND/MINUS/VALUES evaluation integrated into result iteration

### SPARQL EXPLAIN

`SparqlExplainer` generates query execution plans for analysis and debugging.

**Operator symbols:**

| Symbol | Operator | Description |
|--------|----------|-------------|
| ⊳ | TriplePatternScan | Scan index for triple pattern |
| ⋈ | NestedLoopJoin | Join two patterns |
| ⟕ | LeftOuterJoin | OPTIONAL pattern |
| ∪ | Union | UNION alternatives |
| σ | Filter | FILTER expression |
| γ | GroupBy | GROUP BY with aggregation |
| ↑ | Sort | ORDER BY |
| ⌊ | Slice | LIMIT/OFFSET |
| π | Project | SELECT projection |

### SPARQL Result Formats

| Format | Content-Type | Features |
|--------|--------------|----------|
| JSON | application/sparql-results+json | Full type info, datatypes, language tags |
| XML | application/sparql-results+xml | Full type info, datatypes, language tags |
| CSV | text/csv | Compact, values only (no type info) |
| TSV | text/tab-separated-values | Preserves RDF syntax (brackets, quotes) |

### Content Negotiation

**Supported RDF formats:**

| RDF Format | Content Types | Extensions |
|------------|---------------|------------|
| Turtle | text/turtle, application/x-turtle | .ttl, .turtle |
| N-Triples | application/n-triples, text/plain | .nt, .ntriples |
| RDF/XML | application/rdf+xml, application/xml, text/xml | .rdf, .xml, .rdfxml |
| N-Quads | application/n-quads, text/x-nquads | .nq, .nquads |
| TriG | application/trig | .trig |
| JSON-LD | application/ld+json | .jsonld |

**Supported SPARQL result formats:**

| SPARQL Result Format | Content Types | Extensions |
|---------------------|---------------|------------|
| JSON | application/sparql-results+json, application/json | .json, .srj |
| XML | application/sparql-results+xml, application/xml | .xml, .srx |
| CSV | text/csv | .csv |
| TSV | text/tab-separated-values, text/tsv | .tsv |

### Temporal SPARQL Extensions

| Mode | Syntax | Storage Method | Description |
|------|--------|----------------|-------------|
| Current | (default) | `QueryCurrent()` | Data valid at `UtcNow` |
| AS OF | `AS OF "date"^^xsd:date` | `QueryAsOf()` | Data valid at specific time |
| DURING | `DURING ["start"^^xsd:date, "end"^^xsd:date]` | `QueryChanges()` | Data overlapping period |
| ALL VERSIONS | `ALL VERSIONS` | `QueryEvolution()` | Complete history |

**Design notes:**
- Temporal clauses come after LIMIT/OFFSET in solution modifiers
- DateTime literals support both `xsd:date` and `xsd:dateTime` formats
- Default mode is `Current` (equivalent to calling `QueryCurrent()`)

### OWL/RDFS Reasoning (`SkyOmega.Mercury.Owl`)

`OwlReasoner` implements forward-chaining rule-based inference for RDFS and OWL ontologies.

**Supported inference rules:**

| Rule Set | Rules | Description |
|----------|-------|-------------|
| RDFS | `RdfsSubClass` | Transitive subClassOf, type inference from class hierarchy |
| RDFS | `RdfsSubProperty` | Transitive subPropertyOf, property inheritance |
| RDFS | `RdfsDomain` | Infer subject type from property domain |
| RDFS | `RdfsRange` | Infer object type from property range |
| OWL | `OwlTransitive` | TransitiveProperty closure |
| OWL | `OwlSymmetric` | SymmetricProperty inverse |
| OWL | `OwlInverse` | inverseOf bidirectional inference |
| OWL | `OwlSameAs` | Identity-based triple copying |
| OWL | `OwlEquivalentClass` | equivalentClass to mutual subClassOf |
| OWL | `OwlEquivalentProperty` | equivalentProperty to mutual subPropertyOf |

**Design notes:**
- Forward-chaining materialization (inferred triples stored in graph)
- Fixed-point iteration (runs until no new facts)
- Configurable max iterations to prevent infinite loops

### SPARQL HTTP Server (`SkyOmega.Mercury.Sparql.Protocol`)

`SparqlHttpServer` implements W3C SPARQL 1.1 Protocol using BCL HttpListener.

**Endpoints:**
- `GET/POST /sparql` - Query endpoint
- `POST /sparql/update` - Update endpoint (when enabled)
- `GET /sparql` (no query) - Service description (Turtle)

**Content negotiation:**

| Accept Header | Result Format |
|---------------|---------------|
| `application/sparql-results+json` | JSON (default) |
| `application/sparql-results+xml` | XML |
| `text/csv` | CSV |
| `text/tab-separated-values` | TSV |

## Production Hardening Roadmap

### Infrastructure Abstractions

**ILogger** (`SkyOmega.Mercury.Diagnostics.ILogger`):
- BCL-only logging abstraction with zero-allocation hot path
- Levels: Trace, Debug, Info, Warning, Error, Critical
- `NullLogger.Instance` for production (no overhead)
- `ConsoleLogger` for development/debugging

**IBufferManager** (`SkyOmega.Mercury.Buffers.IBufferManager`):
- Unified buffer allocation strategy across all components
- `PooledBufferManager.Shared` uses `ArrayPool<T>` internally
- `BufferLease<T>` ref struct for RAII-style automatic cleanup

### Query Optimization

Statistics-based join reordering for 10-100x performance improvement on multi-pattern queries.

**Components:**

| Component | File | Purpose |
|-----------|------|---------|
| `PredicateStats` | `Storage/PredicateStatistics.cs` | Per-predicate cardinality statistics |
| `StatisticsStore` | `Storage/PredicateStatistics.cs` | Thread-safe statistics storage |
| `QueryPlanner` | `Sparql/Execution/QueryPlanner.cs` | Cardinality estimation and pattern reordering |
| `QueryPlanCache` | `Sparql/Execution/QueryPlanCache.cs` | LRU cache for execution plans |

**How it works:**
1. Statistics Collection: During `Checkpoint()`, scans GPOS index for per-predicate triple count, distinct subjects, distinct objects
2. Cardinality Estimation: Estimates result count based on bound/unbound variables
3. Join Reordering: Sorts patterns by estimated cardinality (lowest first)
4. Plan Caching: Caches reordered pattern order, invalidates when statistics change

| Optimization | Impact | Status |
|--------------|--------|--------|
| Join Reordering | 10-100x | Implemented |
| Statistics Collection | 2-10x | Implemented |
| Plan Caching | 2-5x | Implemented |
| Predicate Pushdown | 5-50x | Implemented - FilterAnalyzer + MultiPatternScan |

### SERVICE Clause Architecture

SERVICE clauses (federated queries) require special handling due to fundamentally different access semantics vs local patterns. See **[docs/adrs/mercury/ADR-004-service-scan-interface.md](docs/adrs/mercury/ADR-004-service-scan-interface.md)** for the architectural decision record.

**Key principle:** SERVICE is a materialization boundary, not an iterator. Implementation uses:
- `IScan` interface for uniform operator handling
- `ServiceStore` with `TempPath` lifecycle (crash-safe temp QuadStore)
- `ServicePatternScan` wrapping `TriplePatternScan` against temp store

**Implementation phases:**
1. Extract `IScan` interface (mechanical refactor, tests must pass unchanged)
2. Implement temp store pattern (SERVICE results become local triples)

### Full-Text Search

BCL-only trigram index for SPARQL text search. Opt-in via `StorageOptions.EnableFullTextSearch`.

**Components:**

| Component | File | Purpose |
|-----------|------|---------|
| `TrigramIndex` | `Storage/TrigramIndex.cs` | UTF-8 trigram extraction and inverted index |
| `text:match` | `Sparql/Execution/FilterEvaluator.Functions.cs` | SPARQL FILTER function |

**Usage:**
```csharp
var options = new StorageOptions { EnableFullTextSearch = true };
var store = new QuadStore(path, null, null, options);
```

```sparql
SELECT ?city ?name WHERE {
    ?city <http://ex.org/name> ?name .
    FILTER(text:match(?name, "göteborg"))
}
```

**Features:**
- Case-insensitive matching with Unicode case-folding (supports Swedish å, ä, ö)
- Memory-mapped two-file architecture (trigram.hash + trigram.posts)
- FNV-1a hashing with quadratic probing
- Alternative `match()` syntax supported
- Works with variables, literals, negation, boolean combinations

### Test Coverage Gaps

| Component | Status | Priority |
|-----------|--------|----------|
| REPL system | ✓ 147 tests | Done |
| HttpSparqlServiceExecutor | ✓ 44 tests | Done |
| LoadExecutor | ✓ tests | Done |
| Concurrent access stress | ✓ 15 tests | Done |
| PatternSlot/QueryBuffer | ✓ tests | Done |

### Benchmark Gaps

| Component | Status | Priority |
|-----------|--------|----------|
| SPARQL parsing | ✓ SparqlParserBenchmarks | Done |
| SPARQL execution | ✓ SparqlExecutionBenchmarks | Done |
| JOIN operators | ✓ JoinBenchmarks | Done |
| FILTER evaluation | ✓ FilterBenchmarks | Done |
| Storage (batch/query) | ✓ BatchWrite/QueryBenchmarks | Done |
| Temporal queries | ✓ TemporalQueryBenchmarks | Done |
| Concurrent access | ✓ ConcurrentBenchmarks | Done |
| RDF parser throughput | ✓ NTriples/Turtle/FormatComparison | Done |
| Filter pushdown | ✓ FilterPushdownBenchmarks | Done |

### Running Benchmarks via Claude Code

BenchmarkDotNet produces verbose output that exceeds context limits. Use this file-based workflow:

**Strategy: Run targeted benchmarks, read artifact files**

```bash
# 1. Run a single benchmark class (generates markdown report)
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- \
  --filter "*QueryBenchmarks*"

# 2. Results are written to: BenchmarkDotNet.Artifacts/results/ (repo root)
# Read the generated markdown summary (compact, ~18 lines per class)
```

**Available benchmark classes:**

| Class | Filter | Purpose |
|-------|--------|---------|
| `BatchWriteBenchmarks` | `*BatchWrite*` | Single vs batch write throughput |
| `QueryBenchmarks` | `*QueryBenchmarks*` | Query operations on pre-populated store |
| `IndexSelectionBenchmarks` | `*IndexSelection*` | Index selection impact (SPO/POS/OSP) |
| `SparqlParserBenchmarks` | `*ParserBenchmarks*` | SPARQL parsing throughput |
| `SparqlExecutionBenchmarks` | `*ExecutionBenchmarks*` | SPARQL query execution |
| `JoinBenchmarks` | `*JoinBenchmarks*` | JOIN operator scaling (2/5/8 patterns) |
| `FilterBenchmarks` | `*FilterBenchmarks*` | FILTER expression overhead |
| `TemporalWriteBenchmarks` | `*TemporalWrite*` | Temporal triple write performance |
| `TemporalQueryBenchmarks` | `*TemporalQuery*` | Temporal query operations |
| `NTriplesParserBenchmarks` | `*NTriples*` | N-Triples parsing (zero-GC vs allocating) |
| `TurtleParserBenchmarks` | `*Turtle*` | Turtle parsing (zero-GC vs allocating) |
| `RdfFormatComparisonBenchmarks` | `*FormatComparison*` | N-Triples vs Turtle format comparison |

**Workflow for Claude Code:**

1. Run benchmark with `--filter` (one class at a time)
2. Wait for completion (may take 1-5 minutes per class)
3. Read the markdown report from `BenchmarkDotNet.Artifacts/results/`
4. Compare results, identify regressions

**Example - investigating query performance:**

```bash
# Run storage query benchmarks (use precise filter to avoid matching TemporalQueryBenchmarks)
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*.QueryBenchmarks.*"

# Read the compact markdown report
cat BenchmarkDotNet.Artifacts/results/SkyOmega.Mercury.Benchmarks.QueryBenchmarks-report-github.md
```

**Filter tips:**
- `*.ClassName.*` is more precise than `*ClassName*`
- `*QueryBenchmarks*` matches both `QueryBenchmarks` and `TemporalQueryBenchmarks`
- `*.QueryBenchmarks.*` matches only `QueryBenchmarks`

**Note:** Artifacts directory is gitignored. Results persist locally between runs for comparison.

### Production Hardening Checklist

- [x] Query timeout via CancellationToken
- [x] Max atom size validation (default 1MB)
- [x] Max query depth limits (parser)
- [x] Max join depth limits (executor)
- [x] try/finally for all operator disposal
- [x] Pointer leak fix in AtomStore
- [x] Thread-safety documentation for parsers

## Code Conventions

- All parsing methods follow W3C EBNF grammar productions (comments reference production numbers)
- Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for hot paths
- Prefer `ReadOnlySpan<char>` over `string` for parsing operations
- Use `unsafe fixed` buffers for small inline storage when needed
- Temporal semantics are implicit - all triples have valid-time bounds

## Design Philosophy

Sky Omega values:
- **Simplicity over flexibility** - fewer moving parts, less to break
- **Append-only where possible** - naturally crash-safe, simpler recovery
- **Zero external dependencies for core library** - Mercury is BCL only; dev tooling (tests, benchmarks) can use standard packages
- **Zero-GC on hot paths** - predictable latency for cognitive operations
