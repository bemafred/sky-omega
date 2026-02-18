# ADR-003: Mercury Encapsulation — Facade Over Internals

## Status

Accepted (2026-02-18) — All four phases complete

## Context

An empirical audit of Mercury's type visibility reveals that **over 140 types are
public** but only **~32 are referenced by any consumer project**. More importantly, the audit
of *how* consumers use those types reveals that they all follow the same pipeline:

```
string in → SparqlParser → Query → QueryExecutor → QueryResults → strings out
```

Every consumer — CLI, MCP, SparqlTool, and SparqlHttpServer — independently
re-implements this pipeline. The parse-execute-materialize sequence is duplicated
four times. Variable name extraction is duplicated four times (three different
implementations). Locking discipline around query execution is duplicated four
times.

### Why Everything Is Public

The root cause is not careless visibility. It is that **no facade exists**. Without
a single entry point that takes a SPARQL string and returns a result, every consumer
must assemble the pipeline from parts:

1. Construct a `SparqlParser` (public)
1. Call `ParseQuery()` → receive `Query` (public struct with public fields)
1. Branch on `query.Type` (forces `QueryType` public)
1. Construct a `QueryExecutor` (public)
1. Call `.Execute()` → receive `QueryResults` (public ref struct)
1. Iterate `QueryResults.Current` → access `BindingTable` (public ref struct)
1. Extract variable names from `BindingTable` or `Query.SelectClause` (forces
   `SelectClause`, `BindingTable`, and all transitive AST types public)

The `Query` struct’s public fields then pull the entire AST into public scope:
`WhereClause`, `GraphPattern`, `TriplePattern`, `FilterExpr`, `SolutionModifier`,
`GroupByClause`, `HavingClause`, `OrderByClause` — 39 SPARQL types that no
consumer inspects, but all must be public because `Query` is public with public
fields.

### What Consumers Actually Need

Examining every consumer’s actual data flow:

**CLI (`Mercury.Cli/Program.cs`):**

- `ExecuteQuery(store, sparql)` → materializes to `QueryResult` DTO
- `ExecuteUpdate(store, sparql)` → materializes to `UpdateResult` DTO
- `GetStatistics(store)` → materializes to `StoreStatistics` DTO
- `GetNamedGraphs(store)` → materializes to `List<string>`

**MCP (`MercuryTools.cs`):**

- Identical pipeline, materializes to `StringBuilder`

**SparqlTool (`SparqlTool.cs`):**

- Identical pipeline, formats to `TextWriter` in JSON/CSV/TSV/XML
- CONSTRUCT/DESCRIBE: streams triples to RDF format writers
- Explain: `SparqlExplainer(source, query)` → formatted string

**SparqlHttpServer (already inside Mercury):**

- Identical pipeline, streams to HTTP response
- The only consumer that genuinely needs streaming for large result sets

All three DTOs (`QueryResult`, `UpdateResult`, `StoreStatistics`) **already exist**
in `Mercury.Abstractions`. They were created for exactly this purpose but are
populated manually by each consumer rather than by Mercury itself.

### The Four-Way Duplication

Variable name extraction — the logic to map `BindingTable` hash-based bindings
back to named variables — exists in four independent implementations:

| Consumer | Method | Strategy | Hash Function |
|---|---|---|---|
| CLI | `ExtractVariableNames` | Hash-scan source text | FNV-1a |
| MCP | `ExtractVariableNames` | Hash-scan source text | FNV-1a |
| SparqlTool | `GetSelectVariables` | Read AST SelectClause | N/A |
| SparqlHttpServer | `ExtractVariableNames` + fallback | AST + hash-scan | `hash * 31 + c` |

**Latent bug:** The HTTP server's fallback `ExtractVariablesFromBindings` uses a
different hash function (`hash * 31 + c`) than CLI and MCP (FNV-1a with seed
`2166136261` and prime `16777619`). If `BindingTable` uses FNV-1a hashes internally,
the 31-based hash will never match. The divergence is a direct consequence of
independent re-implementation — exactly the class of bug a facade eliminates by
construction.

This is a clear signal that Mercury is missing an API.

## Decision

Introduce a **public static facade** inside Mercury that absorbs the entire
parse-execute-materialize pipeline. Make all SPARQL internals — parser, AST,
executor, query results, operators, patterns — **internal**.

### The Facade

A single public static class in the `SkyOmega.Mercury` namespace:

