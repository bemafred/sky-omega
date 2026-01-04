# Mercury API Usage Examples

This document provides detailed code examples for all Mercury APIs. For architecture overview and design decisions, see [CLAUDE.md](../CLAUDE.md).

## Table of Contents

- [Storage Layer](#storage-layer)
  - [Batch Write API](#batch-write-api)
  - [Named Graphs (Quads)](#named-graphs-quads)
  - [Concurrent Read Pattern](#concurrent-read-pattern)
  - [QuadStore Query (Zero-GC)](#quadstore-query-zero-gc)
- [RDF Parsers](#rdf-parsers)
  - [Turtle Parser](#turtle-parser)
  - [N-Triples Parser](#n-triples-parser)
  - [N-Quads Parser](#n-quads-parser)
  - [TriG Parser](#trig-parser)
  - [JSON-LD Parser](#json-ld-parser)
  - [RDF/XML Parser](#rdfxml-parser)
- [RDF Writers](#rdf-writers)
  - [N-Triples Writer](#n-triples-writer)
  - [Turtle Writer](#turtle-writer)
  - [RDF/XML Writer](#rdfxml-writer)
  - [N-Quads Writer](#n-quads-writer)
  - [TriG Writer](#trig-writer)
  - [JSON-LD Writer](#json-ld-writer)
- [SPARQL Engine](#sparql-engine)
  - [SELECT Query](#select-query)
  - [ASK Query](#ask-query)
  - [CONSTRUCT Query](#construct-query)
  - [GRAPH Query](#graph-query)
  - [Subquery](#subquery)
  - [Property Paths](#property-paths)
  - [SERVICE (Federated Query)](#service-federated-query)
  - [SPARQL-star](#sparql-star)
  - [QueryBuffer Infrastructure](#querybuffer-infrastructure)
- [SPARQL Update](#sparql-update)
  - [INSERT DATA / DELETE DATA](#insert-data--delete-data)
  - [DELETE WHERE / INSERT WHERE](#delete-where--insert-where)
  - [WITH Clause](#with-clause)
  - [Graph Management](#graph-management)
  - [LOAD](#load)
- [SPARQL EXPLAIN](#sparql-explain)
- [SPARQL Result Writers](#sparql-result-writers)
- [SPARQL Result Parsers](#sparql-result-parsers)
- [Content Negotiation](#content-negotiation)
- [Temporal SPARQL Extensions](#temporal-sparql-extensions)
- [OWL/RDFS Reasoning](#owlrdfs-reasoning)
- [SPARQL HTTP Server](#sparql-http-server)
- [Infrastructure](#infrastructure)
  - [Logging](#logging)
  - [Buffer Management](#buffer-management)
  - [Query Optimization](#query-optimization)

---

## Storage Layer

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

---

## RDF Parsers

### Turtle Parser

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

The Turtle parser supports RDF-star (RDF 1.2) syntax. Reified triples are converted to standard RDF reification triples:

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

### N-Triples Parser

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

### N-Quads Parser

**Zero-GC API:**
```csharp
await using var parser = new NQuadsStreamParser(stream);
await parser.ParseAsync((subject, predicate, obj, graph) =>
{
    // Spans valid only during callback
    // graph is empty for default graph
    store.AddCurrent(subject, predicate, obj, graph);
});
```

**Legacy API (allocates strings):**
```csharp
await foreach (var quad in parser.ParseAsync())
{
    // quad.Subject, Predicate, Object, Graph are strings
    // quad.Graph is null for default graph
}
```

### TriG Parser

**Zero-GC API:**
```csharp
await using var parser = new TriGStreamParser(stream);
await parser.ParseAsync((subject, predicate, obj, graph) =>
{
    // Spans valid only during callback
    // graph is empty for default graph
    store.AddCurrent(subject, predicate, obj, graph);
});
```

**Legacy API (allocates strings):**
```csharp
await foreach (var quad in parser.ParseAsync())
{
    // quad.Subject, Predicate, Object, Graph are strings
    // quad.Graph is null for default graph
}
```

### JSON-LD Parser

**Zero-GC API:**
```csharp
await using var parser = new JsonLdStreamParser(stream);
await parser.ParseAsync((subject, predicate, obj, graph) =>
{
    // Spans valid only during callback
    // graph is empty for default graph
    store.AddCurrent(subject, predicate, obj, graph);
});
```

**Legacy API (allocates strings):**
```csharp
await foreach (var quad in parser.ParseAsync())
{
    // quad.Subject, Predicate, Object, Graph are strings
}
```

### RDF/XML Parser

**Zero-GC API:**
```csharp
await using var parser = new RdfXmlStreamParser(stream);
await parser.ParseAsync((subject, predicate, obj) =>
{
    // Spans valid only during callback
    store.AddCurrent(subject, predicate, obj);
});
```

---

## RDF Writers

### N-Triples Writer

```csharp
using var sw = new StringWriter();
using var writer = new NTriplesStreamWriter(sw);
writer.WriteTriple("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
writer.WriteTriple("<http://ex.org/s>", "<http://ex.org/name>", "\"Alice\"@en");
```

### Turtle Writer

```csharp
using var sw = new StringWriter();
using var writer = new TurtleStreamWriter(sw);
writer.RegisterPrefix("ex", "http://example.org/");
writer.WritePrefixes();
writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/knows>".AsSpan(), "<http://example.org/Bob>".AsSpan());
writer.Flush(); // Finishes subject grouping
// Output: ex:Alice ex:knows ex:Bob .
```

### RDF/XML Writer

```csharp
using var sw = new StringWriter();
using var writer = new RdfXmlStreamWriter(sw);
writer.RegisterNamespace("ex", "http://example.org/");
writer.WriteStartDocument();
writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/knows>".AsSpan(), "<http://example.org/Bob>".AsSpan());
writer.WriteEndDocument();
```

### N-Quads Writer

```csharp
using var sw = new StringWriter();
using var writer = new NQuadsStreamWriter(sw);

// Write to named graph
writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/graph1>");

// Write to default graph (omit graph parameter)
writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
writer.WriteTriple("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"); // Equivalent
```

### TriG Writer

```csharp
using var sw = new StringWriter();
using var writer = new TriGStreamWriter(sw);

// Register prefixes
writer.RegisterPrefix("ex", "http://example.org/");
writer.WritePrefixes();

// Write to default graph
writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

// Write to named graph - consecutive quads in same graph are grouped
writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/graph1>");
writer.Flush();
```

**TriG output format:**
```turtle
@prefix ex: <http://example.org/> .

# Default graph
ex:s ex:p ex:o .

# Named graph
GRAPH <http://example.org/graph1> {
    ex:s ex:p ex:o .
}
```

### JSON-LD Writer

```csharp
using var sw = new StringWriter();
using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Compacted);

// Register prefixes for compacted output
writer.RegisterPrefix("foaf", "http://xmlns.com/foaf/0.1/");

// Write quads
writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
writer.WriteQuad("<http://example.org/alice>",
    "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
    "<http://xmlns.com/foaf/0.1/Person>");

writer.Flush();
```

**Compacted JSON-LD output:**
```json
{
  "@context": {
    "foaf": "http://xmlns.com/foaf/0.1/"
  },
  "@id": "http://example.org/alice",
  "@type": "foaf:Person",
  "foaf:name": "Alice"
}
```

---

## SPARQL Engine

### SELECT Query

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

### ASK Query

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

### CONSTRUCT Query

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

### GRAPH Query

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

### Subquery

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

### Property Paths

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

### SERVICE (Federated Query)

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

### SPARQL-star

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

### QueryBuffer Infrastructure

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

---

## SPARQL Update

### INSERT DATA / DELETE DATA

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
```

### DELETE WHERE / INSERT WHERE

```csharp
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
```

### WITH Clause

```csharp
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
```

### Graph Management

```csharp
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
```

### LOAD

```csharp
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

**UpdateResult struct:**
```csharp
public struct UpdateResult
{
    public bool Success;        // Whether operation completed successfully
    public int AffectedCount;   // Number of triples affected
    public string? ErrorMessage; // Error details if Success is false
}
```

---

## SPARQL EXPLAIN

**Basic EXPLAIN (plan only):**
```csharp
var query = "SELECT * WHERE { ?s <http://ex.org/knows> ?o . ?o <http://ex.org/age> ?age } ORDER BY ?age LIMIT 10";
var parser = new SparqlParser(query.AsSpan());
var parsed = parser.ParseQuery();

// Generate execution plan without running the query
var plan = parsed.Explain(query.AsSpan());

// Format as human-readable text
string textPlan = plan.Format(ExplainFormat.Text);
Console.WriteLine(textPlan);

// Format as JSON for tooling
string jsonPlan = plan.Format(ExplainFormat.Json);
```

**EXPLAIN ANALYZE (with execution statistics):**
```csharp
// Actually execute the query and capture timing/row counts
var plan = parsed.ExplainAnalyze(query.AsSpan(), store);

Console.WriteLine($"Total rows: {plan.TotalRows}");
Console.WriteLine($"Execution time: {plan.TotalExecutionTimeMs}ms");
Console.WriteLine(plan.Format(ExplainFormat.Text));
```

**Text output format:**
```
QUERY PLAN
──────────────────────────────────────────────────────────
⌊ Slice (LIMIT 10)
  └─ ↑ Sort (ORDER BY ?age ASC)
       └─ ⋈ NestedLoopJoin
            ├─ ⊳ TriplePatternScan (?s <http://ex.org/knows> ?o) [binds: ?s, ?o]
            └─ ⊳ TriplePatternScan (?o <http://ex.org/age> ?age) [binds: ?age]
```

---

## SPARQL Result Writers

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

---

## SPARQL Result Parsers

**JSON Format (`SparqlJsonResultParser`):**
```csharp
await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonContent));
await using var parser = new SparqlJsonResultParser(stream);
await parser.ParseAsync();

// Access variables
foreach (var varName in parser.Variables) { ... }

// Access rows
foreach (var row in parser.Rows)
{
    if (row.TryGetValue("name", out var value))
    {
        // value.Type: Uri, Literal, BlankNode
        // value.Value: the actual value
        // value.Datatype: datatype IRI (for typed literals)
        // value.Language: language tag (for language-tagged literals)
        Console.WriteLine(value.ToTermString()); // N-Triples format
    }
}

// For ASK queries
if (parser.IsAskResult)
{
    bool result = parser.BooleanResult.Value;
}
```

**XML Format (`SparqlXmlResultParser`):**
```csharp
await using var parser = new SparqlXmlResultParser(stream);
await parser.ParseAsync();
// Same API as JSON parser
```

**CSV/TSV Format (`SparqlCsvResultParser`):**
```csharp
// CSV format
await using var parser = new SparqlCsvResultParser(stream);

// TSV format
await using var parser = new SparqlCsvResultParser(stream, isTsv: true);

await parser.ParseAsync();
// Same API as JSON parser
```

**Content negotiation for parsers:**
```csharp
// From Content-Type header
using var parser = SparqlResultFormatNegotiator.CreateParser(stream, "application/sparql-results+json");

// From file extension
using var parser = SparqlResultFormatNegotiator.CreateParserFromPath(stream, "/results/output.srj");

// Direct format
using var parser = SparqlResultFormatNegotiator.CreateParser(stream, SparqlResultFormat.Json);
```

---

## Content Negotiation

**RDF Format Detection (`RdfFormatNegotiator`):**
```csharp
// Detect from Content-Type header
var format = RdfFormatNegotiator.FromContentType("text/turtle; charset=utf-8");
// Returns RdfFormat.Turtle

// Detect from file extension
var format = RdfFormatNegotiator.FromExtension(".nt");
// Returns RdfFormat.NTriples

// Detect from path (handles query strings)
var format = RdfFormatNegotiator.FromPath("http://example.org/data.rdf?version=2");
// Returns RdfFormat.RdfXml

// Negotiate: content type wins, falls back to path
var format = RdfFormatNegotiator.Negotiate("application/octet-stream", "/data/graph.ttl");
// Returns RdfFormat.Turtle (from path, since content type is unknown)

// Create parser/writer from format
using var parser = RdfFormatNegotiator.CreateParser(stream, RdfFormat.Turtle);
using var writer = RdfFormatNegotiator.CreateWriter(textWriter, RdfFormat.NTriples);

// Create from content negotiation
using var parser = RdfFormatNegotiator.CreateParser(stream, contentType: "text/turtle");
using var writer = RdfFormatNegotiator.CreateWriter(textWriter, path: "/output.rdf");
```

**SPARQL Result Format Detection (`SparqlResultFormatNegotiator`):**
```csharp
// Detect from Content-Type
var format = SparqlResultFormatNegotiator.FromContentType("application/sparql-results+json");
// Returns SparqlResultFormat.Json

// Parse Accept header with quality values
var format = SparqlResultFormatNegotiator.FromAcceptHeader(
    "application/sparql-results+json;q=0.8, application/sparql-results+xml;q=1.0");
// Returns SparqlResultFormat.Xml (higher quality)

// Create writer from Accept header
using var writer = SparqlResultFormatNegotiator.CreateWriter(textWriter, "application/sparql-results+xml");

// Create writer from file path
using var writer = SparqlResultFormatNegotiator.CreateWriterFromPath(textWriter, "/results/output.csv");
```

---

## Temporal SPARQL Extensions

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

---

## OWL/RDFS Reasoning

**Basic usage:**
```csharp
var store = new QuadStore("/path/to/store");

// Add ontology and data
store.AddCurrent("<http://ex.org/Dog>", "<http://www.w3.org/2000/01/rdf-schema#subClassOf>", "<http://ex.org/Animal>");
store.AddCurrent("<http://ex.org/Fido>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://ex.org/Dog>");

// Run reasoning with all rules
var reasoner = new OwlReasoner(store, InferenceRules.All);
int inferred = reasoner.Materialize();
// Fido is now also rdf:type Animal

// Query inferred facts
store.AcquireReadLock();
try
{
    var results = store.QueryCurrent(
        "<http://ex.org/Fido>".AsSpan(),
        "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>".AsSpan(),
        ReadOnlySpan<char>.Empty);
    while (results.MoveNext())
    {
        // Returns both Dog and Animal
    }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

**Selective rules:**
```csharp
// Only RDFS rules
var reasoner = new OwlReasoner(store, InferenceRules.AllRdfs);

// Only OWL transitive and symmetric
var reasoner = new OwlReasoner(store, InferenceRules.OwlTransitive | InferenceRules.OwlSymmetric);

// Specific graph only
int inferred = reasoner.Materialize(graph: "<http://ex.org/ontology>");
```

**Example - transitive property:**
```csharp
// Define ancestor as transitive
store.AddCurrent("<http://ex.org/ancestor>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
    "<http://www.w3.org/2002/07/owl#TransitiveProperty>");

// Add facts
store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/ancestor>", "<http://ex.org/Bob>");
store.AddCurrent("<http://ex.org/Bob>", "<http://ex.org/ancestor>", "<http://ex.org/Carol>");

var reasoner = new OwlReasoner(store, InferenceRules.OwlTransitive);
reasoner.Materialize();
// Now: Alice ancestor Carol (inferred)
```

**Example - inverse properties:**
```csharp
// Define hasChild as inverse of hasParent
store.AddCurrent("<http://ex.org/hasChild>", "<http://www.w3.org/2002/07/owl#inverseOf>", "<http://ex.org/hasParent>");

// Add fact
store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/hasChild>", "<http://ex.org/Bob>");

var reasoner = new OwlReasoner(store, InferenceRules.OwlInverse);
reasoner.Materialize();
// Now: Bob hasParent Alice (inferred)
```

---

## SPARQL HTTP Server

**Basic usage:**
```csharp
var store = new QuadStore("/path/to/store");

// Create server with default options (read-only, CORS enabled)
var server = new SparqlHttpServer(store, "http://localhost:8080/");
server.Start();

// Server is now accepting requests at:
// - http://localhost:8080/sparql (queries)
// - http://localhost:8080/sparql/update (updates, if enabled)

// Stop server
await server.StopAsync();
server.Dispose();
```

**Enable updates:**
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

**Query endpoint (GET/POST):**
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
```

**Update endpoint (POST only):**
```bash
# POST with direct update body
curl -X POST "http://localhost:8080/sparql/update" \
    -H "Content-Type: application/sparql-update" \
    -d "INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }"
```

**Service description:**
```bash
# GET without query returns service description (Turtle)
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

### Query Optimization

```csharp
// Collect statistics at checkpoint
store.Checkpoint();  // Automatically collects predicate cardinalities

// Create planner with statistics
var planner = new QueryPlanner(store.Statistics, store.Atoms);

// Execute with optimization
var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery, null, planner);
var results = executor.Execute();

// Plan caching for repeated queries
var cache = new QueryPlanCache(capacity: 1000);
var queryHash = QueryPlanCache.ComputeQueryHash(query.AsSpan());
var cached = cache.Get(queryHash, store.Statistics.LastUpdateTxId);

// EXPLAIN with estimated rows
var explainer = new SparqlExplainer(query.AsSpan(), parsedQuery, planner);
var plan = explainer.Explain();
// plan.Root.EstimatedRows now populated
```
