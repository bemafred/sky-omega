# ADR-026 - Bulk Load Path

## Status

**Status:** Superseded — 2026-04-05

Superseded by: [ADR-027 Wikidata-Scale Ingestion Pipeline](ADR-027-wikidata-scale-streaming-pipeline.md)

## Context

Mercury's write path is designed for cognitive use — ACID-compliant, fsync per write, durable thoughts that survive crashes. This is correct and non-negotiable for its primary role as semantic memory.

But Mercury also needs to ingest large external datasets: Wikidata (~10B triples), DBpedia, FHIR ontologies, Schema.org. These are bulk loads from immutable sources. If the load fails, you delete the store and start over. Per-write fsync is pure waste here — on the M5 Max SSD, each fsync costs ~4.2ms, making single-write ingestion of 10B triples take ~485 days.

### Two Access Patterns

| Pattern | Writes | Durability | Failure mode | fsync strategy |
|---------|--------|------------|--------------|----------------|
| **Cognitive** (MCP, Lucy) | Few per minute | Every write must survive crash | Observation is lost forever | fsync per write (current) |
| **Bulk load** (Wikipedia, DBpedia) | Millions per minute | Source is re-loadable | Delete store, reload | fsync at memory pressure or periodic checkpoint |

### Memory as the Governing Constraint

For bulk loading at scale, the fsync interval is not a fixed number. It's governed by **memory capacity**. The B+Tree indexes, WAL, and atom store all accumulate dirty pages during ingestion. On a 128GB machine loading 10B triples:

- The atom store grows as new IRIs/literals are interned
- B+Tree pages split and accumulate in memory-mapped regions
- The WAL grows unbounded until checkpointed

At some point, the working set exceeds available memory and the OS begins paging. Performance falls off a cliff. The bulk loader must be aware of its memory footprint and flush before this happens — not for durability, but for survival.

The right strategy: **flush when dirty state approaches a configurable memory threshold**, not at fixed triple counts. On a 128GB machine you can accumulate far more before flushing than on a 16GB machine. The hardware determines the batch size, not a constant.

## Decision

Add a bulk load mode to QuadStore that:

1. **Disables per-write fsync** — WAL writes accumulate without flushing
2. **Monitors memory pressure** — tracks dirty page accumulation against a configurable threshold
3. **Flushes at pressure boundaries** — checkpoint WAL + flush indexes when approaching the memory limit
4. **Single commit at completion** — final fsync + checkpoint when load is done
5. **No crash recovery guarantees during load** — if it fails, caller deletes the store and retries

### API Surface

```csharp
// Begin bulk load — disables per-write fsync, enables memory monitoring
var bulkLoad = store.BeginBulkLoad(new BulkLoadOptions
{
    // Flush when estimated dirty memory exceeds this.
    // Default: 75% of available physical memory.
    MemoryThresholdBytes = 96L * 1024 * 1024 * 1024, // 96GB on 128GB machine

    // Optional: progress callback for monitoring
    OnProgress = (loaded, elapsed) =>
        Console.WriteLine($"{loaded:N0} triples in {elapsed.TotalSeconds:F1}s ({loaded / elapsed.TotalSeconds:N0}/sec)")
});

// Write triples — no fsync per write, periodic checkpoint at memory pressure
foreach (var quad in parser.Parse(stream))
    bulkLoad.Add(quad);

// Commit — final fsync + checkpoint
bulkLoad.Commit();
```

### Memory Monitoring

The bulk loader tracks:
- **WAL size** — bytes written since last checkpoint
- **Atom store growth** — new atoms interned since last flush
- **B+Tree dirty pages** — estimated from insertion count and page split rate

When the estimated dirty footprint approaches the threshold, the loader:
1. Commits the current WAL batch (single fsync)
2. Checkpoints the WAL (truncate)
3. Flushes atom store indexes
4. Continues loading

This creates natural "waves" of ingestion: accumulate → flush → accumulate → flush. Each wave processes as many triples as memory allows, then makes them durable in one operation.

### WriteAheadLog Changes

Add `AppendNoSync` — identical to `Append` but without `Flush(flushToDisk: true)`:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void AppendNoSync(LogRecord record)
{
    record.TxId = ++_currentTxId;
    record.Checksum = record.ComputeChecksum();
    record.WriteTo(_writeBuffer);
    _logFile.Write(_writeBuffer);
    // No fsync — caller manages durability
}
```

The existing `Append` (cognitive path) is unchanged.

### Failure Semantics

During bulk load:
- **Crash before Commit** → store is in an undefined state. Caller must delete and retry.
- **Crash during a pressure-flush** → partial checkpoint. Store may be recoverable via WAL replay, but the guaranteed path is delete and retry.
- **Crash after Commit** → store is fully durable.

This is explicit and documented. The caller opts into bulk load mode knowing the trade-off.

## Implementation Plan

### Phase 1: WriteAheadLog.AppendNoSync
- Add `AppendNoSync` method (no fsync variant)
- Existing `Append` unchanged

### Phase 2: BulkLoadSession
- New class in `Mercury.Storage`
- Wraps QuadStore with bulk-optimized write path
- Memory monitoring via `GC.GetGCMemoryInfo()` or WAL size tracking
- Periodic checkpoint at memory threshold

### Phase 3: SPARQL LOAD Integration
- `LOAD <file:///path>` in bulk mode when loading large files
- Threshold: if source file > configurable size, auto-enable bulk mode

### Phase 4: CLI Integration
- `mercury load --bulk <file>` command
- Progress reporting during ingestion

## Success Criteria

- [ ] Bulk load of 1M triples is significantly faster than single-write path
- [ ] Memory stays within configured threshold during load
- [ ] Periodic flushes create durable checkpoints at pressure boundaries
- [ ] Cognitive write path (`Append` with fsync) is completely unchanged
- [ ] Crash during bulk load does not corrupt existing store data (if store was empty at start)

## Consequences

### Positive
- Wikipedia-scale ingestion becomes feasible on consumer hardware
- Memory-aware flushing adapts to any hardware (16GB laptop to 128GB workstation)
- Cognitive path untouched — no risk to semantic memory durability

### Trade-offs
- Bulk load has no crash recovery — explicit design choice, not a limitation
- Two write paths to maintain (but they share the WAL infrastructure)
- Memory monitoring adds complexity (mitigated by using OS-level metrics)

## References
- fsync micro-benchmark (2026-03-30): M5 Max fsync latency ~4.2ms, constant regardless of write size
- [Wikidata dump](https://www.wikidata.org/wiki/Wikidata:Database_download): ~10B triples, 114 GB bzip2-compressed Turtle, 912 GB uncompressed
- PostgreSQL group commit: prior art for batched fsync in databases