```csharp
namespace SkyOmega.Mercury;

/// <summary>
/// Public API for SPARQL operations against a QuadStore.
/// All parsing, planning, execution, and result materialization
/// is handled internally.
/// </summary>
public static class SparqlEngine
{
    /// <summary>
    /// Execute a SPARQL SELECT, ASK, CONSTRUCT, or DESCRIBE query.
    /// Handles locking, parsing, execution, variable name resolution,
    /// and result materialization.
    /// </summary>
    public static QueryResult Query(QuadStore store, string sparql,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a SPARQL UPDATE (INSERT, DELETE, LOAD, CLEAR, DROP, etc.).
    /// Handles parsing, execution, and LOAD operations internally.
    /// </summary>
    public static UpdateResult Update(QuadStore store, string sparql,
        CancellationToken ct = default);

    /// <summary>
    /// Generate an execution plan explanation for a SPARQL query.
    /// When a store is provided, includes estimated cardinalities from
    /// PredicateStats. Without a store, shows plan shape only.
    /// </summary>
    public static string Explain(string sparql, QuadStore? store = null);

    /// <summary>
    /// List all named graph IRIs in the store.
    /// Handles locking internally.
    /// </summary>
    public static IReadOnlyList<string> GetNamedGraphs(QuadStore store);

    /// <summary>
    /// Get store statistics.
    /// </summary>
    public static StoreStatistics GetStatistics(QuadStore store);
}
```

The return types (`QueryResult`, `UpdateResult`, `StoreStatistics`) already exist
in `Mercury.Abstractions` and already contain exactly the fields every consumer
populates manually today.

### RDF I/O Facade

The same pattern applies to RDF format handling. `SparqlTool.LoadRdfAsync` has a
six-way switch over `RdfFormat` to select the right parser. The CONSTRUCT/DESCRIBE
output paths in `SparqlTool` and `SparqlHttpServer` switch over formats to select
writers. Solid uses `TurtleStreamParser` and `NTriplesStreamParser` directly with
the same parse-iterate-store pattern.

```csharp
namespace SkyOmega.Mercury;

/// <summary>
/// Public API for RDF format loading, serialization, and content negotiation.
/// Absorbs format detection, parser/writer selection, and
/// the parse-iterate-store pipeline.
/// </summary>
public static class RdfEngine
{
    // ── Content Negotiation ──────────────────────────────────────

    /// <summary>
    /// Determine RDF format from a Content-Type header value.
    /// Returns RdfFormat.Unknown if unrecognized.
    /// </summary>
    public static RdfFormat DetermineFormat(ReadOnlySpan<char> contentType);

    /// <summary>
    /// Negotiate RDF format from an Accept header value.
    /// Returns <paramref name="defaultFormat"/> if no match.
    /// </summary>
    public static RdfFormat NegotiateFromAccept(ReadOnlySpan<char> acceptHeader,
        RdfFormat defaultFormat = RdfFormat.Turtle);

    /// <summary>
    /// Get the canonical Content-Type string for a format.
    /// </summary>
    public static string GetContentType(RdfFormat format);

    // ── Loading ──────────────────────────────────────────────────

    /// <summary>
    /// Load RDF from a file into a store. Detects format from extension.
    /// </summary>
    public static Task<long> LoadFileAsync(QuadStore store, string filePath);

    /// <summary>
    /// Load RDF from a stream into a store.
    /// </summary>
    public static Task<long> LoadAsync(QuadStore store, Stream stream,
        RdfFormat format, string? baseUri = null);

    // ── Parsing ──────────────────────────────────────────────────

    /// <summary>
    /// Parse RDF from a stream via zero-GC callback.
    /// Spans are valid only during the callback invocation.
    /// Used by consumers (e.g. Solid) that need streaming access
    /// without materializing a list.
    /// </summary>
    public static Task ParseAsync(Stream stream, RdfFormat format,
        Action<ReadOnlySpan<char>, ReadOnlySpan<char>, ReadOnlySpan<char>> handler,
        string? baseUri = null, CancellationToken ct = default);

    /// <summary>
    /// Parse RDF from a stream into a materialized list of triples.
    /// Convenience wrapper over <see cref="ParseAsync"/> for consumers
    /// that need all triples in memory.
    /// </summary>
    public static Task<List<(string Subject, string Predicate, string Object)>>
        ParseTriplesAsync(Stream stream, RdfFormat format, string? baseUri = null);

    // ── Writing ──────────────────────────────────────────────────

    /// <summary>
    /// Write triples to a TextWriter in the specified format.
    /// </summary>
    public static void WriteTriples(TextWriter output, RdfFormat format,
        IEnumerable<(string Subject, string Predicate, string Object)> triples);

    /// <summary>
    /// Write quads to a TextWriter in the specified format.
    /// </summary>
    public static void WriteQuads(TextWriter output, RdfFormat format,
        IEnumerable<(string Subject, string Predicate, string Object, string Graph)> quads);
}
```

