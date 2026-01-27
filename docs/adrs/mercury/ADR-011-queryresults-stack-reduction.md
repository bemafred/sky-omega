# ADR-011: QueryResults Stack Reduction via Pooled Enumerators

## Status

**Accepted** - Implementation complete (2026-01-26)

### Results Summary

Stack overflow issue resolved. All W3C conformance tests now pass (1896/1896).

**Final measurements** (compared to baseline):

| Struct | Baseline | Final | Reduction |
|--------|----------|-------|-----------|
| QueryResults | 89,640 bytes | **6,128 bytes** | **93%** |
| MultiPatternScan | 18,080 bytes | **384 bytes** | **98%** |
| DefaultGraphUnionScan | 33,456 bytes | **1,040 bytes** | **97%** |
| CrossGraphMultiPatternScan | 15,800 bytes | **96 bytes** | **99%** |
| SubQueryScan | 1,976 bytes | 1,976 bytes | unchanged |
| TriplePatternScan | 608 bytes | 608 bytes | unchanged |

### Implementation Details (2026-01-26)

**Phase 1: Enumerator Struct Change + Pooling**
- Changed `TemporalQuadEnumerator` and `TemporalResultEnumerator` from `ref struct` to `struct`
- Replaced 12 inline enumerator fields in `MultiPatternScan` with `ArrayPool<TemporalResultEnumerator>.Shared.Rent(12)`
- Replaced 4 inline enumerator fields in `CrossGraphMultiPatternScan` with pooled array

**Phase 2: GraphPattern Boxing**
- Modified `MultiPatternScan` to always use `BoxedPattern` (heap reference) instead of inline storage
- Applied same pattern to `DefaultGraphUnionScan` and `CrossGraphMultiPatternScan`
- Each scan now allocates ~4KB on heap instead of stack

**Key insight**: The original enumerators were `ref struct` only for lifetime enforcement, not because they contained `Span<T>` fields. Changing to `struct` enabled pooled array storage without functional changes.

### Original Investigation Findings

**Why StructLayout.Explicit approach was abandoned**:
1. QueryResults contains `ReadOnlySpan<char>` fields which cannot have `[FieldOffset]` attributes
2. However, `TemporalQuadEnumerator` and `TemporalResultEnumerator` do NOT contain Span fields
3. These could be changed from `ref struct` to `struct`, enabling pooled array storage

See [ADR-011-implementation-plan.md](ADR-011-implementation-plan.md) for detailed investigation notes.

## Context

`QueryResults` is a ~22KB ref struct that causes stack overflow when running large test suites or complex query paths. This ADR addresses Phase 3.3 from ADR-009 (union types via `StructLayout.Explicit`) and builds on the buffer patterns established in ADR-002 and ADR-003.

### Problem: QueryResults Embeds All Scan Types

`QueryResults` embeds 7 scan types even though only one is ever active:

```csharp
public ref partial struct QueryResults
{
    private TriplePatternScan _singleScan;           // ~500 bytes
    private MultiPatternScan _multiScan;             // ~7.2KB (12 enumerators + GraphPattern)
    private SubQueryScan _subQueryScan;              // ~200 bytes
    private DefaultGraphUnionScan _defaultGraphUnionScan;  // ~8KB (embeds TriplePatternScan + MultiPatternScan)
    private CrossGraphMultiPatternScan _crossGraphScan;    // ~1.5KB (4 enumerators)
    private TriplePatternScan _unionSingleScan;      // ~500 bytes (UNION branch)
    private MultiPatternScan _unionMultiScan;        // ~7.2KB (UNION branch)
    // Plus ~1KB for other fields (bindings, modifiers, state)
}
```

**Total: ~22KB**, regardless of which scan type is actually used.

### Embedded Type Size Analysis

| Type | Size | Composition |
|------|------|-------------|
| `TemporalKey` | 56 bytes | 7 × `long` (Graph, S, P, O, ValidFrom, ValidTo, TransactionTime) |
| `TemporalQuadEnumerator` | ~220 bytes | QuadIndex ref + 3 × TemporalKey + state |
| `TemporalResultEnumerator` | ~250 bytes | TemporalQuadEnumerator + AtomStore ref + char[] buffer |
| `TriplePatternScan` | ~500 bytes | TemporalResultEnumerator + pattern + path traversal state |
| `MultiPatternScan` | ~7.2KB | 12 × TemporalResultEnumerator + GraphPattern (~4KB) + state |
| `DefaultGraphUnionScan` | ~8KB | GraphPattern + TriplePatternScan + MultiPatternScan |
| `CrossGraphMultiPatternScan` | ~1.5KB | 4 × TemporalResultEnumerator + GraphPattern ref + state |
| `SubQueryScan` | ~200 bytes | Refs + SubSelect (~500B) + List ref |

