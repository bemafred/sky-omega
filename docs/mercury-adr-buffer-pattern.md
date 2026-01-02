# ADR: Buffer Pattern for Stack Safety

## Status

Accepted

## Context

Mercury uses large ref structs for zero-GC query execution:

- `QueryResults` ~22KB (contains multiple scan types, binding tables)
- `GraphPattern` ~4KB (inline storage for 32 patterns, filters)
- `MultiPatternScan` ~1.2KB (12 TemporalResultEnumerator structs)

These ref structs are designed for stack allocation to avoid GC pressure. However, complex query paths create deep call chains where nested return values exceed the 1MB default stack limit:

```
Execute()
  → allocates 22KB for QueryResults return value
  → ExecuteWithService()
      → allocates local variables
      → Internal methods create scan structs
```

This causes stack overflow in SERVICE+local pattern joins, deeply nested subqueries, and queries combining GROUP BY + ORDER BY + DISTINCT.

## Options Considered

### Option 1: Make ref structs classes

Convert QueryResults to a class for heap allocation.

**Drawbacks:**
- Breaks zero-GC design for all queries, not just complex ones
- Every query would allocate on heap
- Violates the core performance principle

### Option 2: Union structs with StructLayout

Use `[StructLayout(LayoutKind.Explicit)]` to overlay scan types since only one is active at a time.

**Drawbacks:**
- Complex unsafe code
- Harder to maintain
- Potential for subtle bugs with overlapping memory

### Option 3: QueryBuffer + Materialization pattern

Keep ref structs for simple queries (zero-GC). For complex query paths, materialize results to `List<MaterializedRow>` early, returning only the heap pointer through the call chain.

**Benefits:**
- Zero-GC preserved for simple queries (majority of use cases)
- Heap allocation only when necessary
- Well-understood pattern (already used for ORDER BY, GROUP BY)
- Clean separation: simple = stack, complex = heap

## Decision

**Option 3: QueryBuffer + Materialization pattern**

## Implementation

The pattern has three main components:

### 1. IBufferManager Interface

Unified buffer allocation via `ArrayPool<T>`:

```csharp
public interface IBufferManager
{
    BufferLease<T> Rent<T>(int minimumLength) where T : unmanaged;
    void Return<T>(T[]? buffer, bool clearArray = false) where T : unmanaged;
}
```

Default: `PooledBufferManager.Shared` singleton.

### 2. BufferLease<T> - RAII Wrapper

```csharp
public ref struct BufferLease<T> where T : unmanaged
{
    private readonly IBufferManager? _manager;
    private T[]? _array;

    public Span<T> Span => _array.AsSpan();

    public void Dispose() => _manager?.Return(_array);
}
```

Stack-only lifetime, automatic return via Dispose pattern.

### 3. QueryBuffer - Heap-Based Pattern Storage

Replaces large inline `Query` struct (~9KB) with lightweight wrapper (~100 bytes):

```csharp
internal sealed class QueryBuffer : IDisposable
{
    private byte[]? _buffer;  // Pooled via ArrayPool
    private int _patternCount;
}
```

### 4. Materialization Pattern

For complex query paths, materialize results early:

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
private List<MaterializedRow>? ExecuteWithService()
{
    var results = new List<MaterializedRow>();
    // Execute and populate results
    return results;  // 8-byte pointer, not 22KB struct
}
```

The `[MethodImpl(NoInlining)]` attribute prevents the compiler from merging stack frames, keeping each method's stack allocation isolated.

## Application

| Query Path | Strategy |
|------------|----------|
| Simple SELECT | Zero-GC ref structs |
| ORDER BY | Materialize to List, sort |
| GROUP BY | Materialize to grouped rows |
| SERVICE clauses | Materialize results |
| SERVICE + local joins | Materialize both, join on heap |
| Nested subqueries | Materialize inner results |

## Consequences

- Zero-GC preserved for simple queries (vast majority)
- Complex queries allocate on heap via pooled buffers
- Stack overflow eliminated for all query types
- Clear architectural boundary: stack vs heap by query complexity
- Consistent pattern across all complex query paths

## Heap Allocation Analysis

### What Allocates on Heap

Query execution has several unavoidable heap allocations:

| Category | Types | When |
|----------|-------|------|
| Result materialization | `MaterializedRow`, `List<MaterializedRow>` | ORDER BY, GROUP BY, SERVICE, subqueries |
| Deduplication | `HashSet<int>` | DISTINCT modifier |
| Aggregation | `Dictionary<string, GroupedRow>` | GROUP BY with aggregates |
| String functions | String results | CONCAT, BNODE, UUID, UCASE, LCASE, hashes |
| Property paths | `HashSet<string>`, `Queue<string>` | Transitive path traversal |
| Regex filters | `Regex` objects | REGEX(), REPLACE() functions |

### Why This Is Acceptable

**1. Relative Scale**

The heap allocations are negligible compared to MMF-backed storage:

| Component | Size | Location |
|-----------|------|----------|
| QuadStore (10M triples) | 1-2 GB | Memory-mapped files |
| AtomStore | 100s MB | Memory-mapped files |
| TrigramIndex | 10s MB | Memory-mapped files |
| Query results (10K rows) | ~2 MB | Heap (Gen0) |
| DISTINCT HashSet | ~40 KB | Heap (Gen0) |

Query allocations are typically <0.1% of data footprint.

**2. Allocation Characteristics**

- **Small objects**: MaterializedRow ~100-200 bytes each
- **Short-lived**: Created during query, discarded after results consumed
- **Gen0 collection**: Sub-millisecond cleanup
- **Bounded**: Proportional to result set size, not data size

**3. Critical Paths Are Zero-GC**

The truly hot inner loops remain allocation-free:

- `QuadIndex` B+Tree traversal (ref struct enumerators)
- `AtomStore` string lookup (memory-mapped, no copies)
- `SparqlParser` query parsing (ref struct, span-based)
- `BindingTable` variable binding (caller-provided storage)

Heap allocations occur at result materialization boundaries, not per-index-lookup.

### GC Strategy

Force GC at natural pause points when latency isn't critical:

```csharp
// After query completion
GC.Collect(0, GCCollectionMode.Optimized, false);

// During idle periods or after batch operations
GC.Collect(1, GCCollectionMode.Optimized, true);
```

Recommended trigger points:
- After `QueryResults.Dispose()` for large result sets
- After batch SPARQL UPDATE completion
- After HTTP response sent (in SparqlHttpServer)
- During checkpoint operations (already a pause point)

### Conclusion

The materialization pattern introduces controlled heap allocation that:
- Solves stack overflow for complex queries
- Has negligible memory impact vs MMF-backed data
- Uses short-lived Gen0 objects with fast collection
- Preserves zero-GC guarantees for critical index operations

No further optimization needed. The Regex caching for repeated FILTER patterns would be a minor improvement but is not critical.