This internalizes all 11 format parser/writer types plus `RdfFormatNegotiator`.
The content negotiation methods (`DetermineFormat`, `NegotiateFromAccept`,
`GetContentType`) absorb `RdfFormatNegotiator`'s public surface, allowing Solid's
HTTP handlers to negotiate content types without depending on the negotiator
directly. Solid's `baseUri` requirement is handled through the optional parameter.

### Pruning Facade

The pruning workflow is duplicated between CLI and MCP. Both construct filters
from user input, build `TransferOptions`, then execute the identical
clear→transfer→switch→clear sequence against a `QuadStorePool`. The entire
`Mercury.Pruning` namespace — 11 public types — exists to serve this one
workflow.

```csharp
namespace SkyOmega.Mercury;

/// <summary>
/// Public API for store pruning operations.
/// Absorbs filter construction, transfer orchestration,
/// and the clear-transfer-switch lifecycle.
/// </summary>
public static class PruneEngine
{
    /// <summary>
    /// Prune a pool by transferring live data from primary to secondary,
    /// then switching. Handles the full lifecycle: clear secondary,
    /// transfer, switch active, clear old.
    /// </summary>
    public static PruneResult Execute(QuadStorePool pool, PruneOptions options);
}

/// <summary>
/// Options for a prune operation. Replaces direct use of TransferOptions,
/// IPruningFilter, GraphFilter, PredicateFilter, and CompositeFilter.
/// </summary>
public sealed class PruneOptions
{
    public bool DryRun { get; init; }
    public HistoryMode HistoryMode { get; init; } = HistoryMode.FlattenToCurrent;
    public string[]? ExcludeGraphs { get; init; }
    public string[]? ExcludePredicates { get; init; }
}
```

`HistoryMode` moves from `Mercury.Pruning` to `Mercury.Abstractions` as a public
enum — it is part of the facade contract and has only three values
(`FlattenToCurrent`, `PreserveVersions`, `PreserveAll`). This gives consumers
compile-time validation without exposing any pruning internals.

The remaining 10 pruning types become internal: `PruningTransfer`,
`TransferOptions`, `TransferResult`, `TransferProgress`, `TransferVerification`,
`IPruningFilter`, `GraphFilter`, `PredicateFilter`, `CompositeFilter`,
`AllPassFilter`.

### What Becomes Internal

With the facade in place, **all** of the following become internal to Mercury:

- **SPARQL Parsing:** `SparqlParser`, `NTriplesParser`, `StreamingRdfLoader`,
  `ParseException`
- **SPARQL AST (all 39 types):** `Query`, `QueryType`, `WhereClause`,
  `GraphPattern`, `TriplePattern`, `FilterExpr`, `SelectClause`, `Prologue`,
  `SolutionModifier`, `GroupByClause`, `HavingClause`, `OrderByClause`,
  `TemporalClause`, `ConstructTemplate`, `DatasetClause`, `ValuesClause`,
  `UpdateOperation`, `Term`, `TermType`, `Binding`, `BindingValueType`,
  `PropertyPath`, `PathType`, `BindExpr`, `GraphClause`, `ServiceClause`,
  `ExistsFilter`, `SubSelect`, `CompoundExistsRef`, `QuadData`, `GraphTarget`,
  `GraphTargetType`, `OrderDirection`, `AggregateFunction`, `AggregateExpression`,
  `OrderCondition`, `TemporalQueryMode`, `TemporalClause`, `SparqlParseException`
- **SPARQL Execution:** `QueryExecutor`, `UpdateExecutor`, `QueryResults`,
  `ConstructResults`, `DescribeResults`, `ConstructedTriple`, `BindingTable`,
  `Value`, `ValueType`, `ComparisonOperator`, `FilterEvaluator`,
  `BindExpressionEvaluator`, `FilterAnalyzer`, `FilterAssignment`,
  `QueryPlanner`, `QueryPlanCache`, `IScan`, all scan operators
- **SPARQL Explain:** `SparqlExplainer`, `ExplainPlan`, `ExplainNode`,
  `ExplainOperatorType`, `ExplainFormat`, `SparqlExplainExtensions`
- **SPARQL Federated:** `LoadExecutor`, `LoadExecutorOptions`,
  `ServiceMaterializer`, `HttpSparqlServiceExecutor`, `ISparqlServiceExecutor`,
  all service types, all exception types
- **SPARQL Results I/O:** `SparqlCsvResultWriter`, `SparqlJsonResultWriter`,
  `SparqlXmlResultWriter`, all result parsers, `SparqlResultFormat`,
  `SparqlResultFormatNegotiator`, `SparqlResultValue`, `SparqlValueType`,
  `SparqlResultRow`
- **SPARQL Patterns:** `PatternSlot`, `PatternArray`, `QueryBuffer`, and all
  related internal types (already internal)
