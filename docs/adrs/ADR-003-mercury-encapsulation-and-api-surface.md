# ADR-003: Mercury Encapsulation — Facade Over Internals

## Status

Proposed (2026-02-17)

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

The public surface of Mercury reduces to approximately **15 types**:

| Area | Public Types |
|---|---|
| **Facades** | `SparqlEngine`, `RdfEngine`, `PruneEngine` |
| **Facade DTOs** | `PruneOptions` |
| **Storage** | `QuadStore`, `QuadStorePool` |
| **Protocol** | `SparqlHttpServer`, `SparqlHttpServerOptions` |
| **Diagnostics** | `ILogger`, `NullLogger`, `LogLevel` |

Plus the DTOs already in `Mercury.Abstractions`: `QueryResult`, `UpdateResult`,
`StoreStatistics`, `PruneResult`, `ExecutionResultKind`, `RdfFormat`,
`HistoryMode`, `ByteFormatter`, etc.

### No New InternalsVisibleTo Required

Because the facade provides everything consumers need through public DTOs, **no
consumer project needs access to Mercury internals**. The CLI, MCP, SparqlTool,
and TurtleTool all become pure consumers of the public API.

Mercury already grants `InternalsVisibleTo` to `Mercury.Tests` and
`Mercury.Benchmarks` (declared in `Mercury.csproj`). This is correct and stays —
the test project extensively white-box tests internal components (`AtomStore`,
`QuadIndex`, `PageCache`, `WriteAheadLog`, `PatternSlot`, `FilterEvaluator`,
`QueryExecutor`, etc.). The types newly internalized by this ADR (`SparqlParser`,
`QueryExecutor`, `SparqlExplainer`, the AST types, etc.) automatically become
testable by `Mercury.Tests` through this existing declaration.

`Mercury.Solid` similarly grants `InternalsVisibleTo` to `Mercury.Solid.Tests`.

No additional `InternalsVisibleTo` declarations are introduced by this ADR.

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

### SparqlHttpServer Stays Internal and Streaming

`SparqlHttpServer` is the only consumer that genuinely needs streaming access to
query results (for large HTTP responses) and direct access to format writers (for
RDF serialization in HTTP responses). It is already inside Mercury and already
uses internal types. The facades do not affect it — it continues to use
`QueryExecutor`, `QueryResults`, and format writers directly, as an internal
consumer.

## Implementation

### Phase 1: Create Facades

Create three public static classes inside the Mercury project:

**`SparqlEngine`** (`src/Mercury/SparqlEngine.cs`):
Implement by extracting and consolidating the duplicated pipeline logic from CLI,
MCP, SparqlTool, and SparqlHttpServer. The variable name extraction should use the
AST-based approach from `SparqlHttpServer` (the most correct implementation) with
hash-scan fallback for `SELECT *`. Include `LoadExecutor` lifecycle management
internally.

**`RdfEngine`** (`src/Mercury/RdfEngine.cs`):
Extract the format-switch logic from `SparqlTool.LoadRdfAsync`. The write methods
consolidate the format-switch logic from `SparqlTool`‘s CONSTRUCT/DESCRIBE output
paths. `ParseTriplesAsync` covers Solid’s pattern of parsing to a triple list
(with optional `baseUri`) without direct store insertion.

**`PruneEngine`** (`src/Mercury/PruneEngine.cs`):
Extract the filter construction and clear→transfer→switch→clear workflow from CLI
and MCP. `PruneOptions` uses the `HistoryMode` enum directly (moved to
`Mercury.Abstractions`) and maps string arrays to the internal filter types.

**Verification:** Add tests that call all three facades directly, confirming
results match existing behavior. Run W3C conformance suite.

### Phase 2: Migrate Consumers

Rewrite consumer code to use the facades:

| Consumer | Current lines (all pipelines) | Expected lines |
|---|---:|---:|
| CLI | ~200 | ~20 |
| MCP | ~200 | ~40 |
| SparqlTool | ~170 | ~40 |

Remove all duplicated implementations: `ExtractVariableNames` (4 copies),
format-switch loading (2 copies), pruning workflow (2 copies).

Solid migrates from direct parser/writer usage to `RdfEngine.ParseTriplesAsync`
and `RdfEngine.WriteTriples`.

**Verification:** All CLI, MCP, SparqlTool, and Solid functionality works
identically. Integration tests pass.

### Phase 3: Internalize

Change all types listed in “What Becomes Internal” from `public` to `internal`.
This includes the SPARQL internals, all 12 RDF format parser/writer types,
`RdfFormatNegotiator`, and the 10 pruning types (with `HistoryMode` already moved
to `Mercury.Abstractions`). Single-pass mechanical change.

**Verification:** `dotnet build` for entire solution. If any compilation error
occurs, the failing type was missed in the analysis — either add it to a facade
or keep it public and document why. Run full test suite.

### Phase 4: Audit and Tighten QuadStore Surface

With SPARQL, RDF I/O, and pruning internals hidden, audit the remaining `QuadStore`
public API. Methods like `AcquireReadLock()` / `ReleaseReadLock()` are currently
needed by consumers for query enumeration — but with the facades handling locking,
evaluate whether these can become internal.

`AddCurrent()`, `AddCurrentBatched()`, `BeginBatch()` / `CommitBatch()` are used
by Solid directly for Linked Data Platform operations (not through RDF files).
These likely remain public. `QueryCurrent()` is used by Solid for graph
enumeration — also remains public.

The enumerator types returned by `GetNamedGraphs()` and `QueryCurrent()` can be
evaluated for internalization (duck-typed foreach may allow internal enumerators).

**Verification:** Solid and Pruning continue to compile and function. All tests
pass.

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

### What Not to Change

- **Do not modify `SparqlHttpServer`’s internal implementation.** It streams
  results and should continue to use `QueryExecutor` directly.
- **Do not change `QuadStore.AddCurrent()` or batch APIs.** Solid and the
  `RdfEngine` facade use these.
- **Do not rename anything.** This ADR changes visibility, adds three facade
  classes plus `PruneOptions`, and moves `HistoryMode` to `Mercury.Abstractions`.

## Consequences

### Benefits

- **~130 types become internal** — Mercury's public contract shrinks from over 140 to ~15.
- **Eliminates all pipeline duplication** — SPARQL execution (4 copies), RDF format
  switching (2 copies), pruning workflow (2 copies), variable extraction (4 copies
  with a latent hash-mismatch bug between HTTP server and CLI/MCP).
- **Locking correctness by construction** — consumers cannot forget to lock because
  they never acquire locks for SPARQL or RDF operations.
- **No new InternalsVisibleTo needed** — consumer projects use only the public API.
  Existing test access (`Mercury.Tests`, `Mercury.Benchmarks`) stays as-is.
- **Consumer code shrinks dramatically** — CLI, MCP, and SparqlTool each lose
  100-170 lines of boilerplate pipeline code.
- **Porting surface reduced** — cross-language ports need only replicate ~15 types
  plus the DTO contracts, not 140+.

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
  This is the right tradeoff: five well-designed public types replacing ~130
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