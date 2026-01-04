# IBufferManager Adoption Plan

## Background

We introduced `IBufferManager` in `Mercury.Runtime/Buffers` after struggling with stack overflows from aggressive `stackalloc` usage. This plan systematically adopts the pattern across Mercury.

## Current State

- **IBufferManager adoption:** 100% COMPLETE (All 6 phases done)
- **Files Updated:** RDF Parsers (6), RDF Writers (8), SPARQL Query Engine (5), Storage Layer (2), Factory Methods (3)
- **Tests Added:** 20 new buffer manager tests in `BufferManagerTests.cs`
- **Direct ArrayPool usage:** Eliminated in updated files (replaced with PooledBufferManager)
- **Stackalloc usage:** Now uses AllocateSmart hybrid pattern where appropriate
- **Total tests:** 1343 (all passing)

## Guiding Principles

1. **Constructor injection** - Pass `IBufferManager` as constructor parameter
2. **Default to shared** - Use `PooledBufferManager.Shared` as default for backward compatibility
3. **Keep stackalloc for tiny buffers** - Under 64 bytes, stackalloc is optimal
4. **Use AllocateSmart for medium buffers** - 64-2048 bytes benefit from hybrid approach
5. **Always pool large buffers** - Over 2048 bytes should always be pooled

---

## Phase 1: RDF Parsers (6 files) ✓ COMPLETED

**Priority:** High | **Effort:** Low | **Risk:** Low

### Files Updated

1. ✓ `src/Mercury/Turtle/TurtleStreamParser.cs`
2. ✓ `src/Mercury/NTriples/NTriplesStreamParser.cs`
3. ✓ `src/Mercury/RdfXml/RdfXmlStreamParser.cs` (+ Buffer.cs)
4. ✓ `src/Mercury/NQuads/NQuadsStreamParser.cs`
5. ✓ `src/Mercury/TriG/TriGStreamParser.cs`
6. ✓ `src/Mercury/JsonLd/JsonLdStreamParser.cs`

### Pattern Applied

```csharp
// Before
private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
private readonly ArrayPool<char> _charPool = ArrayPool<char>.Shared;

// After
private readonly IBufferManager _bufferManager;

public TurtleStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
{
    _bufferManager = bufferManager ?? PooledBufferManager.Shared;
    _inputBuffer = _bufferManager.Rent<byte>(bufferSize).Array!;
    _outputBuffer = _bufferManager.Rent<char>(OutputBufferSize).Array!;
}
```

### Checklist

- [x] Add `IBufferManager?` parameter to constructor
- [x] Store as private readonly field
- [x] Replace `ArrayPool<byte>.Shared.Rent()` with `_bufferManager.Rent<byte>().Array!`
- [x] Replace `ArrayPool<char>.Shared.Rent()` with `_bufferManager.Rent<char>().Array!`
- [x] Update Dispose() to use `_bufferManager.Return()`
- [x] Add project reference: Mercury → Mercury.Runtime
- [x] All 1323 tests pass

---

## Phase 2: RDF Writers (8 files) ✓ COMPLETED

**Priority:** High | **Effort:** Low | **Risk:** Low

### Files Updated

1. ✓ `src/Mercury/Turtle/TurtleStreamWriter.cs`
2. ✓ `src/Mercury/NTriples/NTriplesStreamWriter.cs`
3. ✓ `src/Mercury/RdfXml/RdfXmlStreamWriter.cs`
4. ✓ `src/Mercury/NQuads/NQuadsStreamWriter.cs`
5. ✓ `src/Mercury/TriG/TriGStreamWriter.cs`
6. ✓ `src/Mercury/Sparql/Results/SparqlJsonResultWriter.cs`
7. ✓ `src/Mercury/Sparql/Results/SparqlXmlResultWriter.cs`
8. ✓ `src/Mercury/Sparql/Results/SparqlCsvResultWriter.cs`

### Pattern Applied

Same as Phase 1 - constructor injection with optional parameter.

### Checklist

- [x] Add `IBufferManager?` parameter to all writer constructors
- [x] Replace direct ArrayPool references
- [x] Update Dispose() and DisposeAsync() methods
- [x] Update EnsureCapacity() buffer growth
- [x] Ensure backward compatibility (default to PooledBufferManager.Shared)
- [x] All 1323 tests pass

---

## Phase 3: SPARQL Query Engine (5 files) ✓ COMPLETED

**Priority:** High | **Effort:** Medium | **Risk:** Medium

### Files Updated