- **Storage internals:** `TemporalResultEnumerator`, `NamedGraphEnumerator`,
  `ResolvedTemporalQuad`, `TemporalQuery`, `TemporalQueryType`, `TemporalQuad`,
  `TemporalIndexType`, `PredicateStats`, `StatisticsStore`,
  `QuadStorePoolOptions`, `PooledStoreLease`, `StorageOptions`, `DiskSpaceChecker`
- **Rdf model types:** `TripleRef`, `Triple`, `RdfTriple`, `RdfLiteral`,
  `RdfTermType`, `RdfQuad`, `ParserStatistics`
- **OWL:** `OwlReasoner`, `InferenceRules`
- **Diagnostics (most):** `DiagnosticFormatter`, `DiagnosticJsonFormatter`,
  `Diagnostic`, `DiagnosticMessages`, `DiagnosticSeverity`, `DiagnosticCode`,
  `SourceSpan`, `DiagnosticBag`, `MaterializedDiagnostic`, `ConsoleLogger`,
  `LoggerExtensions`
- **JsonLd support:** `JsonLdStreamWriter`, `JsonLdForm`, `IContextResolver`,
  `FileContextResolver`, `NullContextResolver`, `JsonLdContextException`
- **RDF Format Parsers:** `TurtleStreamParser`, `NTriplesStreamParser`,
  `NQuadsStreamParser`, `TriGStreamParser`, `RdfXmlStreamParser`,
  `JsonLdStreamParser`, `RdfFormatNegotiator`
- **RDF Format Writers:** `TurtleStreamWriter`, `NTriplesStreamWriter`,
  `NQuadsStreamWriter`, `TriGStreamWriter`, `RdfXmlStreamWriter`
- **Pruning (10 types):** `PruningTransfer`, `TransferOptions`,
  `TransferResult`, `TransferProgress`, `TransferVerification`,
  `IPruningFilter`, `GraphFilter`, `PredicateFilter`, `CompositeFilter`,
  `AllPassFilter` (`HistoryMode` moves to `Mercury.Abstractions` — see Pruning
  Facade section)

### What Remains Public

The public surface of Mercury reduces to **21 types** (down from ~161):

| Area | Public Types | Count |
|---|---|---|
| **Facades** | `SparqlEngine`, `RdfEngine`, `PruneEngine` | 3 |
| **Facade delegates** | `RdfTripleHandler`, `RdfQuadHandler` | 2 |
| **Protocol** | `SparqlHttpServer`, `SparqlHttpServerOptions` | 2 |
| **Storage core** | `QuadStore`, `QuadStorePool`, `PooledStoreLease` | 3 |
| **Storage config** | `StorageOptions`, `QuadStorePoolOptions` | 2 |
| **Storage stats** | `StatisticsStore`, `PredicateStats`, `TemporalQueryType` | 3 |
| **Storage enumerators** | `TemporalResultEnumerator`, `NamedGraphEnumerator`, `ResolvedTemporalQuad` | 3 |
| **Diagnostics** | `ILogger`, `NullLogger`, `LogLevel` | 3 |

The storage types (11) are public because they appear in `QuadStore`'s public
signatures — constructors, properties, method parameters, and return types.
These provide the low-level API for non-SPARQL consumers (e.g., Solid's LDP
operations, the Examples project).

Plus the DTOs already in `Mercury.Abstractions`: `QueryResult`, `UpdateResult`,
`StoreStatistics`, `PruneResult`, `ExecutionResultKind`, `RdfFormat`,
`HistoryMode`, `ByteFormatter`, etc.

### InternalsVisibleTo

*Updated after Phase 3 implementation:* Three new `InternalsVisibleTo` entries
were required, contrary to the original prediction of zero:

**Mercury.csproj** grants internal access to:
- `Mercury.Tests` — white-box testing (pre-existing)
- `Mercury.Benchmarks` — benchmark access (pre-existing)
- `SkyOmega.Mercury.Solid` — Solid HTTP handlers use internal stream writers
  and parsers for LDP content negotiation
- `SkyOmega.Mercury.Pruning` — uses internal `LoggerExtensions`
- `Mercury.Turtle.Tool` — format conversion tool uses internal parsers/writers

**Mercury.Pruning.csproj** grants internal access to:
- `Mercury.Tests` — white-box testing of pruning internals

CLI, MCP, and SparqlTool are pure consumers of the public API (facades +
`QuadStore`). They do not need `InternalsVisibleTo`.

### Consumer Simplification

The facade eliminates the duplicated pipeline in every consumer. For example,
`MercuryTools.Query()` (currently ~70 lines) becomes:

```csharp
[McpServerTool(Name = “mercury_query”)]
public string Query(string query)
{
    var result = SparqlEngine.Query(_pool.Active, query);

    if (!result.Success)
        return $”Error: {result.ErrorMessage}”;

    return result.Kind switch
    {
        ExecutionResultKind.Select => FormatSelectResult(result),
        ExecutionResultKind.Ask => result.AskResult == true ? “true” : “false”,
        ExecutionResultKind.Construct or
        ExecutionResultKind.Describe => FormatTriples(result),
        _ => $”Error: Unsupported query type”
    };
}
```

The CLI’s `ExecuteQuery` function (currently ~50 lines including locking,
parsing, type-branching, variable extraction, and materialization) becomes:

```csharp
static QueryResult ExecuteQuery(QuadStore store, string sparql)
    => SparqlEngine.Query(store, sparql);
```

`SparqlTool.ExecuteQuery` (currently ~30 lines of parsing and branching)
simplifies similarly, using the `QueryResult` DTO directly for all formatting.
The CONSTRUCT/DESCRIBE path materializes triples in the DTO; the tool then
serializes them through `RdfEngine.WriteTriples()`.

`SparqlTool.LoadRdfAsync` (currently ~70 lines with a six-way format switch)
becomes:

```csharp
public static Task<long> LoadRdfAsync(QuadStore store, string filePath)
    => RdfEngine.LoadFileAsync(store, filePath);
```

The MCP prune tool (currently ~50 lines of filter construction and workflow
orchestration) becomes:

```csharp
[McpServerTool(Name = "mercury_prune")]
public string Prune(bool dryRun = false, string historyMode = "flatten",
    string? excludeGraphs = null, string? excludePredicates = null)
{
    var mode = historyMode switch
    {
        "preserve" => HistoryMode.PreserveVersions,
        "all" => HistoryMode.PreserveAll,
        _ => HistoryMode.FlattenToCurrent
    };

    var result = PruneEngine.Execute(_pool, new PruneOptions
    {
        DryRun = dryRun,
        HistoryMode = mode,
        ExcludeGraphs = excludeGraphs?.Split(',', StringSplitOptions.TrimEntries),
        ExcludePredicates = excludePredicates?.Split(',', StringSplitOptions.TrimEntries)
    });

    return FormatPruneResult(result);
}
```

### SparqlHttpServer — Migrated to Facades

*Updated 2026-02-18:* The original plan kept SparqlHttpServer on direct internal
access for streaming. During Phase 2 implementation, SparqlHttpServer was
migrated to facades because: (a) it eliminated the hash-mismatch bug by removing
the divergent variable extraction code, (b) result set sizes in practice are
bounded by LIMIT clauses and client timeouts, (c) the materialization overhead is
negligible compared to HTTP I/O. SparqlHttpServer now uses `SparqlEngine.Query()`,
`SparqlEngine.Update()`, and inline format writing from `QueryResult` DTOs.

## Implementation

### Phase 1: Create Facades ✓

*Completed 2026-02-18 — commit 034ef17*

Created three public static classes inside the Mercury project:

**`SparqlEngine`** (`src/Mercury/SparqlEngine.cs`):
Consolidates duplicated pipeline logic from CLI, MCP, SparqlTool, and
SparqlHttpServer. Variable name extraction uses FNV-1a hash matching for all
queries (both explicit SELECT and SELECT *), correctly mapping binding columns
to variable names regardless of pattern order.

**`RdfEngine`** (`src/Mercury/RdfEngine.cs`):
Consolidates the format-switch logic from `SparqlTool.LoadRdfAsync`. Write
methods consolidate format-switch logic from CONSTRUCT/DESCRIBE output paths.
`ParseTriplesAsync` covers Solid's pattern of parsing to a triple list.
`LoadFileAsync` buffers the file into a MemoryStream before entering the batch
write lock to avoid thread-affinity issues with `ReaderWriterLockSlim` during
async parsing.

**`PruneEngine`** (`src/Mercury.Pruning/PruneEngine.cs`):
Extracts the filter construction and clear→transfer→switch→clear workflow.
`PruneOptions` and `HistoryMode` moved to `Mercury.Abstractions`.

**Verification:** 57 facade tests added (SparqlEngineTests, RdfEngineTests,
PruneEngineTests). Full test suite passes (3,995 tests, 0 failures).

### Phase 2: Migrate Consumers ✓

*Completed 2026-02-18 — commit 034ef17*

Rewrote all consumer code to use the facades:

| Consumer | Lines removed | Notes |
|---|---:|---|
| CLI | ~308 | Lambda replacement + deleted 8 helper functions |
| MCP | ~276 | Rewritten all tool methods, removed LoadExecutor |
| SparqlTool | ~407 | Rewritten execution methods, deleted format pipeline |
| SparqlHttpServer | ~607 | Rewritten execution, fixed hash bug |
| Solid (Resource + Container) | ~38 each | Replaced ParseRdfAsync with RdfEngine |

