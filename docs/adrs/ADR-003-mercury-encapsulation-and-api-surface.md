# ADR-003: Mercury Encapsulation — Facade Over Internals

## Status

Proposed (2026-02-17)

## Context

An empirical audit of Mercury’s type visibility reveals that **165 types are public**
but only **~32 are referenced by any consumer project**. More importantly, the audit
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

|Consumer        |Method                           |Strategy             |
|-—————|———————————|———————|
|CLI             |`ExtractVariableNames`           |Hash-scan source text|
|MCP             |`ExtractVariableNames`           |Hash-scan source text|
|SparqlTool      |`GetSelectVariables`             |Read AST SelectClause|
|SparqlHttpServer|`ExtractVariableNames` + fallback|AST + hash-scan      |

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
    public static QueryResult Query(QuadStore store, string sparql);

    /// <summary>
    /// Execute a SPARQL UPDATE (INSERT, DELETE, LOAD, CLEAR, DROP, etc.).
    /// Handles parsing, execution, and LOAD operations internally.
    /// </summary>
    public static UpdateResult Update(QuadStore store, string sparql);

    /// <summary>
    /// Generate an execution plan explanation for a SPARQL query.
    /// </summary>
    public static string Explain(string sparql);

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

### What Remains Public

The public surface of Mercury reduces to approximately **25 types**:

|Area           |Public Types                                                                                                                                                                                                                                    |
|—————|————————————————————————————————————————————————————————————————————————————————|
|**Facade**     |`SparqlEngine`                                                                                                                                                                                                                                  |
|**Storage**    |`QuadStore`, `QuadStorePool`                                                                                                                                                                                                                    |
|**Format I/O** |`TurtleStreamParser`, `TurtleStreamWriter`, `TriGStreamParser`, `TriGStreamWriter`, `NQuadsStreamParser`, `NQuadsStreamWriter`, `NTriplesStreamParser`, `NTriplesStreamWriter`, `RdfXmlStreamParser`, `RdfXmlStreamWriter`, `JsonLdStreamParser`|
|**Protocol**   |`SparqlHttpServer`, `SparqlHttpServerOptions`                                                                                                                                                                                                   |
|**Rdf**        |`RdfFormatNegotiator`                                                                                                                                                                                                                           |
|**Diagnostics**|`ILogger`, `NullLogger`, `LogLevel`                                                                                                                                                                                                             |

Plus the DTOs already in `Mercury.Abstractions`: `QueryResult`, `UpdateResult`,
`StoreStatistics`, `PruneResult`, `ExecutionResultKind`, `RdfFormat`,
`ByteFormatter`, etc.

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
serializes them through the format writers it already references.

### SparqlHttpServer Stays Internal and Streaming

`SparqlHttpServer` is the only consumer that genuinely needs streaming access to
query results (for large HTTP responses). It is already inside Mercury and already
uses internal types. The facade does not affect it — it continues to use
`QueryExecutor` and `QueryResults` directly, as an internal consumer.

## Implementation

### Phase 1: Create Facade

Create `SparqlEngine` inside the Mercury project (`src/Mercury/SparqlEngine.cs`).
Implement by extracting and consolidating the duplicated pipeline logic. The
variable name extraction should use the AST-based approach from `SparqlHttpServer`
(the most correct implementation) with hash-scan fallback for `SELECT *`.

Include `LoadExecutor` lifecycle management internally — create and dispose per
update call, or maintain a shared instance within the engine.

**Verification:** Add tests that call `SparqlEngine.Query()` and
`SparqlEngine.Update()` directly, confirming results match existing behavior.
Run W3C conformance suite.

### Phase 2: Migrate Consumers

Rewrite consumer code to use the facade:

|Consumer  |Current lines (pipeline)|Expected lines|
|-———|————————:|-————:|
|CLI       |~120                    |~10           |
|MCP       |~130                    |~30           |
|SparqlTool|~100                    |~30           |

Remove the duplicated `ExtractVariableNames` implementations from all consumers.
`SparqlTool` keeps its format serialization logic (JSON/CSV/TSV/XML output) but
receives pre-materialized data from the DTO instead of iterating `QueryResults`.

**Verification:** All CLI, MCP, and SparqlTool functionality works identically.
Integration tests pass.

### Phase 3: Internalize

Change all types listed in “What Becomes Internal” from `public` to `internal`.
This is a single-pass mechanical change.

**Verification:** `dotnet build` for entire solution. If any compilation error
occurs, the failing type was missed in the analysis — either add it to the facade
or keep it public and document why. Run full test suite.

