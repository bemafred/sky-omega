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

## Project Overview

Sky Omega is a semantic-aware cognitive assistant with zero-GC performance design. The codebase targets .NET 10 with C# 14. The core library (Mercury) has **no external dependencies** (BCL only).

### Solution Structure

```
SkyOmega.sln
├── src/
│   ├── Mercury/             # Core library - storage and query engine (BCL only)
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
│   └── Mercury.Cli.Sparql/  # SPARQL engine CLI demo
├── tests/
│   └── Mercury.Tests/       # xUnit tests
├── benchmarks/
│   └── Mercury.Benchmarks/  # BenchmarkDotNet performance tests
└── examples/
    └── Mercury.Examples/    # Usage examples and demos
```

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

For the vision, methodology (EEE), and broader context, see [docs/sky-omega-convergence.md](docs/sky-omega-convergence.md).

### Storage Layer (`SkyOmega.Mercury.Storage`)

| Component | Purpose |
|-----------|---------|
| `QuadStore` | Multi-index quad store (GSPO ordering) with named graph support |
| `QuadIndex` | Single B+Tree index with bitemporal + graph support |
| `AtomStore` | String interning with memory-mapped storage |
| `PageCache` | LRU cache for B+Tree pages (clock algorithm) |

### Durability Design

Sky Omega uses Write-Ahead Logging (WAL) for crash safety:

1. **Write path**: WAL append → fsync → apply to indexes
2. **Recovery**: Replay uncommitted WAL entries after last checkpoint
3. **Checkpointing**: Hybrid trigger (size OR time, whichever first)

**Design decisions and rationale:**

- **AtomStore has no separate WAL**: It's append-only by design. On recovery, validate tail and rebuild hash index. Simpler than double-WAL.
- **WAL stores atom IDs, not strings**: Atoms are persisted before WAL write (we need IDs to write the record). Natural ordering solves the dependency.
- **Batch-first design**: TxId in WAL records enables batching. Single writes are batch-of-one. Amortizing fsync across N triples is critical for performance.
- **Hybrid checkpoint trigger**: Size-based (16MB) adapts to bursts; time-based (60s) bounds recovery during idle.

### Batch Write API

For high-throughput bulk loading, use the batch API to amortize fsync across many writes:

```csharp
store.BeginBatch();
try
{
    foreach (var triple in triples)
    {
        store.AddCurrentBatched(triple.Subject, triple.Predicate, triple.Object);
    }
    store.CommitBatch();  // Single fsync for entire batch
}
catch
{
    store.RollbackBatch();
    throw;
}
```

**Performance characteristics:**
- Single writes: ~250-300/sec (fsync per write)
- Batch of 1,000: ~25,000+/sec (1 fsync per batch)
- Batch of 10,000: ~100,000+/sec

**Design notes:**
- `BeginBatch()` acquires exclusive write lock (held until commit/rollback)
- `AddBatched()`/`AddCurrentBatched()` write to WAL without fsync
- `CommitBatch()` performs single fsync, releases lock
- `RollbackBatch()` releases lock without committing (in-memory changes persist but WAL uncommitted)

### Named Graphs (Quads)

QuadStore supports RDF named graphs for domain isolation. Each quad can belong to a named graph or the default graph:

```csharp
// Add to named graph
store.AddCurrent(subject, predicate, obj, "<http://example.org/graph1>");

// Add to default graph (no graph parameter)
store.AddCurrent(subject, predicate, obj);

// Query specific named graph
var results = store.QueryCurrent(subject, predicate, obj, "<http://example.org/graph1>");

// Query default graph (no graph parameter)
var results = store.QueryCurrent(subject, predicate, obj);

// Enumerate all named graphs
store.AcquireReadLock();
try
{
    foreach (var graphIri in store.GetNamedGraphs())
    {
        // graphIri is ReadOnlySpan<char> for each distinct named graph
    }
}
finally
{
    store.ReleaseReadLock();
}
```

