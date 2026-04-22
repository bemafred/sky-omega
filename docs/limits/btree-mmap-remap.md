# Limit: B+Tree index has no mmap remap

**Status:**        Latent
**Surfaced:**      2026-04-22, during Phase 6 pre-flight for 21.3B Wikidata Reference bulk-load
**Last reviewed:** 2026-04-22
**Promotes to:**   ADR when (a) a single store genuinely needs to exceed the current 1 TB sparse floor for Reference / the TemporalQuadIndex initial size for Cognitive, OR (b) a workload materializes that cannot predict its final size at store-open time

## Description

`ReferenceQuadIndex` and `TemporalQuadIndex` both acquire a single fixed-size mmap view at store open:

```csharp
_mmapFile = MemoryMappedFile.CreateFromFile(_fileStream, mapName: null, capacity: 0, ...);
_accessor = _mmapFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
_accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);
```

`AllocatePage` extends the file via `_fileStream.SetLength(newSize)` when required, but the mmap view and `_basePtr` are never remapped. Writing to a pageId whose byte offset lies beyond the original view size reads/writes stale or unmapped memory — at worst a segfault, at best silent corruption.

The latency is latent because every validation run to date fit inside the initial sparse allocation:

| Scale | GSPO actual size | Initial allocation (BulkMode) | Fits |
|---|---:|---:|---:|
| 1 M | ~4 MB | 256 GB (pre-2026-04-22), now 1 TB | yes |
| 10 M | ~40 MB | 256 GB / 1 TB | yes |
| 100 M | ~400 MB | 256 GB / 1 TB | yes |
| 1 B | ~2 GB | 256 GB / 1 TB | yes |
| 21.3 B | **~670 GB** | 256 GB → **would crash**; 1 TB → yes | yes with current fix |

The 2026-04-22 floor bump to 1 TB (`ReferenceQuadIndex.cs` line 78) covers the full Wikidata 21.3B case. Any scale past 1 TB actual data (~33B triples at 32B ReferenceKey) would re-surface the gap.

## Why this is a limit, not a bug

The fix to implement proper remap is non-trivial:

- Stale `_basePtr` handling — every site that caches `_basePtr`-derived addresses (PageView structs, page cache entries, active enumerators) must be invalidated or refreshed after remap.
- Reader/writer coordination — readers holding `_basePtr` via `AcquirePointer` must release before remap, reacquire after. Today's single-writer contract (ADR-020) plus external `QuadStore._lock` can support this, but the state machine is new.
- Testing surface — a grow must not corrupt the tree. Needs deliberate stress-test at the grow boundary, analogous to ADR-028's rehash-on-grow tests for AtomStore.
- Remap semantics on macOS vs Linux differ; the implementation must be portable or documented-as-platform-specific.

Shipping a 1 TB floor for Reference BulkMode is a ~2-line change and unblocks every Wikidata-adjacent scale we care about near-term. The real remap implementation is worth doing later, against a motivating workload.

## Trigger condition

Promote to ADR when any of:

- A concrete workload requires a store past 1 TB of actual B+Tree data (~33B ReferenceKey entries or ~12B TemporalKey entries).
- A multi-tenant or long-lived store accumulates past 1 TB through incremental writes (not just bulk-load) and cannot be planned for at open time.
- `AllocatePage` hits the mmap ceiling in production logs or crash reports.

## Current state

Latent. The 1 TB floor covers the 21.3B Phase 6 target with ~33% headroom. No production workload has surfaced the ceiling. AtomStore has its own grow-handling via ADR-028's rehash-on-grow; the gap is specific to the B+Tree indexes.

The Cognitive profile's `TemporalQuadIndex` has the same gap but its initial sizes and typical workloads are smaller — hasn't surfaced in practice.

## Related

- [ADR-028](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md) — AtomStore solves the equivalent problem for the atom hash table via rehash-on-grow. The B+Tree case would follow a similar discipline but the remap state machine is genuinely more involved because readers may hold page pointers across the boundary.
- [ADR-032](../adrs/mercury/ADR-032-radix-external-sort.md) — the radix external sort makes the rebuild path's temp files grow large, but those are separate FileStream reads/writes, not mmap-backed. This limit applies only to the primary and secondary index files.
- [ADR-033](../adrs/mercury/ADR-033-bulk-load-radix-external-sort.md) — the bulk-load sorter accumulates entries in memory + temp files, not in the final index, so the sorter itself is unaffected. The limit applies at the `AppendSorted` drain phase.