1. ✓ `src/Mercury/Sparql/Execution/QueryExecutor.cs` - **CRITICAL**
2. ✓ `src/Mercury/Sparql/Execution/Operators.cs`
3. ✓ `src/Mercury/Sparql/Execution/SlotBasedOperators.cs`
4. ✓ `src/Mercury/Sparql/Execution/UpdateExecutor.cs`
5. ✓ `src/Mercury/Sparql/Execution/VariableGraphExecutor.cs`

### QueryExecutor Changes

QueryExecutor now uses IBufferManager with optional constructor parameter:
- Added `IBufferManager? bufferManager` parameter to all constructors
- Instance methods use `_bufferManager` for buffer allocation/return
- Static methods use `PooledBufferManager.Shared` directly (simplest approach)

### Pattern Applied for Operators (ref structs)

```csharp
// Before
_innerStringBuffer = new char[512];

// After
_innerStringBuffer = PooledBufferManager.Shared.Rent<char>(512).Array!;

public void Dispose()
{
    // existing dispose logic...
    if (_innerStringBuffer != null)
        PooledBufferManager.Shared.Return(_innerStringBuffer);
}
```

### Checklist

- [x] Add IBufferManager to QueryExecutor constructor
- [x] Static helper methods use PooledBufferManager.Shared directly
- [x] Update all operator constructors (SubQueryScan, SubQueryJoinScan, SlotMultiPatternScan)
- [x] Update Dispose() methods to return buffers
- [x] All 1323 tests pass

---

## Phase 4: Storage Layer (2 files) ✓ COMPLETED

**Priority:** Medium | **Effort:** Low | **Risk:** Low

### Files Updated

1. ✓ `src/Mercury/Storage/AtomStore.cs`
2. ✓ `src/Mercury/Storage/WriteAheadLog.cs` - Updated for consistency

### AtomStore Pattern

```csharp
// Before (lines 178-180)
Span<byte> utf8Bytes = byteCount <= 512
    ? stackalloc byte[byteCount]
    : new byte[byteCount];

// After - use AllocateSmart extension
Span<byte> stackBuffer = stackalloc byte[Math.Min(byteCount, 512)];
var utf8Bytes = _bufferManager.AllocateSmart(byteCount, stackBuffer, out var rentedBuffer);
try
{
    // use utf8Bytes...
}
finally
{
    rentedBuffer.Dispose();
}
```

### WriteAheadLog

Updated for consistency - replaced `ArrayPool<byte>.Shared` with `PooledBufferManager.Shared` in:
- `TruncateLog()` method
- `RecoverState()` method
- `LogRecordEnumerator` constructor and Dispose

### Checklist

- [x] Add IBufferManager to AtomStore constructor
- [x] Replace conditional stackalloc with AllocateSmart pattern
- [x] Update WriteAheadLog for consistency (replaces ArrayPool with PooledBufferManager)
- [x] All 1323 tests pass

---

## Phase 5: Factory Methods & Integration ✓ COMPLETED

**Priority:** Medium | **Effort:** Medium | **Risk:** Low

### Files Updated

1. ✓ `src/Mercury/Storage/QuadStore.cs` - Propagates IBufferManager to AtomStore and WriteAheadLog
2. ✓ `src/Mercury/Rdf/RdfFormat.cs` - RdfFormatNegotiator.CreateParser/CreateWriter accept IBufferManager
3. ✓ `src/Mercury/Sparql/Results/SparqlResultFormat.cs` - SparqlResultFormatNegotiator.CreateWriter accepts IBufferManager

### QuadStore Integration

QuadStore now propagates IBufferManager to internal components:

```csharp
public QuadStore(string baseDirectory, ILogger? logger, IBufferManager? bufferManager)
{
    _bufferManager = bufferManager ?? PooledBufferManager.Shared;
    _atoms = new AtomStore(atomPath, _bufferManager);
    _wal = new WriteAheadLog(walPath, DefaultCheckpointSizeThreshold,
        DefaultCheckpointTimeSeconds, _bufferManager);
}
```

Also updated `TemporalResultEnumerator` to use `PooledBufferManager.Shared` instead of `ArrayPool<char>.Shared`.

### Factory Method Pattern

```csharp
// RdfFormatNegotiator
public static IDisposable CreateParser(Stream stream, RdfFormat format, IBufferManager? bufferManager = null)
public static IDisposable CreateWriter(TextWriter writer, RdfFormat format, IBufferManager? bufferManager = null)

// SparqlResultFormatNegotiator
public static IDisposable CreateWriter(TextWriter writer, SparqlResultFormat format, IBufferManager? bufferManager = null)
```

