# Mercury API Usage Examples

This document provides code examples for the Mercury public API. For architecture overview and design decisions, see [CLAUDE.md](../../CLAUDE.md).

## Table of Contents

- [SPARQL Engine](#sparql-engine)
  - [SELECT Query](#select-query)
  - [ASK Query](#ask-query)
  - [CONSTRUCT Query](#construct-query)
  - [DESCRIBE Query](#describe-query)
  - [SPARQL Update](#sparql-update)
  - [EXPLAIN](#explain)
  - [Named Graphs](#named-graphs)
  - [Store Statistics](#store-statistics)
  - [Temporal Extensions](#temporal-extensions)
  - [Property Paths](#property-paths)
  - [SPARQL-star](#sparql-star)
  - [SERVICE / Federated Query](#service--federated-query)
- [RDF Engine](#rdf-engine)
  - [Format Detection](#format-detection)
  - [Loading Files](#loading-files)
  - [Loading Streams](#loading-streams)
  - [Parsing with Callbacks (Zero-GC)](#parsing-with-callbacks-zero-gc)
  - [Parsing to Materialized List](#parsing-to-materialized-list)
  - [Writing Triples](#writing-triples)
  - [Writing Quads](#writing-quads)
- [Pruning](#pruning)
  - [Basic Pruning](#basic-pruning)
  - [With Filtering](#with-filtering)
  - [Dry Run](#dry-run)
  - [History Modes](#history-modes)
- [Storage Layer](#storage-layer)
  - [Creating a Store](#creating-a-store)
  - [Batch Write API](#batch-write-api)
  - [Named Graphs (Quads)](#named-graphs-quads)
  - [Concurrent Read Pattern](#concurrent-read-pattern)
  - [Zero-GC Query Pattern](#zero-gc-query-pattern)
- [SPARQL HTTP Server](#sparql-http-server)
  - [Basic Usage](#basic-usage)
  - [Enable Updates](#enable-updates)
  - [curl Examples](#curl-examples)
- [Infrastructure](#infrastructure)
  - [Logging](#logging)
  - [Buffer Management](#buffer-management)
- [CLI Tools](#cli-tools)
  - [Mercury.Cli.Sparql](#mercuryclisparql)
  - [Mercury.Cli.Turtle](#mercurycliturtle)

---

## SPARQL Engine

The `SparqlEngine` facade handles SPARQL query parsing, execution, and result materialization. It manages read locks, cancellation tokens, and query optimization automatically.

### SELECT Query

```csharp
var result = SparqlEngine.Query(store, @"
    SELECT ?name ?age
    WHERE {
        ?person <http://foaf/name> ?name .
        ?person <http://foaf/age> ?age
        FILTER(?age > 25)
    }
    ORDER BY ?name LIMIT 10");

if (result.Success)
{
    // result.Variables: ["name", "age"]
    foreach (var row in result.Rows!)
    {
        var name = row["name"];
        var age = row["age"];
    }
}
```

### ASK Query

```csharp
var result = SparqlEngine.Query(store,
    "ASK WHERE { <http://example.org/Alice> <http://foaf/knows> ?someone }");

if (result.Success && result.AskResult == true)
{
    // Pattern exists in the store
}
```

### CONSTRUCT Query

```csharp
var result = SparqlEngine.Query(store, @"
    CONSTRUCT { ?person <http://example.org/hasName> ?name }
    WHERE { ?person <http://foaf/name> ?name }");

if (result.Success)
{
    foreach (var (subject, predicate, obj) in result.Triples!)
    {
        // subject, predicate, obj are strings
    }
}
```

### DESCRIBE Query

```csharp
var result = SparqlEngine.Query(store,
    "DESCRIBE <http://example.org/Alice>");

if (result.Success)
{
    foreach (var (subject, predicate, obj) in result.Triples!)
    {
        // All triples about Alice
    }
}
```

### SPARQL Update

```csharp
// INSERT DATA
var result = SparqlEngine.Update(store,
    "INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }");
// result.Success, result.AffectedCount

// DELETE DATA
SparqlEngine.Update(store,
    "DELETE DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }");

// DELETE/INSERT WHERE — modify triples based on pattern matching
SparqlEngine.Update(store, @"
    DELETE { ?p <http://ex.org/status> ""active"" }
    INSERT { ?p <http://ex.org/status> ""inactive"" }
    WHERE { ?p <http://ex.org/status> ""active"" }");

// WITH clause — scope updates to a named graph
SparqlEngine.Update(store, @"
    WITH <http://ex.org/graph1>
    DELETE { ?s <http://ex.org/status> ""active"" }
    INSERT { ?s <http://ex.org/status> ""inactive"" }
    WHERE { ?s <http://ex.org/status> ""active"" }");

// INSERT DATA with named graph
SparqlEngine.Update(store, @"
    INSERT DATA {
        GRAPH <http://ex.org/graph1> {
            <http://ex.org/s> <http://ex.org/p> <http://ex.org/o>
        }
    }");

// Graph management
SparqlEngine.Update(store, "CLEAR DEFAULT");
SparqlEngine.Update(store, "CLEAR GRAPH <http://ex.org/g1>");
SparqlEngine.Update(store, "COPY <http://ex.org/src> TO <http://ex.org/dst>");
SparqlEngine.Update(store, "MOVE <http://ex.org/src> TO <http://ex.org/dst>");
SparqlEngine.Update(store, "DROP GRAPH <http://ex.org/g1>");

// Cancellation support
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var result = SparqlEngine.Query(store, "SELECT * WHERE { ?s ?p ?o }", cts.Token);
```

### EXPLAIN

```csharp
// Basic EXPLAIN — plan only, no store needed
var plan = SparqlEngine.Explain(
    "SELECT * WHERE { ?s <http://ex.org/knows> ?o . ?o <http://ex.org/age> ?age } ORDER BY ?age LIMIT 10");
Console.WriteLine(plan);
```

Output:
```
QUERY PLAN
──────────────────────────────────────────────────────────
⌊ Slice (LIMIT 10)
  └─ ↑ Sort (ORDER BY ?age ASC)
       └─ ⋈ NestedLoopJoin
            ├─ ⊳ TriplePatternScan (?s <http://ex.org/knows> ?o) [binds: ?s, ?o]
            └─ ⊳ TriplePatternScan (?o <http://ex.org/age> ?age) [binds: ?age]
```

```csharp
// EXPLAIN ANALYZE — executes the query and collects statistics
var plan = SparqlEngine.Explain(
    "SELECT * WHERE { ?s <http://ex.org/knows> ?o }", store);
Console.WriteLine(plan);
```

### Named Graphs

```csharp
// Get all named graph IRIs
IReadOnlyList<string> graphs = SparqlEngine.GetNamedGraphs(store);
foreach (var graphIri in graphs)
{
    Console.WriteLine(graphIri);
}

// Query a specific named graph
var result = SparqlEngine.Query(store,
    "SELECT * WHERE { GRAPH <http://example.org/graph1> { ?s ?p ?o } }");

// Query all named graphs with variable binding
var result = SparqlEngine.Query(store,
    "SELECT ?g ?s ?p ?o WHERE { GRAPH ?g { ?s ?p ?o } }");
```

### Store Statistics

```csharp
var stats = SparqlEngine.GetStatistics(store);
Console.WriteLine($"Quads: {stats.QuadCount}");
Console.WriteLine($"Atoms: {stats.AtomCount}");
Console.WriteLine($"Size: {stats.TotalBytes / 1024 / 1024} MB");
Console.WriteLine($"WAL TxId: {stats.WalTxId}");
Console.WriteLine($"WAL Checkpoint: {stats.WalCheckpoint}");
Console.WriteLine($"WAL Size: {stats.WalSize}");
```

### Temporal Extensions

SPARQL syntax examples — temporal queries work through `SparqlEngine.Query()`:

```sparql
-- AS OF: point-in-time query
SELECT ?person ?company
WHERE { ?person <http://ex.org/worksFor> ?company }
AS OF "2021-06-15"^^xsd:date

-- DURING: range query (all versions overlapping the period)
SELECT ?person ?company
WHERE { ?person <http://ex.org/worksFor> ?company }
DURING ["2023-01-01"^^xsd:date, "2023-12-31"^^xsd:date]

-- ALL VERSIONS: complete history
SELECT ?company
WHERE { <http://ex.org/alice> <http://ex.org/worksFor> ?company }
ALL VERSIONS

-- Temporal clauses combine with LIMIT/OFFSET
SELECT ?company
WHERE { <http://ex.org/alice> <http://ex.org/worksFor> ?company }
LIMIT 10 OFFSET 5
ALL VERSIONS
```

### Property Paths

```sparql
-- Transitive closure: find all people reachable via knows* (0 or more hops)
SELECT ?reachable WHERE { <http://ex.org/Alice> <http://foaf/knows>* ?reachable }

-- One-or-more: at least 1 hop
SELECT ?reachable WHERE { <http://ex.org/Alice> <http://foaf/knows>+ ?reachable }

-- Inverse path: find who knows Alice
SELECT ?knower WHERE { <http://ex.org/Alice> ^<http://foaf/knows> ?knower }

-- Sequence path: friends of friends
SELECT ?fof WHERE { <http://ex.org/Alice> <http://foaf/knows>/<http://foaf/knows> ?fof }

-- Alternative path: knows OR follows
SELECT ?connected WHERE { <http://ex.org/Alice> (<http://foaf/knows>|<http://ex.org/follows>) ?connected }
```

### SPARQL-star

```sparql
-- Query metadata about a specific triple
SELECT ?confidence WHERE {
    << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
        <http://ex.org/confidence> ?confidence .
}

-- Query with variable inside quoted triple
SELECT ?person ?score WHERE {
    << <http://ex.org/Alice> <http://ex.org/knows> ?person >>
        <http://ex.org/score> ?score .
}
```

### SERVICE / Federated Query

```sparql
-- Query a remote SPARQL endpoint
SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }

-- SERVICE SILENT ignores errors and returns empty results on failure
SELECT * WHERE { SERVICE SILENT <http://might-fail.example.org/sparql> { ?x ?y ?z } }
```

---

## RDF Engine

The `RdfEngine` facade handles RDF parsing, writing, loading, and content negotiation across all six formats (Turtle, N-Triples, RDF/XML, N-Quads, TriG, JSON-LD).

### Format Detection

```csharp
// From MIME content type
var format = RdfEngine.DetermineFormat("text/turtle; charset=utf-8");
// Returns RdfFormat.Turtle

// From Accept header (picks best match)
var format = RdfEngine.NegotiateFromAccept("text/turtle;q=1.0, application/n-triples;q=0.8");
// Returns RdfFormat.Turtle

// Get MIME type for a format
var contentType = RdfEngine.GetContentType(RdfFormat.NTriples);
// Returns "application/n-triples"
```

### Loading Files

```csharp
// Load an RDF file into a store — format detected from extension
long count = await RdfEngine.LoadFileAsync(store, "data.ttl");
Console.WriteLine($"Loaded {count} triples");

// Supports: .ttl, .nt, .rdf, .xml, .nq, .trig, .jsonld
await RdfEngine.LoadFileAsync(store, "quads.nq");
```

### Loading Streams

```csharp
// Load from a stream with explicit format
await using var stream = File.OpenRead("data.ttl");
long count = await RdfEngine.LoadAsync(store, stream, RdfFormat.Turtle);

// With base URI for relative IRI resolution
await RdfEngine.LoadAsync(store, stream, RdfFormat.Turtle, baseUri: "http://example.org/");
```

### Parsing with Callbacks (Zero-GC)

```csharp
// Parse triples with zero-allocation callback — spans valid only during callback
await using var stream = File.OpenRead("data.nt");
await RdfEngine.ParseAsync(stream, RdfFormat.NTriples, (subject, predicate, obj) =>
{
    // subject, predicate, obj are ReadOnlySpan<char>
    // Process without allocation...
});

// For quad formats (NQuads, TriG, JsonLd), the graph component is ignored
// and only triples are delivered to the handler
```

### Parsing to Materialized List

```csharp
// Parse and materialize all triples as strings
await using var stream = File.OpenRead("data.ttl");
var triples = await RdfEngine.ParseTriplesAsync(stream, RdfFormat.Turtle);

foreach (var (subject, predicate, obj) in triples)
{
    Console.WriteLine($"{subject} {predicate} {obj}");
}
```

### Writing Triples

```csharp
var triples = new List<(string Subject, string Predicate, string Object)>
{
    ("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Bob>"),
    ("<http://ex.org/Alice>", "<http://ex.org/name>", "\"Alice\"@en"),
};

// Write as N-Triples
using var sw = new StringWriter();
RdfEngine.WriteTriples(sw, RdfFormat.NTriples, triples);

// Also supports: RdfFormat.Turtle, RdfFormat.RdfXml
RdfEngine.WriteTriples(sw, RdfFormat.Turtle, triples);
```

### Writing Quads

```csharp
var quads = new List<(string Subject, string Predicate, string Object, string Graph)>
{
    ("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/graph1>"),
    ("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", ""),  // default graph
};

// Write as N-Quads
using var sw = new StringWriter();
RdfEngine.WriteQuads(sw, RdfFormat.NQuads, quads);

// Also supports: RdfFormat.TriG, RdfFormat.JsonLd
RdfEngine.WriteQuads(sw, RdfFormat.TriG, quads);
```

---

## Pruning

The `PruneEngine` facade performs dual-instance pruning with copy-and-switch. It physically removes soft-deleted quads and optionally filters by graph or predicate.

### Basic Pruning

```csharp
// Prune the pool's active store — removes soft-deleted quads
var result = PruneEngine.Execute(pool);
Console.WriteLine($"Scanned: {result.QuadsScanned}, Written: {result.QuadsWritten}");
Console.WriteLine($"Saved: {result.BytesSaved} bytes in {result.Duration}");
```

### With Filtering

```csharp
var options = new PruneOptions
{
    ExcludeGraphs = ["<http://temp.data>", "<http://debug.data>"],
    ExcludePredicates = ["<http://internal/debug>"],
};
var result = PruneEngine.Execute(pool, options);
```

### Dry Run

```csharp
// Preview what would be pruned without writing
var options = new PruneOptions { DryRun = true };
var result = PruneEngine.Execute(pool, options);
Console.WriteLine($"Would scan {result.QuadsScanned}, write {result.QuadsWritten}");
Console.WriteLine($"DryRun: {result.DryRun}");  // true
```

### History Modes

```csharp
// FlattenToCurrent (default) — only current facts, most compact
var options = new PruneOptions { HistoryMode = HistoryMode.FlattenToCurrent };

// PreserveVersions — all versions excluding soft-deleted
var options = new PruneOptions { HistoryMode = HistoryMode.PreserveVersions };

// PreserveAll — full audit trail including soft-deleted
var options = new PruneOptions { HistoryMode = HistoryMode.PreserveAll };
```

---

## Storage Layer

### Creating a Store

```csharp
// Create or open a persistent store
using var store = new QuadStore("/path/to/store");

// With logging
var logger = new ConsoleLogger(LogLevel.Debug);
using var store = new QuadStore("/path/to/store", logger);
```

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

### Named Graphs (Quads)

```csharp
// Add to named graph
store.AddCurrent(subject, predicate, obj, "<http://example.org/graph1>");

// Add to default graph (no graph parameter)
store.AddCurrent(subject, predicate, obj);

// Query specific named graph
var results = store.QueryCurrent(subject, predicate, obj, "<http://example.org/graph1>");

// Query default graph (no graph parameter)
var results = store.QueryCurrent(subject, predicate, obj);
```

### Concurrent Read Pattern

QuadStore uses `ReaderWriterLockSlim` for thread-safety. Always wrap query enumeration with explicit locking:

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

### Zero-GC Query Pattern

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

---

## SPARQL HTTP Server

`SparqlHttpServer` implements W3C SPARQL 1.1 Protocol.

### Basic Usage

```csharp
var store = new QuadStore("/path/to/store");

// Create server with default options (read-only, CORS enabled)
var server = new SparqlHttpServer(store, "http://localhost:8080/");
server.Start();

// Server accepts requests at:
// - http://localhost:8080/sparql (queries)
// - http://localhost:8080/sparql/update (updates, if enabled)

// Stop server
await server.StopAsync();
server.Dispose();
```

### Enable Updates

```csharp
var options = new SparqlHttpServerOptions
{
    EnableUpdates = true,  // Allow INSERT/DELETE operations
    EnableCors = true,     // CORS headers for browser access
    CorsOrigin = "*"       // Allow all origins (customize for production)
};

var server = new SparqlHttpServer(store, "http://localhost:8080/", options);
server.Start();
```

### curl Examples

```bash
# GET with query parameter
curl "http://localhost:8080/sparql?query=SELECT%20*%20WHERE%20%7B%20%3Fs%20%3Fp%20%3Fo%20%7D%20LIMIT%2010"

# POST with form-encoded body
curl -X POST "http://localhost:8080/sparql" \
    -d "query=SELECT * WHERE { ?s ?p ?o } LIMIT 10"

# POST with direct query body
curl -X POST "http://localhost:8080/sparql" \
    -H "Content-Type: application/sparql-query" \
    -d "SELECT * WHERE { ?s ?p ?o } LIMIT 10"

# Update endpoint (POST only, requires EnableUpdates)
curl -X POST "http://localhost:8080/sparql/update" \
    -H "Content-Type: application/sparql-update" \
    -d "INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }"

# Service description (GET without query)
curl "http://localhost:8080/sparql"
```

---

## Infrastructure

### Logging

```csharp
// Production (zero overhead)
var store = new QuadStore("/path/to/store");

// Development (with logging)
var logger = new ConsoleLogger(LogLevel.Debug);
var store = new QuadStore("/path/to/store", logger);

// Custom logging
if (logger.IsEnabled(LogLevel.Debug))
    logger.Log(LogLevel.Debug, "Processing {0} triples".AsSpan(), count);

// Extension methods
logger.Info("Store opened".AsSpan());
logger.Warning("Large result set: {0}".AsSpan(), rowCount);
```

### Buffer Management

```csharp
// Simple pooled buffer
using var lease = PooledBufferManager.Shared.RentCharBuffer(1024);
var span = lease.Span;
// buffer automatically returned at end of scope

// Smart allocation: stack for small, pool for large
Span<char> stackBuffer = stackalloc char[256];
var span = PooledBufferManager.Shared.AllocateSmartChar(
    neededLength, stackBuffer, out var rented);
try
{
    // use span...
}
finally
{
    rented.Dispose(); // no-op if stack was used
}
```

---

## CLI Tools

Mercury provides two command-line tools for working with RDF data and SPARQL queries.

### Mercury.Cli.Sparql

Full-featured SPARQL CLI for loading RDF data and executing queries.

**Basic usage:**
```bash
# Build the CLI
dotnet build src/Mercury.Cli.Sparql

# Show help
dotnet run --project src/Mercury.Cli.Sparql -- --help
```

**Load and query (temp store):**
```bash
# Load Turtle file and run query (temp store, auto-deleted on exit)
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl \
    --query "SELECT * WHERE { ?s ?p ?o } LIMIT 10"
```

**Persistent named stores:**
```bash
# Create/open named store and load data
dotnet run --project src/Mercury.Cli.Sparql -- \
    --store ./mydb \
    --load data.ttl

# Query existing store (no reload needed)
dotnet run --project src/Mercury.Cli.Sparql -- \
    --store ./mydb \
    --query "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }"

# Add more data incrementally
dotnet run --project src/Mercury.Cli.Sparql -- \
    --store ./mydb \
    --load more-data.nt
```

**Output formats:**
```bash
# JSON (default)
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl -q "SELECT * WHERE { ?s ?p ?o }" --format json

# CSV
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl -q "SELECT * WHERE { ?s ?p ?o }" --format csv

# TSV
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl -q "SELECT * WHERE { ?s ?p ?o }" --format tsv

# XML
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl -q "SELECT * WHERE { ?s ?p ?o }" --format xml
```

**CONSTRUCT queries with RDF output:**
```bash
# CONSTRUCT with N-Triples output (default)
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format nt

# CONSTRUCT with Turtle output (grouped by subject)
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format ttl

# CONSTRUCT with RDF/XML output
dotnet run --project src/Mercury.Cli.Sparql -- \
    --load data.ttl -q "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }" --rdf-format rdf
```

**Query execution plan:**
```bash
# Show EXPLAIN plan
dotnet run --project src/Mercury.Cli.Sparql -- \
    --explain "SELECT * WHERE { ?s <http://ex.org/knows> ?o . ?o <http://ex.org/age> ?age }"
```

**Read query from file:**
```bash
# Execute query from .rq file
dotnet run --project src/Mercury.Cli.Sparql -- \
    --store ./mydb \
    --query-file query.rq
```

**Interactive REPL mode:**
```bash
# Start REPL with persistent store
dotnet run --project src/Mercury.Cli.Sparql -- \
    --store ./mydb \
    --repl

# REPL commands:
#   .help              Show available commands
#   .quit              Exit REPL
#   .load <file>       Load RDF file
#   .format [fmt]      Get/set SELECT output format (json, csv, tsv, xml)
#   .rdf-format [fmt]  Get/set CONSTRUCT output format (nt, ttl, rdf, nq, trig)
#   .count             Count triples in store
#   .store             Show store path
#   .explain <query>   Show query execution plan
#
# Multi-line queries: type across multiple lines, end with ; to execute
```

**Supported RDF formats:**
| Extension | Format |
|-----------|--------|
| `.ttl`, `.turtle` | Turtle |
| `.nt`, `.ntriples` | N-Triples |
| `.rdf`, `.xml` | RDF/XML |
| `.nq`, `.nquads` | N-Quads |
| `.trig` | TriG |
| `.jsonld` | JSON-LD |

---

### Mercury.Cli.Turtle

Turtle parser CLI for validation, format conversion, and performance benchmarking.

**Basic usage:**
```bash
# Build the CLI
dotnet build src/Mercury.Cli.Turtle

# Show help
dotnet run --project src/Mercury.Cli.Turtle -- --help

# Run demo (no arguments)
dotnet run --project src/Mercury.Cli.Turtle
```

**Validate Turtle syntax:**
```bash
# Validate file (reports errors with line/column)
dotnet run --project src/Mercury.Cli.Turtle -- --validate input.ttl

# Example output for valid file:
# Valid Turtle: 1,234 triples

# Example output for invalid file:
# Syntax error: Line 42, Column 15: Unexpected character '@'
```

**Show statistics:**
```bash
# Triple count, predicate distribution
dotnet run --project src/Mercury.Cli.Turtle -- --stats data.ttl

# Example output:
# Total triples:    1,234
# Unique subjects:  456
# Unique objects:   789
# Unique predicates: 12
#
# Predicate distribution:
#     500 ( 40.5%) <http://www.w3.org/1999/02/22-rdf-syntax-ns#type>
#     300 ( 24.3%) <http://xmlns.com/foaf/0.1/name>
#     ...
```

**Format conversion:**
```bash
# Convert Turtle to N-Triples (output file)
dotnet run --project src/Mercury.Cli.Turtle -- \
    --input data.ttl \
    --output data.nt

# Convert to N-Quads
dotnet run --project src/Mercury.Cli.Turtle -- \
    --input data.ttl \
    --output data.nq

# Convert to stdout with explicit format
dotnet run --project src/Mercury.Cli.Turtle -- \
    --input data.ttl \
    --output-format nt > data.nt
```

**Load into QuadStore:**
```bash
# Load Turtle into persistent store
dotnet run --project src/Mercury.Cli.Turtle -- \
    --input data.ttl \
    --store ./mydb

# The store can then be queried with Mercury.Cli.Sparql
dotnet run --project src/Mercury.Cli.Sparql -- \
    --store ./mydb \
    --query "SELECT * WHERE { ?s ?p ?o } LIMIT 10"
```

**Performance benchmark:**
```bash
# Run benchmark with generated data
dotnet run --project src/Mercury.Cli.Turtle -- \
    --benchmark \
    --count 100000

# Example output:
# === Mercury Turtle Parser Benchmark ===
#
# Source: generated (100,000 triples)
# Size: 3,982 KB
#
# Results:
#   Triples:     100,000
#   Time:        200 ms
#   Throughput:  500,000 triples/sec
#
# GC Collections:
#   Gen 0:       0
#   Gen 1:       0
#   Gen 2:       0
#
# Zero GC collections during parse!
```

**Output formats for conversion:**
| Format | Options |
|--------|---------|
| N-Triples | `nt`, `ntriples` |
| N-Quads | `nq`, `nquads` |
| TriG | `trig` |
| Turtle | `ttl`, `turtle` |