### Why This Matters

1. **Cumulative stack pressure**: Each `QueryResults` return allocates ~22KB on caller's stack
2. **Async test overhead**: xUnit async machinery adds ~2-4KB per test method
3. **1MB Windows limit**: After ~40-50 tests, stack is exhausted
4. **W3C test suite**: 1600+ tests cause consistent stack overflow

### Current Workarounds (Insufficient)

| Workaround | Applied In | Limitation |
|------------|------------|------------|
| `[NoInlining]` | Execute(), ExecuteGraphClauses | Isolates frames but doesn't reduce size |
| Return `List<MaterializedRow>?` | ExecuteGraphClausesToList | Only helps GRAPH path |
| `FromMaterializedList()` | Multiple paths | Struct size unchanged (space reserved) |
| Thread with 4MB stack | Removed in ADR-009 | Anti-pattern |

## Decision

Implement a **discriminated union pattern** using `[StructLayout(LayoutKind.Explicit)]` to overlay scan types at the same memory offset. Only one scan type is active at a time, so they can safely share storage.

### Alternative Options Considered

#### Option A: Boxed Scan Holder

Store active scan in a heap-allocated wrapper class.

```csharp
internal interface IScanHolder : IDisposable
{
    bool MoveNext(ref BindingTable bindings);
}

internal sealed class SingleScanHolder : IScanHolder { ... }
internal sealed class MultiScanHolder : IScanHolder { ... }
```

**Pros:**
- Simple to implement
- Clear ownership semantics

**Cons:**
- Heap allocation for every query (violates zero-GC for simple queries)
- Virtual dispatch overhead on hot path (MoveNext)
- Breaks current ref struct design

#### Option B: Split Result Types

Create specialized `QueryResults` variants:

```csharp
internal ref struct SinglePatternResults { ... }      // ~600 bytes
internal ref struct MultiPatternResults { ... }       // ~8KB
internal ref struct MaterializedResults { ... }       // ~200 bytes
```

**Pros:**
- Each type only contains what it needs
- No unsafe code

**Cons:**
- Major API change (Execute() return type varies)
- Duplication of iteration logic
- Complex caller code to handle variants

#### Option C: Discriminated Union (Selected)

Use explicit struct layout to overlay scan types:

```csharp
[StructLayout(LayoutKind.Explicit)]
public ref partial struct QueryResults
{
    // Discriminator at offset 0
    [FieldOffset(0)] private ScanType _activeScanType;

    // All scans overlaid at same offset
    [FieldOffset(8)] private TriplePatternScan _singleScan;
    [FieldOffset(8)] private MultiPatternScan _multiScan;
    [FieldOffset(8)] private SubQueryScan _subQueryScan;
    // ... other scans at offset 8

    // Non-overlapping fields after scan union
    [FieldOffset(ScanUnionSize + 8)] private BindingTable _bindingTable;
    // ... other fields
}
```

**Pros:**
- Reduces size from ~22KB to ~8.5KB (largest scan + overhead)
- No heap allocation (preserves zero-GC)
- No virtual dispatch
- Single return type (API unchanged)

**Cons:**
- Requires unsafe code
- Complex field offset calculations
- Must ensure only active scan is accessed

## Implementation

### Phase 1: Preparation (Pre-Refactor)

**Goal:** Establish baseline and ensure test stability.

- [ ] **1.1** Fix remaining W3C test failures (functional issues, not stack)
- [ ] **1.2** Document all `QueryResults` construction sites
- [ ] **1.3** Create benchmark for QueryResults creation/iteration
- [ ] **1.4** Add stack consumption test (verify current ~22KB)

### Phase 2: Scan Union Implementation

**Goal:** Reduce QueryResults to ~8.5KB via discriminated union.

#### 2.1 Define Scan Type Discriminator

```csharp
internal enum ScanType : byte
{
    None = 0,
    Single = 1,           // TriplePatternScan
    Multi = 2,            // MultiPatternScan
    SubQuery = 3,         // SubQueryScan
    DefaultGraphUnion = 4,// DefaultGraphUnionScan
    CrossGraph = 5,       // CrossGraphMultiPatternScan
    UnionSingle = 6,      // UNION branch TriplePatternScan
    UnionMulti = 7,       // UNION branch MultiPatternScan
    Materialized = 8      // Pre-collected List<MaterializedRow>
}
```

#### 2.2 Calculate Field Offsets

The scan union must be sized to fit the largest scan type:

| Scan Type | Size | Alignment |
|-----------|------|-----------|
| TriplePatternScan | ~500 bytes | 8 |
| MultiPatternScan | ~7,200 bytes | 8 |
| SubQueryScan | ~200 bytes | 8 |
| DefaultGraphUnionScan | ~8,000 bytes | 8 |
| CrossGraphMultiPatternScan | ~1,500 bytes | 8 |

**Union size: 8,000 bytes** (DefaultGraphUnionScan is largest)

```csharp
private const int ScanUnionOffset = 8;          // After discriminator (aligned)
private const int ScanUnionSize = 8000;         // Largest scan type
private const int PostUnionOffset = 8008;       // ScanUnionOffset + ScanUnionSize
```

#### 2.3 Restructured QueryResults Layout

```csharp
[StructLayout(LayoutKind.Explicit, Size = QueryResultsSize)]
public ref partial struct QueryResults
{
    private const int QueryResultsSize = 8500;  // Calculated based on all fields

    // === Discriminator (offset 0) ===
    [FieldOffset(0)] private ScanType _activeScanType;

    // === Scan Union (offset 8, size 8000) ===
    // Only ONE of these is valid based on _activeScanType
    [FieldOffset(ScanUnionOffset)] private TriplePatternScan _singleScan;
    [FieldOffset(ScanUnionOffset)] private MultiPatternScan _multiScan;
    [FieldOffset(ScanUnionOffset)] private SubQueryScan _subQueryScan;
    [FieldOffset(ScanUnionOffset)] private DefaultGraphUnionScan _defaultGraphUnionScan;
    [FieldOffset(ScanUnionOffset)] private CrossGraphMultiPatternScan _crossGraphScan;

    // === Non-Overlapping Fields (offset 8008+) ===
    [FieldOffset(PostUnionOffset)] private Patterns.QueryBuffer? _buffer;
    [FieldOffset(PostUnionOffset + 8)] private BindingTable _bindingTable;
    // ... remaining fields with calculated offsets

    // === UNION Branch Storage ===
    // UNION branch scans stored AFTER main scan, not overlaid
    // This allows UNION iteration to switch between branches
    [FieldOffset(PostUnionOffset + CommonFieldsSize)] private ScanType _unionScanType;
    [FieldOffset(PostUnionOffset + CommonFieldsSize + 8)] private TriplePatternScan _unionSingleScan;
    [FieldOffset(PostUnionOffset + CommonFieldsSize + 8)] private MultiPatternScan _unionMultiScan;
}
```

**Note:** UNION branch scans cannot be in the main union because both branches may be needed during iteration (first branch exhausted → switch to second branch).

#### 2.4 Safe Access Pattern

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private bool MoveNextScan(ref BindingTable bindings)
{
    return _activeScanType switch
    {
        ScanType.Single => _singleScan.MoveNext(ref bindings),
        ScanType.Multi => _multiScan.MoveNext(ref bindings),
        ScanType.SubQuery => _subQueryScan.MoveNext(ref bindings),
        ScanType.DefaultGraphUnion => _defaultGraphUnionScan.MoveNext(ref bindings),
        ScanType.CrossGraph => _crossGraphScan.MoveNext(ref bindings),
        ScanType.Materialized => MoveNextMaterialized(ref bindings),
        _ => false
    };
}

public void Dispose()
{
    switch (_activeScanType)
    {
        case ScanType.Single: _singleScan.Dispose(); break;
        case ScanType.Multi: _multiScan.Dispose(); break;
        case ScanType.SubQuery: _subQueryScan.Dispose(); break;
        case ScanType.DefaultGraphUnion: _defaultGraphUnionScan.Dispose(); break;
        case ScanType.CrossGraph: _crossGraphScan.Dispose(); break;
    }

    // Also dispose UNION branch if active
    switch (_unionScanType)
    {
        case ScanType.UnionSingle: _unionSingleScan.Dispose(); break;
        case ScanType.UnionMulti: _unionMultiScan.Dispose(); break;
    }

    _activeScanType = ScanType.None;
    _unionScanType = ScanType.None;
}
```

### Phase 3: Further Reduction via Pooled Scans

**Goal:** Reduce MultiPatternScan from ~7.2KB to ~200 bytes by pooling enumerators.

#### 3.1 Problem: MultiPatternScan Embeds 12 Enumerators

```csharp
internal ref struct MultiPatternScan
{
    // 12 × ~250 bytes = 3,000 bytes just for enumerators
    private TemporalResultEnumerator _enum0;
    private TemporalResultEnumerator _enum1;
    // ... _enum2 through _enum11

    // Plus GraphPattern (~4KB)
    private readonly GraphPattern _pattern;
}
```

#### 3.2 Solution: Pool Enumerator Storage

```csharp
internal ref struct MultiPatternScan
{
    // Enumerator storage rented from pool
    private TemporalResultEnumerator[]? _enumerators;  // 8 bytes (reference)
    private readonly IBufferManager _bufferManager;     // 8 bytes (reference)

    // Pattern already on heap via QueryBuffer
    private readonly BoxedPattern? _boxedPattern;       // 8 bytes (reference)

    // State fields remain inline (~100 bytes)
    private int _currentLevel;
    private bool _init0, _init1, ..., _init11;  // 12 bytes
    private int _bindingCount0, ..., _bindingCount11;  // 48 bytes
}
```

**New size: ~200 bytes** (down from ~7.2KB)

#### 3.3 Enumerator Pool Management

```csharp
public MultiPatternScan(QuadStore store, ReadOnlySpan<char> source,
    GraphPattern pattern, IBufferManager? bufferManager = null)
{
    _bufferManager = bufferManager ?? PooledBufferManager.Shared;

    // Rent enumerator array from pool
    var lease = _bufferManager.Rent<TemporalResultEnumerator>(12);
    _enumerators = lease.Array;

    // Box pattern to avoid 4KB inline storage
    _boxedPattern = new BoxedPattern { Pattern = pattern };
}

