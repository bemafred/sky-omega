# Mercury Internals

Detailed technical reference for Mercury's storage, durability, concurrency, and zero-GC design.

## Storage Layer (`SkyOmega.Mercury.Storage`)

| Component      | Purpose                                                                |
|----------------|------------------------------------------------------------------------|
| `QuadStore`    | Multi-index quad store with named graph support                        |
| `TemporalQuadIndex` | Cognitive B+Tree index: each entry a period of a quad, with its memorized and forgotten times (GSPO, GPOS, GOSP, TGSP) |
| `AtomStore`    | String interning with memory-mapped storage                            |
| `BTreeFile`    | The file a B+Tree index lives in: mapped in extents, grows by adding one ([ADR-063](../../adrs/mercury/ADR-063-an-index-file-grows-by-extents.md)) |
| `TrigramIndex` | Full-text search via trigram inverted index (opt-in)                   |

## The Log

`mercury` and `mercury-mcp` keep a log file, `mercury.log`, in the pool's directory beside `pool.json`
(`FileLogger`, handed to every store through `QuadStorePoolOptions.Logger`). A line is the time in UTC, the level
and the message:

```
2026-11-14T18:05:11.249Z INF mercury 1.8.43 starts, process 53689
2026-11-14T18:05:11.249Z INF Opening store at /…/stores/01a1db3b…
2026-11-14T18:05:11.298Z INF mercury stops, process 53689
```

A run that has no `stops` line did not close its stores. A store that could not apply a committed write logs the
internal error at `CRT`, followed by the cause and its stack (ADR-061 M7); the store opened again logs how many
records it applied from its log. The file is put aside as `mercury.log.1` at 4 MB, and two files are kept at
most. A library user gets no log unless it gives a logger to its pool or its store.

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
- **One clock reading per write**: A Cognitive write reads the store's clock (`StorageOptions.Clock`) once, under the write lock, in milliseconds; a batch has one reading. The write rule decides from that reading what changes, and the log holds the decided change: `BeginPeriod`, `EndPeriod`, `RemovePeriod` or `ContinuePeriod`, each naming its period by the quad and its memorized time, so a replay applies it to the same entry and applying it twice changes nothing ([ADR-061](../../adrs/mercury/ADR-061-a-facts-own-times.md) M5, M7). No caller states a time. A log written by an earlier version replays its `Add`, `End` and `Retract` records as written. A Graph or Minimal store reads no clock ([ADR-064](../../adrs/mercury/ADR-064-a-facts-states.md) D1).
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
- **Multiple index paths**: GSPO, GPOS, GOSP, TGSP for efficient query patterns from any entry point (Cognitive; a Graph store keeps GSPO, GPOS and GOSP, a Minimal store GSPO only, ADR-064)
- **Graph isolation**: Default graph (atom 0) and named graphs are fully isolated
- **TemporalKey**: 56 bytes (GraphAtom + SubjectAtom + PredicateAtom + ObjectAtom + MemorizedAt + ForgottenAt + LegacyTransactionTime)
- **WAL record**: 80 bytes (v2: includes GraphId, TransactionTimeTicks, BeginTx/CommitTx markers). On a Graph or Minimal store every record is `NextVersion`, the change decided at the write: the fact goes from the version in bytes 48 to 55 to the next, and the other time fields are 0 (ADR-064 D5).

## Pruning (`SkyOmega.Mercury.Pruning`)

Dual-instance pruning system using copy-and-switch pattern. Transfers quads between QuadStore instances with filtering. There is one way to prune ([ADR-061](../../adrs/mercury/ADR-061-a-facts-own-times.md) D12):
- What is remembered is transferred, each memory with the time it was memorized; the target's period begins then, not at the prune. The copy that carries the time is internal to Mercury (`QuadStore.CopyRememberedBatched`): no ordinary writer states a time.
- Nothing forgotten is transferred. A `FORGOTTEN` question on the target finds nothing, and a source in which everything is forgotten gives an empty store.
- Graph/predicate-based filtering selects among the remembered memories.
- A rebuild is not a prune: it carries every period, with both times.

**Components:**

| Component | Purpose |
|-----------|---------|
| `PruningTransfer` | Orchestrates transfer from source to target store |
| `IPruningFilter` | Filter interface for inclusion criteria |
| `GraphFilter` | Filter by graph IRI(s) - include/exclude modes |
| `PredicateFilter` | Filter by predicate IRI(s) |
| `CompositeFilter` | AND/OR composition of filters |
| `TransferOptions` | Batch size, progress interval, verification flags |

**Basic usage:**
```csharp
using var target = new QuadStore("/path/to/new/store");
var result = new PruningTransfer(source, target).Execute();
// The target holds what the source remembers, with its memorized times, and nothing forgotten
```

**With filtering:**
```csharp
var options = new TransferOptions {
    Filter = CompositeFilter.All(
        GraphFilter.Exclude("<http://temp.data>"),
        PredicateFilter.Exclude("<http://internal/debug>")),
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
3. **No lock in the enumerator**: `ResultEnumerator` and `TemporalResultEnumerator` hold no lock; the caller holds the read lock for the whole enumeration ([ADR-062](../../adrs/mercury/ADR-062-what-a-read-yields.md) describes the two result types)

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