**Design notes:**
- **GSPO ordering**: B+Tree keys are ordered by Graph first, enabling efficient graph-scoped queries
- **Graph isolation**: Default graph (atom 0) and named graphs are fully isolated
- **Graph enumeration**: `GetNamedGraphs()` returns distinct graph IRIs (excludes default graph)
- **TemporalKey**: 56 bytes (GraphAtom + SubjectAtom + PredicateAtom + ObjectAtom + ValidFrom + ValidTo + TransactionTime)
- **WAL record**: 72 bytes (includes GraphId for crash recovery)
- All Add/Delete/Query methods accept optional `graph` parameter

### Concurrency Design

QuadStore uses `ReaderWriterLockSlim` for thread-safety:

1. **Single writer, multiple readers**: Write operations (`Add`, `Checkpoint`) acquire exclusive write lock
2. **Explicit read locking**: Callers use `AcquireReadLock()`/`ReleaseReadLock()` around query enumeration
3. **ref struct constraint**: `TemporalResultEnumerator` cannot hold locks internally (stack-only lifetime)

**Usage pattern for concurrent reads:**
```csharp
store.AcquireReadLock();
try
{
    var results = store.QueryCurrent(subject, predicate, obj);
    while (results.MoveNext())
    {
        var triple = results.Current;
        // Process triple...
    }
}
finally
{
    store.ReleaseReadLock();
}
```

**Design decisions:**
- **ReaderWriterLockSlim over lock**: Enables concurrent read throughput for query-heavy workloads
- **NoRecursion policy**: Prevents accidental deadlocks, forces explicit lock management
- **AtomStore relies on QuadStore lock**: Append-only with `Interlocked` for allocation; no separate lock needed

### Zero-GC Design Principles

All parsers use aggressive zero-allocation techniques:
- `ref struct` parsers that live entirely on the stack
- `ArrayPool<T>` for all buffer allocations
- `ReadOnlySpan<char>` for string operations
- String interning via AtomStore to avoid duplicate allocations
- Streaming enumerators that yield results without materializing collections

**Key insight: Zero-GC ≠ "everything on stack"**

Zero-GC means **no uncontrolled allocations**, not "avoid heap entirely". Pooled heap memory is equally zero-GC as stack memory, but without size limits.

A naive approach of using large inline fixed buffers in structs (e.g., `GraphPattern` with 32 inline `TriplePattern` fields ≈ 4KB) causes stack overflow when structs pass by value through nested calls. The .NET stack is typically only 1MB.

**The Buffer + View pattern** (used by `Span<T>`, `Utf8JsonReader`, `System.IO.Pipelines`):
- Tiny handle/view struct (just a `Span<byte>` or pointer + length)
- Caller owns/provides storage (stackalloc for small, pooled array for large, mmap for persistence)
- Typed access via `MemoryMarshal.AsRef<T>()` for discriminated unions

This pattern is implemented in `PatternSlot` (`src/Mercury/Sparql/Patterns/PatternSlot.cs`) - a 64-byte cache-aligned slot with a discriminator byte and typed views over raw bytes. The caller controls the buffer, eliminating hidden allocations while avoiding stack overflow.

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
| RDF/XML Parser | Near Zero-GC | Allocates for namespace dictionary + async boundaries |

### QuadStore Query (Zero-GC)

Query results use pooled buffers. Call `Dispose()` to return buffers:

```csharp
store.AcquireReadLock();
try
{
    var results = store.QueryCurrent(subject, predicate, obj, graph);
    try
    {
        while (results.MoveNext())
        {
            var triple = results.Current;
            // Spans valid until next MoveNext()
            // triple.Graph is empty for default graph, otherwise the graph IRI
            ProcessTriple(triple.Graph, triple.Subject, triple.Predicate, triple.Object);
        }
    }
    finally
    {
        results.Dispose();  // Return pooled buffer
    }
}
finally
{
    store.ReleaseReadLock();
}
```

### Turtle Parser (`SkyOmega.Mercury.Turtle`)

`TurtleStreamParser` is a `partial class` split across files:
- `TurtleStreamParser.cs` - Main parser logic and `ParseAsync()` entry points
- `TurtleStreamParser.Buffer.cs` - Buffer management
- `TurtleStreamParser.Structures.cs` - RDF structure parsing (blank nodes, collections)
- `TurtleStreamParser.Terminals.cs` - Terminal parsing (IRIs, literals, prefixed names)

