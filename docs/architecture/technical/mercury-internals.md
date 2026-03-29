# Mercury Internals

Detailed technical reference for Mercury's storage, durability, concurrency, and zero-GC design.

## Storage Layer (`SkyOmega.Mercury.Storage`)

| Component      | Purpose                                                                |
|----------------|------------------------------------------------------------------------|
| `QuadStore`    | Multi-index quad store with named graph support                        |
| `QuadIndex`    | B+Tree index with bitemporal + graph support (GSPO, GPOS, GOSP, TGSPO) |
| `AtomStore`    | String interning with memory-mapped storage                            |
| `PageCache`    | LRU cache for B+Tree pages (clock algorithm)                           |
| `TrigramIndex` | Full-text search via trigram inverted index (opt-in)                   |

## Durability Design

Sky Omega uses Write-Ahead Logging (WAL) for crash safety:

1. **Write path**: WAL append → fsync → apply to indexes
2. **Recovery**: Replay only committed WAL entries after last checkpoint (uncommitted batches discarded)
3. **Checkpointing**: Hybrid trigger (size OR time, whichever first)

**Design decisions:**

- **AtomStore has no separate WAL**: Append-only by design. On recovery, validate tail and rebuild hash index.
- **WAL stores atom IDs, not strings**: Atoms persisted before WAL write (we need IDs to write the record).
- **Batch-first design**: TxId in WAL records enables batching. Amortizing fsync across N triples is critical for performance.
- **Transaction boundaries**: `BeginTx`/`CommitTx` markers in WAL. Recovery two-pass: collect committed TxIds, then replay only committed records.
- **Deferred materialization**: Batched writes buffer in memory, apply to indexes only at `CommitBatch()`. `RollbackBatch()` discards buffer — indexes untouched.
- **Transaction time per-write**: Each write generates `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` stored in WAL and indexes. Preserved through crash recovery.
- **Hybrid checkpoint trigger**: Size-based (16MB) adapts to bursts; time-based (60s) bounds recovery during idle.

## Batch Write API

