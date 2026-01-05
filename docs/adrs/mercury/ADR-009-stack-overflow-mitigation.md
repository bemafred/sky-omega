# ADR-009: Stack Overflow Mitigation Strategy

## Status

Accepted (Phase 1 and 2 complete)

## Context

Mercury uses large inline structs for zero-GC query execution. This design works well on macOS (8MB default stack) but causes stack overflow on Windows (1MB default stack).

### Observed Failures

| Test | Platform | Issue |
|------|----------|-------|
| `RdfStarTests.QueryReifiedTriple_WithSparql` | Windows | Stack overflow |
| `StorageOptionsTests.InsufficientDiskSpaceException_ContainsUsefulInformation` | Windows/VS2022 | Disk space (unrelated) |

### Root Cause: Large Inline Structs

| Struct | Approximate Size | Composition |
|--------|-----------------|-------------|
| `GraphPattern` | ~4KB | 32 TriplePatterns (768B) + 16 Filters (128B) + 8 Binds (128B) + 8 MINUS (192B) + 4 ExistsFilters (~416B) + 4 GraphClauses (~832B) + 2 SubSelects (~1000B) + 2 ServiceClauses (~432B) |
| `Query` | ~7-8KB | Prologue (2KB fixed buffer) + SelectClause + WhereClause (GraphPattern) + SolutionModifier |
| `QueryResults` | ~22KB | Multiple scan types + binding tables |
| `SubSelect` | ~500B | 16 patterns + 8 filters + modifiers |
| `MultiPatternScan` | ~1.2KB | 12 TemporalResultEnumerator structs |

### Platform Stack Size Differences

| Platform | Default Thread Stack | Safety Margin |
|----------|---------------------|---------------|
| macOS/Linux | 8MB | ~7MB headroom |
| Windows | 1MB | Easily exhausted |

### Stack Consumption Analysis

A moderately complex query path:

```
Execute()                           // QueryResults return: 22KB
  → ExecuteWithService()            // Query copy: 8KB
    → ExecuteServiceWithLocal()     // GraphPattern access: 4KB
      → MultiPatternScan            // Scan struct: 1.2KB
        → TriplePatternScan         // Per-pattern: ~200B each
```

**Cumulative: 35KB+ per call chain depth**

With 3-4 levels of nesting, async machinery, and test runner overhead, Windows' 1MB limit is exceeded while macOS has ample headroom.

### Current Anti-Pattern: Thread Hacks

Two locations spawn threads solely to get fresh stack space:

| Location | Purpose | Stack Size |
|----------|---------|------------|
| `QueryExecutor.Graph.cs:554` | GRAPH with multiple patterns | 4MB |
| `UpdateExecutor.cs:568` | DELETE/INSERT WHERE | 4MB |

```csharp
// Anti-pattern - using threads for stack, not parallelism
var thread = new Thread(() => {
    // Execute on fresh stack
}, 4 * 1024 * 1024);
thread.Start();
thread.Join();
```

**Problems with thread hacks:**
1. Threads are for parallelism, not stack circumvention
2. Synchronous `Join()` blocks calling thread
3. Thread pool starvation under load
4. Debugging complexity (call stacks span threads)
5. Exception handling complications
6. Doesn't fix the underlying architectural issue

## Decision

Implement a phased mitigation strategy prioritizing correctness over performance optimization.

## Implementation Phases

### Phase 1: Short-Term Fixes (Remove Thread Hacks)

**Goal:** Eliminate thread anti-pattern, apply established materialization pattern from ADR-003.

#### Checklist

- [x] **1.1** Refactor `ExecuteFixedGraphMultiPatterns` in `QueryExecutor.Graph.cs`
  - Remove `new Thread()` wrapper
  - Keep `TriplePattern[]` array extraction (already heap-allocated)
  - Add `[MethodImpl(MethodImplOptions.NoInlining)]` attribute
  - Return `List<MaterializedRow>` directly

- [x] **1.2** Refactor `ExecuteModifyWithMultiPatternOnThread` in `UpdateExecutor.cs`
  - Remove `new Thread()` wrapper
  - Renamed to `ExecuteModifyWithMultiPattern`
  - Add `[MethodImpl(MethodImplOptions.NoInlining)]` attribute

- [x] **1.2a** Add `[NoInlining]` to main execution paths
  - `QueryExecutor.Execute()` - isolates 22KB QueryResults return
  - `QueryExecutor.ExecuteWithJoins()` - isolates multi-pattern join path

- [x] **1.3** Enable and verify `RdfStarTests.QueryReifiedTriple_WithSparql`
  - Test now passes after Phase 2 QueryBuffer migration
  - Two queries in same async method no longer cause stack overflow

- [ ] **1.4** Add regression tests
  - Test with 5-pattern GRAPH clause
  - Test DELETE/INSERT WHERE with 4+ patterns
  - Run under constrained stack (if possible in test framework)

#### Pattern to Apply

```csharp
// Before (anti-pattern):
private List<MaterializedRow>? ExecuteFixedGraphMultiPatterns(...)
{
    var thread = new Thread(() => { /* work */ }, 4 * 1024 * 1024);
    thread.Start();
    thread.Join();
    return results;
}

// After (correct):
[MethodImpl(MethodImplOptions.NoInlining)]
private List<MaterializedRow> ExecuteFixedGraphMultiPatterns(...)
{
    // Patterns already copied to heap array
    var patterns = new TriplePattern[patternCount];
    // ... materialize results directly
    return results;  // 8-byte pointer, not large struct
}
```