**Zero-GC API (recommended):**
```csharp
await using var parser = new TurtleStreamParser(stream);
await parser.ParseAsync((subject, predicate, obj) =>
{
    // Spans valid only during callback
    store.AddCurrent(subject, predicate, obj);
});
```

**Legacy API (allocates strings):**
```csharp
await foreach (var triple in parser.ParseAsync())
{
    // triple.Subject, Predicate, Object are strings
}
```

**RDF-star support:**

The Turtle parser supports RDF-star (RDF 1.2) syntax. Reified triples are converted to standard RDF reification triples for storage and query:

```turtle
# RDF-star input
<< <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
    <http://ex.org/confidence> "0.9" .
```

Generates these standard RDF triples:
```turtle
# Reification triples
_:b0 rdf:type rdf:Statement .
_:b0 rdf:subject <http://ex.org/Alice> .
_:b0 rdf:predicate <http://ex.org/knows> .
_:b0 rdf:object <http://ex.org/Bob> .

# Asserted triple (RDF-star "asserted" semantics)
<http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> .

# Annotation triple
_:b0 <http://ex.org/confidence> "0.9" .
```

Features:
- Nested reified triples (`<< << s p o >> p2 o2 >> p3 o3`)
- Explicit reifier IRI (`<< s p o ~ <http://ex.org/stmt1> >>`)
- Query reified triples using standard SPARQL reification patterns

### N-Triples Parser (`SkyOmega.Mercury.NTriples`)

`NTriplesStreamParser` is a streaming parser for N-Triples format, following the same zero-GC pattern as the Turtle parser.

**Zero-GC API (recommended):**
```csharp
await using var parser = new NTriplesStreamParser(stream);
await parser.ParseAsync((subject, predicate, obj) =>
{
    // Spans valid only during callback
    store.AddCurrent(subject, predicate, obj);
});
```

**Legacy API (allocates strings):**
```csharp
await foreach (var triple in parser.ParseAsync())
{
    // triple.Subject, Predicate, Object are strings
}
```

N-Triples is simpler than Turtle (no prefixes, no shortcuts), making the parser more straightforward. Each triple is on its own line, terminated by a period.

### RDF/XML Parser (`SkyOmega.Mercury.RdfXml`)

`RdfXmlStreamParser` is a custom streaming XML parser optimized for the RDF/XML subset. It does not use `System.Xml` - instead, it parses XML character-by-character for zero-allocation streaming.

**Zero-GC API:**
```csharp
await using var parser = new RdfXmlStreamParser(stream);
await parser.ParseAsync((subject, predicate, obj) =>
{
    // Spans valid only during callback
    store.AddCurrent(subject, predicate, obj);
});
```

**Supported RDF/XML features:**
- `rdf:RDF` root element
- `rdf:Description` with `rdf:about`, `rdf:ID`, `rdf:nodeID`
- Property elements with `rdf:resource` for IRI objects
- Literal objects (plain, typed with `rdf:datatype`, language-tagged with `xml:lang`)
- `rdf:parseType="Resource"` for nested blank nodes
- `rdf:parseType="Collection"` for RDF collections
- XML namespaces and entity references (`&lt;`, `&gt;`, `&amp;`, `&apos;`, `&quot;`)

**Note:** Near zero-GC - allocates for namespace dictionary and string values that cross async boundaries.

### RDF Writers

Streaming writers for all three RDF formats, following the same zero-GC patterns as the parsers.

**N-Triples Writer (`NTriplesStreamWriter`):**
```csharp
using var sw = new StringWriter();
using var writer = new NTriplesStreamWriter(sw);
writer.WriteTriple("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
writer.WriteTriple("<http://ex.org/s>", "<http://ex.org/name>", "\"Alice\"@en");
```

**Turtle Writer (`TurtleStreamWriter`):**
```csharp
using var sw = new StringWriter();
using var writer = new TurtleStreamWriter(sw);
writer.RegisterPrefix("ex", "http://example.org/");
writer.WritePrefixes();
writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/knows>".AsSpan(), "<http://example.org/Bob>".AsSpan());
writer.Flush(); // Finishes subject grouping
// Output: ex:Alice ex:knows ex:Bob .
```