Removed all duplicated implementations: `ExtractVariableNames` (4 copies),
format-switch loading (2 copies), pruning workflow (2 copies). Net result:
+1,983 / -1,580 lines across 18 files.

**Deviation from plan:** SparqlHttpServer was also migrated to use facades
(the ADR originally proposed keeping it on direct internal access). This
eliminated the hash-mismatch bug (`hash * 31 + c` vs FNV-1a) by removing
the divergent `ComputeHash`/`ExtractVariablesFromBindings` code entirely.

**Bugs found and fixed during migration:**
1. **Thread-affinity in `RdfEngine.LoadFileAsync`:** `BeginBatch()` holds a
   `ReaderWriterLockSlim` write lock, but `FileStream.ReadAsync` can resume on
   a different thread. Fixed by buffering the file into a `MemoryStream` first.
2. **Variable name mismatch in `SparqlEngine.ExecuteSelect`:** For explicit
   SELECT queries, the facade initially used SELECT clause variable order for
   naming, but binding table columns are in pattern match order. Fixed by always
   using FNV-1a hash matching for column-to-name mapping.

**Verification:** All 3,995 tests pass. Specific groups verified: REPL (106),
SparqlTool (12), Solid (25), Facade (57).

### Phase 3: Internalize ✓

*Completed 2026-02-18*

Changed ~130 types from `public` to `internal` across seven groups: SPARQL types
(~66), RDF format parsers/writers (~16), RDF core types (6), Storage subset (5),
Diagnostics (15), OWL (2), and Mercury.Pruning (10). Four test methods that
exposed internal enums (`SparqlResultFormat`, `DiagnosticSeverity`) in public
`[Theory]` parameter signatures were fixed by casting to `int`.

**Deviations from plan:**

1. **Three `InternalsVisibleTo` entries added** (the ADR predicted none needed):
   - `SkyOmega.Mercury.Solid` — Solid GET handlers use internal stream writers
   - `SkyOmega.Mercury.Pruning` — uses internal `LoggerExtensions`
   - `Mercury.Turtle.Tool` — format conversion tool uses internal parsers/writers

2. **Five storage types kept public** (build forced — used in `QuadStore` public
   signatures): `StatisticsStore`, `PredicateStats`, `TemporalQueryType`,
   `StorageOptions`, `QuadStorePoolOptions`

3. **Enumerator types kept public** (`TemporalResultEnumerator`,
   `NamedGraphEnumerator`, `ResolvedTemporalQuad`, `PooledStoreLease`) — returned
   from public `QuadStore`/`QuadStorePool` methods. Phase 4 evaluates whether
   duck-typed `foreach` can allow internalization.

**Final public type count:** 21 types across Mercury + Mercury.Pruning (down from
~161). See "What Remains Public" section for the intended ~15 — the delta is the
storage/enumerator types kept public by necessity.

**Verification:** `dotnet build` — 0 errors, 0 warnings. `dotnet test` — 3,995
tests pass (3,970 + 25 Solid, 6 known JSON-LD skips).

### Phase 4: Audit and Confirm QuadStore Surface ✓

*Completed 2026-02-18*

With SPARQL, RDF I/O, and pruning internals hidden, audit the remaining `QuadStore`
public API to confirm the current surface is intentional.

**Audit findings:**

Post-Phase 3, direct `QuadStore` usage by consumer (excluding InternalsVisibleTo
projects):

| Consumer | Direct QuadStore usage | Notes |
|---|---|---|
| CLI | None | Fully on `SparqlEngine` facades |
| MCP | None | Fully on `SparqlEngine` facades |
| SparqlTool | `.count` command: `AcquireReadLock`/`QueryCurrent` | Replaceable with `GetStatistics().QuadCount` |
| Examples | `QueryCurrent`, batch API, temporal queries | Intentional — demonstrates low-level API |

Solid (InternalsVisibleTo) uses locking, batch, and query methods extensively for
Linked Data Platform operations. Pruning (InternalsVisibleTo) uses locking and
batch for transfer orchestration.

**Decisions:**

1. **Lock methods (`AcquireReadLock`/`ReleaseReadLock`)** — stay public. The
   Examples project demonstrates them, and external consumers building non-SPARQL
   applications (like Solid) need direct store access. Facades handle locking for
   SPARQL workloads; the low-level API remains for everything else.

2. **Batch methods (`BeginBatch`/`CommitBatch`/`AddCurrentBatched`)** — stay public.
   Same reasoning: Examples demonstrate them, Solid uses them for LDP writes.

