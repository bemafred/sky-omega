# Embedding Mercury

Use Mercury as a library in your own .NET projects. This tutorial covers
adding the dependency, creating stores, writing and reading triples, running
SPARQL from code, parsing RDF files, and working with the zero-GC patterns.

> **Prerequisites:** .NET 10 SDK. Familiarity with C# and basic RDF concepts.
> See [Your First Knowledge Graph](your-first-knowledge-graph.md) for RDF
> basics.

---

## Adding Mercury to a Project

Mercury is a project reference (not yet published on NuGet). Clone the
repository and reference the project directly:

```xml
<ProjectReference Include="../sky-omega/src/Mercury/Mercury.csproj" />
```

Mercury has **no external dependencies** -- it uses only the BCL. This means
no transitive package conflicts.

For runtime utilities (store paths, cross-process coordination), also
reference:

```xml
<ProjectReference Include="../sky-omega/src/Mercury.Runtime/Mercury.Runtime.csproj" />
```

---

## Creating a Store

### Basic store

```csharp
using SkyOmega.Mercury.Storage;

// Creates the directory if it doesn't exist
using var store = new QuadStore("/path/to/my-store");
```

The store is a set of memory-mapped files: B+Tree indexes, an atom store,
and a write-ahead log. Always dispose the store when done.

### Temporary store

For tests or throwaway work:

```csharp
var tempPath = Path.Combine(Path.GetTempPath(), $"mercury-{Guid.NewGuid():N}");
using var store = new QuadStore(tempPath);

// ... use the store ...

// Clean up
Directory.Delete(tempPath, recursive: true);
```

### Store options

For testing, use reduced file sizes:

```csharp
using var store = new QuadStore(path, storageOptions: StorageOptions.ForTesting);
```

`StorageOptions.ForTesting` uses 64 MB indexes instead of the default 1 GB,
reducing disk usage from ~5.5 GB to ~320 MB per store.

---

## Writing Triples

### Single writes

```csharp
// Add to the default graph (valid from now, valid forever)
store.AddCurrent(
    "<http://example.org/alice>",
    "<http://xmlns.com/foaf/0.1/name>",
    "\"Alice\"");

// Add to a named graph
store.AddCurrent(
    "<http://example.org/alice>",
    "<http://xmlns.com/foaf/0.1/knows>",
    "<http://example.org/bob>",
    "<http://example.org/graph/people>");
```

Single writes call fsync after each operation (~250-300 writes/sec).

### Batch writes

For bulk loading, use the batch API to amortize fsync:

```csharp
store.BeginBatch();
try
{
    for (int i = 0; i < 10_000; i++)
    {
        store.AddCurrentBatched(
            $"<http://example.org/item/{i}>",
            "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
            "<http://example.org/Item>");
    }
    store.CommitBatch();  // Single fsync for entire batch
}
catch
{
    store.RollbackBatch();
    throw;
}
```

Performance: ~25,000 writes/sec (batch of 1,000), ~100,000 writes/sec
(batch of 10,000).

### Forgetting, and the store's clock

`DeleteCurrent` forgets a fact. On a Cognitive store the period in which it was remembered stays, with the time it
was memorized and the time it was forgotten:

```csharp
store.DeleteCurrent(
    "<http://example.org/alice>",
    "<http://example.org/worksFor>",
    "<http://example.org/Acme>");
```

No write takes a time: the store reads its own clock, `StorageOptions.Clock` (the system clock unless one is given),
once per write or batch. When something was true in the world is content: write it as triples, such as
`<http://example.org/alice-acme> <http://example.org/start> "2020-01-01"^^xsd:date`.

---

## Reading Triples

### Current state

```csharp
store.AcquireReadLock();
try
{
    // Query with any combination of subject/predicate/object
    // Pass ReadOnlySpan<char>.Empty for wildcards
    var results = store.QueryCurrent(
        "<http://example.org/alice>",           // subject
        ReadOnlySpan<char>.Empty,               // any predicate
        ReadOnlySpan<char>.Empty);              // any object

    while (results.MoveNext())
    {
        var triple = results.Current;
        Console.WriteLine($"{triple.Subject} {triple.Predicate} {triple.Object}");
    }
    results.Dispose();  // Return pooled buffer
}
finally
{
    store.ReleaseReadLock();
}
```