Use the batch API for high-throughput bulk loading (~100,000 triples/sec vs ~300/sec for single writes). See [api-usage.md#batch-write-api](../../api/api-usage.md#batch-write-api).

**Performance characteristics:**
- Single writes: ~250-300/sec (fsync per write)
- Batch of 1,000: ~25,000+/sec (1 fsync per batch)
- Batch of 10,000: ~100,000+/sec

## Named Graphs (Quads)

QuadStore supports RDF named graphs for domain isolation. See [api-usage.md#named-graphs-quads](../../api/api-usage.md#named-graphs-quads).

**Design notes:**
- **Multiple index paths**: GSPO, GPOS, GOSP, TGSP for efficient query patterns from any entry point
- **Graph isolation**: Default graph (atom 0) and named graphs are fully isolated
- **TemporalKey**: 56 bytes (GraphAtom + SubjectAtom + PredicateAtom + ObjectAtom + ValidFrom + ValidTo + TransactionTime)
- **WAL record**: 80 bytes (v2: includes GraphId, TransactionTimeTicks, BeginTx/CommitTx markers)

## Pruning (`SkyOmega.Mercury.Pruning`)

Dual-instance pruning system using copy-and-switch pattern. Transfers quads between QuadStore instances with filtering, enabling:
- Soft deletes → hard deletes (physical removal)
- History flattening or preservation
- Graph/predicate-based filtering

**Components:**

| Component | Purpose |
|-----------|---------|
| `PruningTransfer` | Orchestrates transfer from source to target store |
| `IPruningFilter` | Filter interface for inclusion criteria |
| `GraphFilter` | Filter by graph IRI(s) - include/exclude modes |
| `PredicateFilter` | Filter by predicate IRI(s) |
| `CompositeFilter` | AND/OR composition of filters |
| `HistoryMode` | FlattenToCurrent, PreserveVersions, PreserveAll |
| `TransferOptions` | Batch size, progress interval, verification flags |

**Basic usage:**
```csharp
using var target = new QuadStore("/path/to/new/store");
var result = new PruningTransfer(source, target).Execute();
// Soft-deleted quads are now physically gone
```

**With filtering:**
```csharp
var options = new TransferOptions {
    Filter = CompositeFilter.All(
        GraphFilter.Exclude("<http://temp.data>"),
        PredicateFilter.Exclude("<http://internal/debug>")),
    HistoryMode = HistoryMode.FlattenToCurrent,
    VerifyAfterTransfer = true
};
var result = new PruningTransfer(source, target, options).Execute();
```

**Verification options:**
- `DryRun` - Preview what would transfer without writing
- `VerifyAfterTransfer` - Re-enumerate and verify counts match
- `ComputeChecksum` - FNV-1a checksum for content verification
- `AuditLogPath` - Write filtered-out quads to N-Quads file

## Concurrency Design

QuadStore uses `ReaderWriterLockSlim` for thread-safety:

1. **Single writer, multiple readers**: Write operations acquire exclusive write lock
2. **Explicit read locking**: Callers use `AcquireReadLock()`/`ReleaseReadLock()` around query enumeration
3. **ref struct constraint**: `TemporalResultEnumerator` cannot hold locks internally (stack-only lifetime)

**Critical pattern - always wrap queries with locks:**
```csharp
store.AcquireReadLock();
try
{
    var results = store.QueryCurrent(subject, predicate, obj);
    while (results.MoveNext()) { /* process */ }
    results.Dispose();  // Return pooled buffer
}
finally
{
    store.ReleaseReadLock();
}
```

## Zero-GC Design Principles

All parsers use aggressive zero-allocation techniques:
- `ref struct` parsers that live entirely on the stack
- `ArrayPool<T>` for all buffer allocations
- `ReadOnlySpan<char>` for string operations
- String interning via AtomStore to avoid duplicate allocations
- Streaming enumerators that yield results without materializing collections

**Key insight: Zero-GC ≠ "everything on stack"**

Zero-GC means **no uncontrolled allocations**, not "avoid heap entirely". Pooled heap memory is equally zero-GC as stack memory, but without size limits.

**The Buffer + View pattern** (used by `Span<T>`, `Utf8JsonReader`, `System.IO.Pipelines`):
- Tiny handle/view struct (just a `Span<byte>` or pointer + length)
- Caller owns/provides storage (stackalloc for small, pooled array for large, mmap for persistence)
- Typed access via `MemoryMarshal.AsRef<T>()` for discriminated unions

Implemented in `PatternSlot` (`src/Mercury/Sparql/Patterns/PatternSlot.cs`) - a 64-byte cache-aligned slot with discriminator byte and typed views over raw bytes.

**Stack safety for large ref structs:** Large ref structs like `QueryResults` (~22KB) can cause stack overflow in complex query paths. The solution is to materialize results to heap (`List<MaterializedRow>`) early, returning only the pointer through the call chain. See [ADR-003: Buffer Pattern for Stack Safety](../../adrs/mercury/ADR-003-buffer-pattern.md) for details.

**Critical patterns:**

1. **Parser callback API (zero-GC)** - spans valid only during callback:
```csharp
await parser.ParseAsync((subject, predicate, obj) =>
{
    store.AddCurrent(subject, predicate, obj);
});
```

2. **Query result disposal** - always call `Dispose()`:
```csharp
var results = executor.Execute();
try { while (results.MoveNext()) { /* process */ } }
finally { results.Dispose(); }
```

**Zero-GC compliance by component:**

| Component | Status | Notes |
|-----------|--------|-------|
| SPARQL Parser | ✓ Zero-GC | ref struct, no allocations |
| Query Executor | ✓ Zero-GC | ref struct operators, call Dispose() |
| QuadStore Query | ✓ Zero-GC | Pooled buffer, call Dispose() |
| Turtle Parser (Handler) | ✓ Zero-GC | Use TripleHandler callback |
| Turtle Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| N-Triples Parser (Handler) | ✓ Zero-GC | Use TripleHandler callback |
| N-Triples Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| N-Quads Parser (Handler) | ✓ Zero-GC | Use QuadHandler callback |
| N-Quads Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| TriG Parser (Handler) | ✓ Zero-GC | Use QuadHandler callback |
| TriG Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| TriG Writer | ✓ Zero-GC | Streaming output, no allocations |
| JSON-LD Parser (Handler) | Near Zero-GC | Uses System.Text.Json, allocates for context |
| JSON-LD Parser (Legacy) | Allocates | IAsyncEnumerable for compatibility |
| JSON-LD Writer | Allocates | Collects quads, outputs on flush |
| RDF/XML Parser | Near Zero-GC | Allocates for namespace dictionary + async boundaries |

## Turtle Parser (`SkyOmega.Mercury.Turtle`)

`TurtleStreamParser` is a `partial class` split across files:
- `TurtleStreamParser.cs` - Main parser logic and `ParseAsync()` entry points
- `TurtleStreamParser.Buffer.cs` - Buffer management
- `TurtleStreamParser.Structures.cs` - RDF structure parsing (blank nodes, collections)
- `TurtleStreamParser.Terminals.cs` - Terminal parsing (IRIs, literals, prefixed names)

Supports RDF-star (RDF 1.2) syntax - reified triples converted to standard RDF reification for storage/query.

## RDF Writers

| Feature | N-Triples | Turtle | RDF/XML |
|---------|-----------|--------|---------|
| Prefix/namespace support | No | Yes | Yes |
| Subject grouping | No | Yes (`;`) | Yes (`rdf:Description`) |
| `rdf:type` shorthand | No | Yes (`a`) | No |
| Language tags | Yes | Yes | Yes (`xml:lang`) |
| Typed literals | Yes | Yes | Yes (`rdf:datatype`) |
| Blank nodes | Yes | Yes | Yes (`rdf:nodeID`) |