### Checklist

- [x] Add IBufferManager to QuadStore constructor (propagates to AtomStore, WriteAheadLog)
- [x] Update RdfFormatNegotiator.CreateParser/CreateWriter with IBufferManager parameter
- [x] Update SparqlResultFormatNegotiator.CreateWriter with IBufferManager parameter
- [x] Update TemporalResultEnumerator to use PooledBufferManager
- [x] All 1323 tests pass

---

## Phase 6: Testing & Validation ✓ COMPLETED

**Priority:** High | **Effort:** Medium | **Risk:** N/A

### Files Created

1. ✓ `tests/Mercury.Tests/BufferManagerTests.cs` - 20 new tests

### Test Categories Implemented

1. **Parser injection tests (5)** - NTriples, Turtle, RDF/XML, N-Quads, TriG
2. **Writer injection tests (6)** - NTriples, Turtle, RDF/XML, SPARQL JSON/XML/CSV
3. **Storage layer tests (3)** - AtomStore, QuadStore, WriteAheadLog
4. **Factory method tests (3)** - RdfFormatNegotiator, SparqlResultFormatNegotiator
5. **Stress tests (3)** - High-throughput parsing, writing, and AtomStore operations

### TrackingBufferManager

Created test implementation in `BufferManagerTests.cs`:

```csharp
public sealed class TrackingBufferManager : IBufferManager
{
    public int RentCount { get; }
    public int ReturnCount { get; }
    public long TotalBytesRented { get; }
    public int OutstandingBuffers => RentCount - ReturnCount;

    public BufferLease<T> Rent<T>(int minimumLength) where T : unmanaged { ... }
    public void Return<T>(T[] array, bool clearArray = false) where T : unmanaged { ... }

    public void AssertAllReturned() { ... }  // Strict leak check
    public void AssertNoLeaks() { ... }      // Exact balance check
    public void AssertSomeActivity() { ... } // Verify injection worked
    public void AssertBufferManagerUsed() { ... } // Lenient check
}
```

### Checklist

- [x] Create TrackingBufferManager with rent/return tracking
- [x] Add parser injection tests for all 5 parsers
- [x] Add writer injection tests for all 6 writers
- [x] Add storage layer tests (AtomStore, QuadStore, WriteAheadLog)
- [x] Add factory method tests
- [x] Add stress tests for high-throughput scenarios
- [x] All 1343 tests pass (20 new + 1323 existing)

---

## Components NOT to Refactor

| Component | Reason |
|-----------|--------|
| BindingTable 24-32 byte stackalloc | Too small, stackalloc optimal |
| PatternSlot structures | Fixed-size, stack optimal |
| WAL 72-byte buffer | Single allocation, no benefit |
| PageCache internals | Different allocation model (pages) |

---

## Migration Order

```
Phase 1 (Parsers) ─┬─► Phase 3 (Query Engine) ─► Phase 5 (Factories)
                   │                                      │
Phase 2 (Writers) ─┘                                      ▼
                                                   Phase 6 (Tests)
Phase 4 (Storage) ─────────────────────────────────────────┘
```

Phases 1-2 can run in parallel. Phase 3 depends on patterns established in 1-2.

---

## Success Metrics

1. **100% IBufferManager adoption** in parsers, writers, query engine
2. **Zero direct ArrayPool references** (except in PooledBufferManager itself)
3. **Zero inline `new T[]` allocations** over 64 bytes in hot paths
4. **All tests pass** with TrackingBufferManager asserting balanced rent/return
5. **No performance regression** (benchmark validation)

---

## Estimated Effort

| Phase | Files | Effort |
|-------|-------|--------|
| Phase 1 | 6 | 2-3 hours |
| Phase 2 | 8 | 2-3 hours |
| Phase 3 | 5 | 4-6 hours |
| Phase 4 | 2 | 1-2 hours |
| Phase 5 | varies | 2-3 hours |
| Phase 6 | new | 3-4 hours |
| **Total** | ~21 | **~16-20 hours** |

---

## Future Enhancements

1. **Statistics-aware buffer sizing** - Track actual sizes, optimize defaults
2. **Per-thread buffer pools** - Reduce contention in high-concurrency scenarios
3. **Memory pressure callbacks** - Trim pools under memory pressure
4. **Diagnostic buffer manager** - Production telemetry on allocation patterns