Always wrap queries with `AcquireReadLock`/`ReleaseReadLock` and call
`Dispose()` on results to return pooled buffers.

### Memories with their times

On a Cognitive store, `QueryMemories` gives each matching memory with its two times. Its bounds are those of the
words `MEMORIZED`, `FORGOTTEN` and `INCLUDING FORGOTTEN` ([Memory Time](temporal-rdf.md)):

```csharp
store.AcquireReadLock();
try
{
    // What was memorized in 2026 and is still remembered
    var memories = store.QueryMemories(subject, predicate, obj,
        memorizedSince: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        memorizedBefore: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    while (memories.MoveNext())
    {
        var memory = memories.Current;
        Console.WriteLine($"{memory.Object} memorized {memory.MemorizedAt:O}");
    }
    memories.Dispose();

    // Every period, forgotten ones too; ForgottenAt is null while a memory is remembered
    var everything = store.QueryMemories(subject, predicate, obj, includingForgotten: true);
    while (everything.MoveNext())
    {
        var memory = everything.Current;
        Console.WriteLine($"{memory.Object} {memory.MemorizedAt:O} to {memory.ForgottenAt?.ToString("O") ?? "now"}");
    }
    everything.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

On a Graph, Reference or Minimal store `QueryMemories` throws `ProfileCapabilityException`: those profiles keep no
times.

### Named graph queries

```csharp
// Query a specific named graph
var results = store.QueryCurrent(subject, predicate, obj,
    "<http://example.org/graph/people>");

// List all named graphs
var graphs = store.GetNamedGraphs();
while (graphs.MoveNext())
{
    Console.WriteLine(graphs.Current.ToString());
}
```

---

## SPARQL from Code

The `SparqlEngine` facade handles parsing, execution, read locking, and result
materialization in a single call.

### SELECT queries

```csharp
using SkyOmega.Mercury;

var result = SparqlEngine.Query(store,
    "SELECT ?name WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name }");

if (result.Success)
{
    // result.Variables: ["name"]
    foreach (var row in result.Rows!)
    {
        Console.WriteLine(row["name"]);
    }
}
```

### ASK queries

```csharp
var result = SparqlEngine.Query(store,
    "ASK WHERE { <http://example.org/alice> <http://xmlns.com/foaf/0.1/name> ?name }");