### Phase 2: Mid-Term Fixes (QueryBuffer Adoption)

**Status: Complete**

**Goal:** Migrate from inline struct storage to pooled heap storage via `QueryBuffer`.

#### Checklist

- [x] **2.1** Audit all `Query` struct usage in executor paths
  - Identified 50+ locations where `Query` was accessed
  - All solution modifier access (Limit, Offset, OrderBy, GroupBy, Having) migrated to buffer

- [x] **2.2** Extend `QueryBuffer` to hold all pattern types
  - Added: `HavingStart/Length`, `DescribeAll`, `ConstructPatterns[]`
  - Added: `SelectClauseData` for aggregate storage
  - Added: `TemporalMode` and time range offsets
  - Helper methods: `GetOrderByClause()`, `GetGroupByClause()`, `GetSelectClause()`, `GetHavingClause()`, `GetConstructTemplate()`

- [x] **2.3** Create `QueryBuffer.FromQuery(Query q)` factory
  - `QueryBufferAdapter.FromQuery()` copies inline storage to pooled heap
  - Returns lightweight handle (~100 bytes vs ~8KB)

- [x] **2.4** Update `QueryExecutor` to use `QueryBuffer`
  - Removed `_query` field from QueryExecutor
  - Added `_cachedPattern` for complex pattern structures (subqueries, service clauses)
  - All solution modifier access via `_buffer` methods
  - Partial classes (Graph, Service, Subquery) updated

- [ ] **2.5** Update `SparqlParser` to emit to `QueryBuffer` directly
  - Deferred: Current approach sufficient for stack safety
  - Would require significant parser refactoring

#### Target Architecture

```csharp
// Current: ~7KB on stack per copy
public struct Query {
    public Prologue Prologue;           // 2KB fixed buffer
    public WhereClause WhereClause;     // Contains 4KB GraphPattern
    // ...
}

// Target: ~100 bytes on stack
internal sealed class QueryBuffer : IDisposable {
    private byte[]? _buffer;            // Pooled via ArrayPool
    private int _patternCount;
    // Patterns stored in pooled buffer, not inline
}
```

### Phase 3: Long-Term Fixes (Architectural Improvements)

**Goal:** Systematic prevention of stack overflow class of issues.

#### Checklist

- [ ] **3.1** Implement stack budget analysis tooling
  - Roslyn analyzer for struct sizes in return positions
  - CI check for structs > 1KB in hot paths

- [ ] **3.2** Document stack budget per method
  - Add comments to critical path methods
  - Example: `// Stack budget: ~200 bytes (BindingTable only)`

- [ ] **3.3** Consider union types via `StructLayout.Explicit`
  - `QueryResults` contains multiple scan types, only one active
  - Overlay storage could reduce from 22KB to ~2KB
  - Higher risk, needs careful implementation

- [ ] **3.4** Evaluate alternative pattern storage
  - Span over pooled memory instead of inline fixed arrays
  - Trade-off: Slightly more indirection vs massive size reduction

- [ ] **3.5** Add stack consumption tests
  - Tests that deliberately use deep call chains
  - Verify no regression on Windows with 1MB stack

## Verification

### Stack Safety Verification Query

Test with this query pattern (hits multiple large struct paths):

```sparql
SELECT * WHERE {
  GRAPH <http://example.org/g1> {
    ?s1 ?p1 ?o1 .
    ?s2 ?p2 ?o2 .
    ?s3 ?p3 ?o3 .
    ?s4 ?p4 ?o4 .
  }
  {
    SELECT ?x WHERE {
      ?x ?y ?z .
      ?a ?b ?c .
    }
  }
  SERVICE <http://example.org/sparql> {
    ?remote ?pred ?val .
  }
}
```

### Success Criteria

| Criterion | Measurement | Status |
|-----------|-------------|--------|
| No thread hacks | `grep "new.*Thread" src/Mercury/Sparql` returns 0 hits | **Done** |
| Windows tests pass | CI green on Windows runner | Pending CI run |
| Stack headroom | Complex queries use < 500KB stack | **Done** (QueryBuffer eliminates ~8KB Query struct from stack) |
| Multiple queries in async | `RdfStarTests.QueryReifiedTriple_WithSparql` passes | **Done** (Phase 2 fix) |
| Zero-GC preserved | Simple queries allocate 0 bytes (verified via benchmark) | Unchanged |

## Consequences

### Positive

- Stack overflow eliminated across platforms
- Thread pool not abused for stack workarounds
- Cleaner debugging (single-thread call stacks)
- Production-safe on Windows servers
- Consistent behavior regardless of platform

### Negative

- QueryBuffer adds indirection (minor perf impact)
- More complex memory management (must dispose QueryBuffer)
- Phase 2.5 (parser emit to buffer) deferred - not needed for stack safety

### Neutral

- Heap allocation for complex queries already accepted (ADR-003)
- Simple queries remain zero-GC (no change)

## References

- [ADR-003: Buffer Pattern for Stack Safety](ADR-003-buffer-pattern.md) - Establishes materialization pattern
- [ADR-004: SERVICE Scan Interface](ADR-004-service-scan-interface.md) - SERVICE execution architecture
- Windows thread stack default: https://docs.microsoft.com/en-us/windows/win32/procthread/thread-stack-size