### Phase 4: Audit and Tighten QuadStore Surface

With SPARQL internals hidden, audit the remaining `QuadStore` public API.
Methods like `AcquireReadLock()` / `ReleaseReadLock()` are currently needed by
consumers for query enumeration — but with the facade handling locking, evaluate
whether these can become internal. `AddCurrent()`, `AddCurrentBatched()`,
`BeginBatch()` / `CommitBatch()` are used by Solid and format loading — these
likely remain public.

`GetNamedGraphs()` and `QueryCurrent()` are used by Solid directly (not through
SPARQL). These remain public but the enumerator types they return can be evaluated
for internalization (duck-typed foreach may allow internal enumerators).

**Verification:** Solid and Pruning continue to compile and function. All tests
pass.

## Implementation Notes for Claude Code

### Phase Ordering Is Strict

Phase 1 must be complete and verified before Phase 2. Phase 3 must wait until
Phase 2 is stable. Do not combine phases.

### The Facade Implementation Is Consolidation, Not New Logic

`SparqlEngine.Query()` should consolidate logic from these four existing
implementations:

1. `Mercury.Cli/Program.cs` → `ExecuteQuery()`
1. `Mercury.Mcp/MercuryTools.cs` → `Query()`
1. `Mercury.Sparql.Tool/SparqlTool.cs` → `ExecuteQuery()`
1. `Mercury/Sparql/Protocol/SparqlHttpServer.cs` → `ExecuteQuerySync()`

The HTTP server version is the most complete (handles aggregates in variable
extraction). Use it as the reference implementation. The DTO population matches
what CLI and MCP already do.

### Locking Lives in the Facade

The facade acquires and releases read locks. Consumers should not need to call
`AcquireReadLock()` / `ReleaseReadLock()` for SPARQL operations. This eliminates
a class of bugs (the locking bug found in ADR-021 was exactly this pattern).

### LoadExecutor Lifecycle

`LoadExecutor` is currently created and disposed by CLI and MCP. The facade
should manage this internally. Either create per-call (simple, safe) or hold a
shared instance with disposal tied to some sensible lifecycle. Start with
per-call; optimize only if profiling shows a problem.

### What Not to Change

- **Do not modify `SparqlHttpServer`’s internal implementation.** It streams
  results and should continue to use `QueryExecutor` directly.
- **Do not change `QuadStore.AddCurrent()` or batch APIs.** Solid and format
  loaders use these directly.
- **Do not touch format parsers/writers** (`TurtleStreamParser`, etc.).
  These remain public — they serve a different purpose (RDF I/O) from the
  SPARQL execution pipeline.
- **Do not rename anything.** This ADR changes visibility and adds one class.

## Consequences

### Benefits

- **~140 types become internal** — Mercury’s public contract shrinks from 165 to ~25.
- **Eliminates four-way pipeline duplication** — one implementation, centrally tested.
- **Eliminates four-way variable extraction duplication** — the most error-prone
  piece of consumer code disappears.
- **Locking correctness by construction** — consumers cannot forget to lock because
  they never acquire locks for SPARQL operations.
- **No new InternalsVisibleTo needed** — consumer projects use only the public API.
  Existing test access (`Mercury.Tests`, `Mercury.Benchmarks`) stays as-is.
- **Consumer code shrinks dramatically** — CLI, MCP, and SparqlTool each lose
  50-100 lines of boilerplate pipeline code.
- **Porting surface reduced** — cross-language ports need only replicate ~25 types
  plus the DTO contracts, not 165.

### Drawbacks

- **Materialization cost for CONSTRUCT/DESCRIBE in SparqlTool:** Currently
  SparqlTool streams triples directly to format writers. With the facade, triples
  are first materialized into `QueryResult.Triples`, then serialized. For large
  CONSTRUCT results via CLI, this doubles memory. Acceptable because: (a) the HTTP
  server handles the truly large-scale streaming case internally, (b) CLI workloads
  are bounded by terminal output, (c) the simplification justifies the tradeoff.
  If profiling reveals a problem, a streaming overload can be added later.
- **One more type (`SparqlEngine`)** in the public API. This is the right tradeoff:
  one well-designed entry point replacing ~32 accidentally-public types.

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

165 public types, four duplicated pipelines, works fine.

**Rejected:** “Works” is not “intentional.” The duplication has already produced
divergent implementations (three different variable extraction strategies). Each
new consumer (a future REST API, a gRPC surface, a WASM interface) would copy
the pipeline a fifth time.

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