3. **Query methods (`QueryCurrent`, `QueryAsOf`, etc.)** — stay public. Fundamental
   low-level API; Examples demonstrate them.

4. **Enumerator types (`TemporalResultEnumerator`, `NamedGraphEnumerator`,
   `ResolvedTemporalQuad`)** — stay public. These are ref structs and cannot
   implement interfaces, so duck-typed `foreach` would require the caller to see
   the concrete type. Internalizing them would break any external consumer calling
   `QueryCurrent()` or `GetNamedGraphs()`.

5. **`StorageOptions`, `QuadStorePoolOptions`** — stay public. Constructor
   parameters for `QuadStore` and `QuadStorePool`.

6. **`PooledStoreLease`** — stays public. RAII wrapper returned by
   `QuadStorePool.RentScoped()`. Minimal 16-byte readonly struct, zero allocation,
   idiomatic `using` pattern. No benefit to internalizing — it's part of the pool's
   public contract.

7. **`StatisticsStore`, `PredicateStats`, `TemporalQueryType`** — stay public.
   Used in `QuadStore` public property/method signatures.

**One cleanup:** Replace SparqlTool's `.count` command to use
`store.GetStatistics().QuadCount` instead of manual `QueryCurrent` iteration.
This eliminates the last non-InternalsVisibleTo consumer use of locking.

**Final public surface (21 types):**

| Area | Public Types | Count |
|---|---|---|
| Facades | `SparqlEngine`, `RdfEngine`, `PruneEngine` | 3 |
| Facade delegates | `RdfTripleHandler`, `RdfQuadHandler` | 2 |
| Protocol | `SparqlHttpServer`, `SparqlHttpServerOptions` | 2 |
| Storage core | `QuadStore`, `QuadStorePool`, `PooledStoreLease` | 3 |
| Storage config | `StorageOptions`, `QuadStorePoolOptions` | 2 |
| Storage stats | `StatisticsStore`, `PredicateStats`, `TemporalQueryType` | 3 |
| Storage enumerators | `TemporalResultEnumerator`, `NamedGraphEnumerator`, `ResolvedTemporalQuad` | 3 |
| Diagnostics | `ILogger`, `NullLogger`, `LogLevel` | 3 |

This is the correct and final public surface. The delta from the originally
predicted ~15 (6 additional types) is entirely storage types that must be public
because they appear in `QuadStore`'s public signatures — not accidental exposure.

**Verification:** SparqlTool `.count` fixed to use `GetStatistics().QuadCount`.
`dotnet build` — 0 errors, 0 warnings. SparqlTool (12) and REPL (106) tests pass.

## Implementation Notes for Claude Code

### Phase Ordering Is Strict

Phase 1 must be complete and verified before Phase 2. Phase 3 must wait until
Phase 2 is stable. Do not combine phases.

### The Facade Implementations Are Consolidation, Not New Logic

**`SparqlEngine.Query()`** should consolidate logic from these four existing
implementations:

1. `Mercury.Cli/Program.cs` → `ExecuteQuery()`
1. `Mercury.Mcp/MercuryTools.cs` → `Query()`
1. `Mercury.Sparql.Tool/SparqlTool.cs` → `ExecuteQuery()`
1. `Mercury/Sparql/Protocol/SparqlHttpServer.cs` → `ExecuteQuerySync()`

The HTTP server version is the most complete (handles aggregates in variable
extraction). Use it as the reference implementation. The DTO population matches
what CLI and MCP already do.

**`RdfEngine.LoadFileAsync()`** should consolidate:

1. `Mercury.Sparql.Tool/SparqlTool.cs` → `LoadRdfAsync()`
1. `Mercury/Sparql/Execution/Federated/LoadExecutor.cs` (internal LOAD handler)

The format-switch logic is identical. `ParseTriplesAsync` covers Solid’s pattern
of parsing to an in-memory list. `WriteTriples` / `WriteQuads` consolidate the
format writer selection from `SparqlTool`’s CONSTRUCT/DESCRIBE paths.

**`PruneEngine.Execute()`** should consolidate:

1. `Mercury.Cli/Program.cs` → `ExecutePrune()`
1. `Mercury.Mcp/MercuryTools.cs` → `Prune()`

Both are nearly identical. `PruneOptions` absorbs the filter construction
(string arrays → `GraphFilter.Exclude()` / `PredicateFilter.Exclude()` →
`CompositeFilter.All()`). `HistoryMode` is used directly as a public enum — no
string→enum mapping needed inside the facade.

### Locking Lives in the Facades

`SparqlEngine` and `RdfEngine` acquire and release read locks internally.
Consumers should not need to call `AcquireReadLock()` / `ReleaseReadLock()` for
SPARQL or RDF operations. This eliminates a class of bugs (the locking bug found
in ADR-021 was exactly this pattern).