if (result.Success && result.AskResult == true)
{
    // Pattern exists
}
```

### CONSTRUCT queries

```csharp
var result = SparqlEngine.Query(store, @"
    CONSTRUCT { ?person <http://example.org/hasName> ?name }
    WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name }");

if (result.Success)
{
    foreach (var (subject, predicate, obj) in result.Triples!)
    {
        Console.WriteLine($"{subject} {predicate} {obj}");
    }
}
```

### SPARQL Update

```csharp
var result = SparqlEngine.Update(store,
    "INSERT DATA { <http://example.org/alice> <http://xmlns.com/foaf/0.1/name> \"Alice\" }");
// result.Success, result.AffectedCount
```

---

## Parsing RDF Files

The `RdfEngine` facade handles all six RDF formats (Turtle, N-Triples,
RDF/XML, N-Quads, TriG, JSON-LD) through a single API.

### Load a file into a store

```csharp
using SkyOmega.Mercury;

// Format detected from file extension (.ttl, .nt, .rdf, .nq, .trig, .jsonld)
long count = await RdfEngine.LoadFileAsync(store, "data.ttl");
Console.WriteLine($"Loaded {count} triples");
```

### Load from a stream

```csharp
await using var stream = File.OpenRead("data.nt");
long count = await RdfEngine.LoadAsync(store, stream, RdfFormat.NTriples);
```

### Zero-GC callback parsing

For processing without loading into a store:

```csharp
await using var stream = File.OpenRead("data.ttl");
await RdfEngine.ParseAsync(stream, RdfFormat.Turtle, (subject, predicate, obj) =>
{
    // subject, predicate, obj are ReadOnlySpan<char>
    // Valid only during this callback
    Console.WriteLine($"{subject} {predicate} {obj}");
});
```

### Materialize to a list

```csharp
await using var stream = File.OpenRead("data.ttl");
var triples = await RdfEngine.ParseTriplesAsync(stream, RdfFormat.Turtle);

foreach (var (subject, predicate, obj) in triples)
{
    Console.WriteLine($"{subject} {predicate} {obj}");
}
```

---

## Writing RDF Output

### Writing triples

```csharp
using SkyOmega.Mercury;

var triples = new List<(string Subject, string Predicate, string Object)>
{
    ("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\""),
    ("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/bob>"),
};

// Write as N-Triples
using var sw = new StringWriter();
RdfEngine.WriteTriples(sw, RdfFormat.NTriples, triples);
Console.Write(sw.ToString());

// Also supports: RdfFormat.Turtle, RdfFormat.RdfXml
```

### Writing quads

```csharp
var quads = new List<(string Subject, string Predicate, string Object, string Graph)>
{
    ("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"", "<http://example.org/graph1>"),
};

using var sw = new StringWriter();
RdfEngine.WriteQuads(sw, RdfFormat.NQuads, quads);
// Also supports: RdfFormat.TriG, RdfFormat.JsonLd
```

### Format detection

```csharp
// From MIME content type
var format = RdfEngine.DetermineFormat("text/turtle; charset=utf-8");
// Returns RdfFormat.Turtle

// From Accept header
var format2 = RdfEngine.NegotiateFromAccept("text/turtle;q=1.0, application/n-triples;q=0.8");
// Returns RdfFormat.Turtle

// Get MIME type for a format
var contentType = RdfEngine.GetContentType(RdfFormat.NTriples);
// Returns "application/n-triples"
```

---

## The Zero-GC Patterns

Mercury avoids garbage collection on hot paths. This means some APIs use
patterns that differ from typical .NET code.

### ReadOnlySpan callbacks

Parser callbacks receive `ReadOnlySpan<char>` instead of `string`. The spans
are valid only during the callback -- if you need the value later, call
`.ToString()`:

```csharp
await parser.ParseAsync((subject, predicate, obj) =>
{
    // CORRECT: use spans directly for store operations
    store.AddCurrent(subject, predicate, obj);

    // CORRECT: materialize to string if you need it later
    var subjectStr = subject.ToString();

    // WRONG: spans are invalid after callback returns
    // spanList.Add(subject);  // Will point to recycled memory
});
```

### Explicit read locking

`QuadStore` uses `ReaderWriterLockSlim` for thread safety. Query enumerators
are `ref struct` types that cannot hold locks internally, so locking is
explicit:

```csharp
store.AcquireReadLock();
try
{
    var results = store.QueryCurrent(s, p, o);
    while (results.MoveNext()) { /* process */ }
    results.Dispose();
}
finally
{
    store.ReleaseReadLock();
}
```

### Result disposal

Query results use `ArrayPool<T>` buffers. Always call `Dispose()` to return
them:

```csharp
var results = executor.Execute();
try
{
    while (results.MoveNext()) { /* process */ }
}
finally
{
    results.Dispose();
}
```

### When to use the library vs. the tools

| Use case | Choice |
|----------|--------|
| Interactive exploration | `mercury` CLI |
| Scripting, CI pipelines | `mercury-sparql`, `mercury-turtle` |
| Claude integration | `mercury-mcp` |
| Custom application logic | Mercury library (this tutorial) |
| Embedding in a web API | Mercury library |
| Unit testing with RDF data | Mercury library with `StorageOptions.ForTesting` |

---

## See Also

- [API Usage Reference](../api/api-usage.md) -- comprehensive API reference
  with all operations
- [Running Benchmarks](running-benchmarks.md) -- measuring performance
- [Temporal RDF](temporal-rdf.md) -- temporal API details
- [Your First Knowledge Graph](your-first-knowledge-graph.md) -- RDF basics