**RDF/XML Writer (`RdfXmlStreamWriter`):**
```csharp
using var sw = new StringWriter();
using var writer = new RdfXmlStreamWriter(sw);
writer.RegisterNamespace("ex", "http://example.org/");
writer.WriteStartDocument();
writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/knows>".AsSpan(), "<http://example.org/Bob>".AsSpan());
writer.WriteEndDocument();
```

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
| Property paths | ^iri (inverse), iri* (zero+), iri+ (one+), iri? (optional), path/path, path\|path |
| Filtering | FILTER, VALUES, EXISTS, NOT EXISTS, IN, NOT IN |
| Filter functions | BOUND, IF, COALESCE, REGEX, REPLACE, sameTerm |
| Type checking | isIRI, isURI, isBlank, isLiteral, isNumeric |
| String functions | STR, STRLEN, SUBSTR, CONTAINS, STRSTARTS, STRENDS, STRBEFORE, STRAFTER, CONCAT, UCASE, LCASE, ENCODE_FOR_URI |
| Numeric functions | ABS, ROUND, CEIL, FLOOR |
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

**SELECT query example:**
```csharp
var query = "SELECT * WHERE { ?person <http://foaf/name> ?name . ?person <http://foaf/age> ?age FILTER(?age > 25) } ORDER BY ?name LIMIT 10";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    var results = executor.Execute();

    while (results.MoveNext())
    {
        var bindings = results.Current;
        var nameIdx = bindings.FindBinding("?name".AsSpan());
        if (nameIdx >= 0)
        {
            var name = bindings.GetString(nameIdx);
            // Process name...
        }
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**ASK query example:**
```csharp
var query = "ASK WHERE { <http://example.org/Alice> <http://foaf/knows> ?someone }";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    bool exists = executor.ExecuteAsk();  // Returns true/false
}
finally
{
    store.ReleaseReadLock();
}
```

**CONSTRUCT query example:**
```csharp
var query = "CONSTRUCT { ?person <http://example.org/hasName> ?name } WHERE { ?person <http://foaf/name> ?name }";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    var results = executor.ExecuteConstruct();

    while (results.MoveNext())
    {
        var triple = results.Current;
        // triple.Subject, triple.Predicate, triple.Object are ReadOnlySpan<char>
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**GRAPH query examples:**
```csharp
// Query a specific named graph
var query = "SELECT * WHERE { GRAPH <http://example.org/graph1> { ?s ?p ?o } }";

// Query all named graphs with variable binding
var query = "SELECT ?g ?s ?p ?o WHERE { GRAPH ?g { ?s ?p ?o } }";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    var results = executor.Execute();

    while (results.MoveNext())
    {
        var bindings = results.Current;
        var gIdx = bindings.FindBinding("?g".AsSpan());
        if (gIdx >= 0)
        {
            var graphIri = bindings.GetString(gIdx);
            // graphIri contains the named graph IRI
        }
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**Subquery example:**
```csharp
// Subqueries can be combined with outer patterns
// Find emails for people who have names
var query = @"SELECT ?person ?email WHERE {
    ?person <http://foaf/email> ?email .
    { SELECT ?person WHERE { ?person <http://foaf/name> ?name } }
}";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    var results = executor.Execute();

    while (results.MoveNext())
    {
        var bindings = results.Current;
        // Results contain joined bindings from subquery and outer pattern
        var personIdx = bindings.FindBinding("?person".AsSpan());
        var emailIdx = bindings.FindBinding("?email".AsSpan());
        // ...
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**Property path query examples:**
```csharp
// Transitive closure: find all people reachable via knows* (0 or more hops)
var query = "SELECT ?reachable WHERE { <http://ex.org/Alice> <http://foaf/knows>* ?reachable }";
// Returns: Alice (0 hops), Bob (1 hop), Charlie (2 hops), etc.

// One-or-more: find people reachable via knows+ (at least 1 hop)
var query = "SELECT ?reachable WHERE { <http://ex.org/Alice> <http://foaf/knows>+ ?reachable }";
// Returns: Bob (1 hop), Charlie (2 hops), etc. - excludes Alice

// Inverse path: find who knows Alice (equivalent to ?knower <knows> <Alice>)
var query = "SELECT ?knower WHERE { <http://ex.org/Alice> ^<http://foaf/knows> ?knower }";

// Sequence path: find friends of friends
var query = "SELECT ?fof WHERE { <http://ex.org/Alice> <http://foaf/knows>/<http://foaf/knows> ?fof }";

// Alternative path: find connections via knows OR follows
var query = "SELECT ?connected WHERE { <http://ex.org/Alice> (<http://foaf/knows>|<http://ex.org/follows>) ?connected }";

var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    var results = executor.Execute();

    while (results.MoveNext())
    {
        var bindings = results.Current;
        // Process path results...
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**SERVICE query example (federated queries):**
```csharp
// SERVICE clause requires an ISparqlServiceExecutor
// Default implementation uses HttpClient + System.Text.Json (BCL only)
var query = "SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

// Create executor with HttpSparqlServiceExecutor
using var serviceExecutor = new HttpSparqlServiceExecutor();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery, serviceExecutor);
    var results = executor.Execute();

    while (results.MoveNext())
    {
        var bindings = results.Current;
        // Remote results are bound to ?s, ?p, ?o
    }
    results.Dispose();
    executor.Dispose();
}
finally
{
    store.ReleaseReadLock();
}

// SERVICE SILENT ignores errors and returns empty results on failure
var silentQuery = "SELECT * WHERE { SERVICE SILENT <http://might-fail.example.org/sparql> { ?x ?y ?z } }";
```

**SPARQL-star query example:**
```csharp
// SPARQL-star quoted triples are expanded to reification patterns at parse time
// This allows querying RDF-star data stored via the Turtle parser

// Query metadata about a specific triple
var query = @"SELECT ?confidence WHERE {
    << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
        <http://ex.org/confidence> ?confidence .
}";