public void Dispose()
{
    // Dispose active enumerators
    for (int i = 0; i <= _currentLevel && i < 12; i++)
    {
        if (IsInitialized(i))
            _enumerators![i].Dispose();
    }

    // Return array to pool
    _bufferManager.Return(_enumerators);
    _enumerators = null;
}
```

### Phase 4: Cascading Size Reductions

After Phase 3, other scan types that embed MultiPatternScan also shrink:

| Type | Before | After Phase 3 |
|------|--------|---------------|
| MultiPatternScan | 7,200 bytes | ~200 bytes |
| DefaultGraphUnionScan | 8,000 bytes | ~1,000 bytes |
| QueryResults (union size) | 8,000 bytes | ~1,000 bytes |
| **QueryResults (total)** | **~22KB** | **~2KB** |

### Phase 5: Validation

- [ ] **5.1** Stack consumption test
  ```csharp
  [Fact]
  public void QueryResults_StackSize_Under3KB()
  {
      // Use Unsafe.SizeOf or manual measurement
      Assert.True(Unsafe.SizeOf<QueryResults>() < 3000);
  }
  ```

- [ ] **5.2** Run full W3C test suite without stack overflow
- [ ] **5.3** Benchmark comparison (before/after)
- [ ] **5.4** Memory leak test (all pooled resources returned)

## Migration Checklist

### Pre-Refactor

- [ ] All W3C functional test failures fixed (separate from stack issues)
- [ ] Baseline benchmark recorded
- [ ] Stack size measurement recorded (~22KB)

### Phase 2: Discriminated Union

- [ ] Calculate exact field offsets for all types
- [ ] Update QueryResults with StructLayout.Explicit
- [ ] Update all constructors to set _activeScanType
- [ ] Update MoveNext() with switch dispatch
- [ ] Update Dispose() with switch dispatch
- [ ] Verify all tests pass
- [ ] Measure new stack size (~8.5KB target)

### Phase 3: Pooled Scans

- [ ] Add IBufferManager parameter to MultiPatternScan
- [ ] Replace inline enumerators with pooled array
- [ ] Box GraphPattern in MultiPatternScan
- [ ] Update DefaultGraphUnionScan similarly
- [ ] Update CrossGraphMultiPatternScan similarly
- [ ] Verify all tests pass
- [ ] Measure new stack size (~2KB target)

### Phase 4: Validation

- [ ] Full W3C test suite passes
- [ ] No performance regression (< 5% slower)
- [ ] TrackingBufferManager verifies no leaks
- [ ] Windows CI green

## Consequences

### Positive

- Stack overflow eliminated for W3C test suite
- Windows compatibility restored (1MB stack sufficient)
- Zero-GC preserved for simple queries
- Consistent with ADR-002/003 buffer patterns

### Negative

- Unsafe code required for explicit layout
- More complex QueryResults implementation
- Must ensure discriminator checked before scan access
- Debugging slightly harder (union fields show as garbage)

### Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Incorrect field access | Debug assertions verify discriminator before access |
| Memory corruption | Extensive test coverage, sanitizer in CI |
| Performance regression | Benchmark comparison before merge |
| Pool exhaustion | BufferManager handles growth automatically |

## References

- [ADR-002: IBufferManager Adoption](ADR-002-ibuffermanager-adoption.md)
- [ADR-003: Buffer Pattern for Stack Safety](ADR-003-buffer-pattern.md)
- [ADR-009: Stack Overflow Mitigation Strategy](ADR-009-stack-overflow-mitigation.md)
- [StructLayout.Explicit Documentation](https://docs.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.layoutkind)