### LoadExecutor Lifecycle

`LoadExecutor` is currently created and disposed by CLI and MCP. Both
`SparqlEngine.Update()` (for SPARQL LOAD operations) and `RdfEngine` (for file
loading) need this internally. Either create per-call (simple, safe) or hold a
shared instance with disposal tied to some sensible lifecycle. Start with
per-call; optimize only if profiling shows a problem.

### What Not to Change (Phase 3-4)

- **Do not change `QuadStore.AddCurrent()` or batch APIs.** Solid and the
  `RdfEngine` facade use these.
- **Do not rename anything.** Phase 3 changes only visibility (`public` →
  `internal`). No renames, no moves (except `HistoryMode` already moved in
  Phase 1).

## Consequences

### Benefits

- **~140 types become internal** — Mercury's public contract shrinks from ~161 to 21.
- **Eliminates all pipeline duplication** — SPARQL execution (4 copies), RDF format
  switching (2 copies), pruning workflow (2 copies), variable extraction (4 copies
  with a latent hash-mismatch bug between HTTP server and CLI/MCP).
- **Locking correctness by construction** — consumers cannot forget to lock because
  they never acquire locks for SPARQL or RDF operations.
- **Three new InternalsVisibleTo** required for sibling projects (Solid, Pruning,
  TurtleTool) that use internal parsers/writers. CLI, MCP, and SparqlTool are
  pure public-API consumers.
- **Consumer code shrinks dramatically** — CLI, MCP, and SparqlTool each lose
  100-170 lines of boilerplate pipeline code.
- **Porting surface reduced** — cross-language ports need only replicate ~21 types
  plus the DTO contracts, not 160+.

### Drawbacks

- **Materialization cost for CONSTRUCT/DESCRIBE in SparqlTool:** Currently
  SparqlTool streams triples directly to format writers. With the facade, triples
  are first materialized into `QueryResult.Triples`, then serialized. For large
  CONSTRUCT results via CLI, this doubles memory. Acceptable because: (a) the HTTP
  server handles the truly large-scale streaming case internally, (b) CLI workloads
  are bounded by terminal output, (c) the simplification justifies the tradeoff.
  If profiling reveals a problem, a streaming overload can be added later.
- **Three more types plus one DTO** in the public API (`SparqlEngine`, `RdfEngine`,
  `PruneEngine`, `PruneOptions`), plus `HistoryMode` promoted to `Mercury.Abstractions`.
  This is the right tradeoff: five well-designed public types replacing ~140
  accidentally-public types.

### Neutral

- **No performance change for query execution.** The facade calls the same internal
  code. Materialization overhead is negligible for typical result sizes.
- **No behavioral change.** All existing functionality is preserved identically.

## Alternatives Considered

### 1. InternalsVisibleTo With Gradual Internalization

Grant sibling projects access to internals and mechanically change visibility
type-by-type.

**Rejected:** Treats the symptom (too many public types) rather than the cause
(missing API). Consumers would still duplicate the pipeline and still need access
to internal types. The `InternalsVisibleTo` boundary is weaker than a true
public API — it permits rather than prevents coupling to internals.

### 2. Return Streaming Results From Facade

Add `SparqlEngine.QueryStreaming(store, sparql, Action<BindingTable>)` or
return `IEnumerable<T>` for lazy evaluation.

**Rejected for now:** Adds complexity. The only consumer that genuinely needs
streaming (SparqlHttpServer) is already inside Mercury. CLI and MCP materialize
everything anyway. Can be added later if a consumer demonstrates the need.

### 3. Keep Status Quo

Over 140 public types, four duplicated pipelines, works fine.

**Rejected:** "Works" is not "intentional." The duplication has already produced
divergent implementations (three different variable extraction strategies — one
with a latent hash-mismatch bug — two pruning workflows, two format-switch
loaders). Each new consumer would copy all three pipelines again.

## References

- Empirical audit conducted 2026-02-17
- `Mercury.Abstractions/Results.cs` — existing DTOs: `QueryResult`, `UpdateResult`,
  `StoreStatistics`
- `Mercury.Cli/Program.cs` — pipeline duplication #1
- `Mercury.Mcp/MercuryTools.cs` — pipeline duplication #2
- `Mercury.Sparql.Tool/SparqlTool.cs` — pipeline duplication #3
- `Mercury/Sparql/Protocol/SparqlHttpServer.cs` — pipeline duplication #4
  (reference implementation)
- [ADR-021](mercury/ADR-021-hardening-store-contract-query-ergonomics-and-surface-isolation.md) —
  identified locking discipline as a surface concern (§1, §2)
- Sky Omega porting thought experiment (2026-02-17) — motivated “every public type
  is a porting obligation”