// Query with variable inside quoted triple
var query2 = @"SELECT ?person ?score WHERE {
    << <http://ex.org/Alice> <http://ex.org/knows> ?person >>
        <http://ex.org/score> ?score .
}";

var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    var results = executor.Execute();

    while (results.MoveNext())
    {
        var bindings = results.Current;
        var confIdx = bindings.FindBinding("?confidence".AsSpan());
        if (confIdx >= 0)
        {
            var confidence = bindings.GetString(confIdx);
            // Process confidence value...
        }
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**Operator pipeline:**
- `TriplePatternScan` - Scans single pattern, binds variables from matching triples
- `MultiPatternScan` - Nested loop join for up to 12 patterns with backtracking (supports SPARQL-star expansion)
- `SlotTriplePatternScan` - Slot-based variant reading from 64-byte PatternSlot
- `SlotMultiPatternScan` - Slot-based multi-pattern join reading from byte[] buffer
- `SubQueryScan` - Executes nested SELECT subquery, projects selected variables
- `SubQueryJoinScan` - Joins subquery results with outer patterns via nested loop
- `ServiceScan` - Executes SERVICE clause against remote SPARQL endpoint via ISparqlServiceExecutor
- Filter/BIND/MINUS/VALUES evaluation integrated into result iteration

**QueryBuffer infrastructure:**

`QueryExecutor` uses `QueryBuffer` to store parsed patterns on the heap, avoiding stack overflow from large struct copies:

```csharp
// QueryBuffer stores patterns in pooled byte[] (~100 bytes vs ~9KB for Query struct)
// QueryBufferAdapter converts old Query struct to new buffer format
var buffer = QueryBufferAdapter.FromQuery(in query, source);

// Patterns accessed via PatternSlot views - no large struct copies
var patterns = buffer.GetPatterns();
foreach (var slot in patterns)
{
    if (slot.Kind == PatternKind.Triple)
    {
        // slot.SubjectType, slot.SubjectStart, slot.SubjectLength, etc.
    }
}
```

This enables GRAPH ?g queries and subquery joins to execute without thread workarounds that were previously needed to avoid stack overflow.

**SPARQL Update execution:**

```csharp
// INSERT DATA - add triples directly
var update = "INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }";
var parser = new SparqlParser(update.AsSpan());
var operation = parser.ParseUpdate();

var executor = new UpdateExecutor(store, update.AsSpan(), operation);
var result = executor.Execute();
// result.Success, result.AffectedCount, result.ErrorMessage

// DELETE DATA - remove specific triples
var update = "DELETE DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }";

// INSERT DATA with named graph
var update = @"INSERT DATA {
    GRAPH <http://ex.org/graph1> {
        <http://ex.org/s> <http://ex.org/p> <http://ex.org/o>
    }
}";

// DELETE WHERE - delete matching triples with pattern variables
var update = "DELETE WHERE { ?s <http://ex.org/type> <http://ex.org/Person> }";
// Deletes all triples where predicate is type and object is Person

// DELETE/INSERT WHERE - modify triples based on pattern matching
var update = @"DELETE { ?p <http://ex.org/status> ""active"" }
               INSERT { ?p <http://ex.org/status> ""inactive"" }
               WHERE { ?p <http://ex.org/status> ""active"" }";
// Changes status from "active" to "inactive" for all matching subjects

// INSERT WHERE - add new triples based on existing patterns
var update = @"INSERT { ?s <http://ex.org/type> <http://ex.org/Person> }
               WHERE { ?s <http://ex.org/name> ?name }";
// Adds type=Person triple for every subject that has a name

// WITH clause - scope updates to a named graph
var update = @"WITH <http://ex.org/graph1>
               DELETE { ?s <http://ex.org/status> ""active"" }
               INSERT { ?s <http://ex.org/status> ""inactive"" }
               WHERE { ?s <http://ex.org/status> ""active"" }";
// WITH scopes WHERE clause and template patterns to the named graph
// Explicit GRAPH clauses in templates can override WITH

// WITH with explicit GRAPH override
var update = @"WITH <http://ex.org/source>
               INSERT { GRAPH <http://ex.org/archive> { ?s <http://ex.org/archived> true } }
               WHERE { ?s <http://ex.org/status> ""active"" }";
// WHERE matches from <source> graph, INSERT goes to <archive> graph

// CLEAR operations
var update = "CLEAR DEFAULT";           // Clear default graph
var update = "CLEAR GRAPH <http://ex.org/g1>";  // Clear specific graph
var update = "CLEAR NAMED";             // Clear all named graphs
var update = "CLEAR ALL";               // Clear everything

// Graph management
var update = "COPY <http://ex.org/src> TO <http://ex.org/dst>";   // Copy triples
var update = "MOVE <http://ex.org/src> TO <http://ex.org/dst>";   // Move triples
var update = "ADD <http://ex.org/src> TO <http://ex.org/dst>";    // Add triples
var update = "DROP GRAPH <http://ex.org/g1>";                     // Drop graph

// LOAD - fetch RDF from URL and add to store
using var loadExecutor = new LoadExecutor();
var update = "LOAD <http://example.org/data.ttl>";
var parser = new SparqlParser(update.AsSpan());
var operation = parser.ParseUpdate();
var executor = new UpdateExecutor(store, update.AsSpan(), operation, loadExecutor);
var result = executor.Execute();  // Fetches and parses RDF into default graph

// LOAD into named graph
var update = "LOAD <http://example.org/data.ttl> INTO GRAPH <http://ex.org/imported>";

// LOAD SILENT - suppress errors on failure
var update = "LOAD SILENT <http://might-not-exist.example.org/data.ttl>";
```

**LOAD content negotiation:**

The `LoadExecutor` uses HTTP content negotiation to determine the RDF format:

| Content-Type | Parser |
|--------------|--------|
| `text/turtle`, `application/x-turtle` | TurtleStreamParser |
| `application/n-triples` | NTriplesStreamParser |
| `application/rdf+xml`, `text/xml`, `application/xml` | RdfXmlStreamParser |

If Content-Type is unclear, falls back to URL extension (`.ttl`, `.nt`, `.rdf`).

**UpdateResult struct:**
```csharp
public struct UpdateResult
{
    public bool Success;        // Whether operation completed successfully
    public int AffectedCount;   // Number of triples affected
    public string? ErrorMessage; // Error details if Success is false
}
```

### SPARQL Result Writers

Streaming writers for W3C SPARQL Query Results formats.

**JSON Format (`SparqlJsonResultWriter`):**
```csharp
using var sw = new StringWriter();
using var writer = new SparqlJsonResultWriter(sw);
writer.WriteHead(["s", "p", "o"]);

// For each result row from query execution:
writer.WriteResult(ref bindings);

writer.WriteEnd();
// Output: {"head":{"vars":["s","p","o"]},"results":{"bindings":[...]}}

// For ASK queries:
writer.WriteBooleanResult(true);
```

**XML Format (`SparqlXmlResultWriter`):**
```csharp
using var sw = new StringWriter();
using var writer = new SparqlXmlResultWriter(sw);
writer.WriteHead(["s", "p", "o"]);
writer.WriteResult(ref bindings);
writer.WriteEnd();
// Output: <sparql xmlns="..."><head>...</head><results>...</results></sparql>
```

**CSV/TSV Format (`SparqlCsvResultWriter`):**
```csharp
// CSV format
using var writer = new SparqlCsvResultWriter(sw);

// TSV format
using var writer = new SparqlCsvResultWriter(sw, useTsv: true);

writer.WriteHead(["s", "p", "o"]);
writer.WriteResult(ref bindings);
writer.WriteEnd();
```

| Format | Content-Type | Features |
|--------|--------------|----------|
| JSON | application/sparql-results+json | Full type info, datatypes, language tags |
| XML | application/sparql-results+xml | Full type info, datatypes, language tags |
| CSV | text/csv | Compact, values only (no type info) |
| TSV | text/tab-separated-values | Preserves RDF syntax (brackets, quotes) |

### Temporal SPARQL Extensions

Mercury exposes the bitemporal storage layer through SPARQL query syntax. All triples have implicit valid-time bounds (`ValidFrom`, `ValidTo`), and temporal clauses filter based on these bounds.

**Temporal query modes:**

| Mode | Syntax | Storage Method | Description |
|------|--------|----------------|-------------|
| Current | (default) | `QueryCurrent()` | Data valid at `UtcNow` |
| AS OF | `AS OF "date"^^xsd:date` | `QueryAsOf()` | Data valid at specific time |
| DURING | `DURING ["start"^^xsd:date, "end"^^xsd:date]` | `QueryChanges()` | Data overlapping period |
| ALL VERSIONS | `ALL VERSIONS` | `QueryEvolution()` | Complete history |

**AS OF query (point-in-time):**
```csharp
// Who worked where on June 15, 2021?
var query = @"SELECT ?person ?company
              WHERE { ?person <http://ex.org/worksFor> ?company }
              AS OF ""2021-06-15""^^xsd:date";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();

store.AcquireReadLock();
try
{
    var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
    var results = executor.Execute();
    // Returns only data where ValidFrom <= 2021-06-15 < ValidTo
    while (results.MoveNext())
    {
        var b = results.Current;
        // ...
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**DURING query (range):**
```csharp
// What employment changes happened in 2023?
var query = @"SELECT ?person ?company
              WHERE { ?person <http://ex.org/worksFor> ?company }
              DURING [""2023-01-01""^^xsd:date, ""2023-12-31""^^xsd:date]";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();
// Returns all versions whose validity period overlaps with 2023
```

**ALL VERSIONS query (history):**
```csharp
// Get Alice's complete employment history
var query = @"SELECT ?company
              WHERE { <http://ex.org/alice> <http://ex.org/worksFor> ?company }
              ALL VERSIONS";
var parser = new SparqlParser(query.AsSpan());
var parsedQuery = parser.ParseQuery();
// Returns all versions ever recorded for this pattern
```

**Temporal clauses with modifiers:**
```csharp
// Temporal clauses can be combined with LIMIT/OFFSET
var query = @"SELECT ?company
              WHERE { <http://ex.org/alice> <http://ex.org/worksFor> ?company }
              LIMIT 10 OFFSET 5
              ALL VERSIONS";
```

**Design notes:**
- Temporal clauses come after LIMIT/OFFSET in solution modifiers
- DateTime literals support both `xsd:date` and `xsd:dateTime` formats
- Zero-GC parsing stores datetime literals as offsets, parsed at execution time
- Default mode is `Current` (equivalent to calling `QueryCurrent()`)

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
