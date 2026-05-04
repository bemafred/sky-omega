# Bulk-load data flow (Reference profile, Sorted-backed)

**Purpose.** Hold the end-to-end ingest flow visible while we work. Every optimization, every limits-register entry, every "where is the wall-clock cost living" question lands somewhere on this map. Captured 2026-05-04 during the cycle 7 instrumentation gradient — when the merge phase finally became measurable instead of opaque.

This is the canonical map for **Reference profile bulk-load** (Sorted-backed atom store, ADR-034). Cognitive profile follows a different path (HashAtomStore, WAL-backed, mutable). The shapes are similar but the disk artifacts and lifecycle differ.

## High-level flow

```
                           SOURCE
   /Users/.../latest-all.ttl.bz2                      114 GB compressed
                              │
                              │ BZip2DecompressorStream
                              │ single-threaded, ~30 MB/s decompressed
                              │ (ADR-036 Phase 2 — parallel bz2 not used in bulk path)
                              ▼
                        DECOMPRESSED BYTES
                       (in-memory streaming)                no persistent artifact
                              │
                              │ TurtleStreamParser
                              │ zero-GC, BCL-only
                              │ ~450K triples/sec sustained on M5 Max
                              ▼
                       TRIPLE STREAM
                  (subj_bytes, pred_bytes, obj_bytes)        in-memory, transient
                              │
                              │ For each triple:
                              │   AddOneAtomOccurrence(S_bytes)  → _globalIdx 3T+0
                              │   AddOneAtomOccurrence(P_bytes)  → _globalIdx 3T+1
                              │   AddOneAtomOccurrence(O_bytes)  → _globalIdx 3T+2
                              │
                              │ _globalIdx is the implicit position encoder:
                              │ occurrence 3T+{0,1,2} belongs to triple T.
                              │ Reference profile uses Turtle (default graph) so
                              │ no per-triple graph atom; 3 atoms/triple.
                              ▼
            SortedAtomBulkBuilder._spillBuffer
                List<(byte[] Bytes, long InputIdx)>          in-memory, ~1 GB peak
                              │
                              │ When _spillBufferBytes >= 1 GB:
                              │   1. Sort buffer (introsort, ~5 sec / 17M items)  ← parser blocked
                              │   2. Write chunk-NNNNNN.bin (~0.4 sec / 1 GB)     ← parser blocked
                              │   3. Clear buffer, resume parsing
                              ▼
        bulk-tmp/sorted-vocab/occurrences/chunk-NNNNNN.bin
            ~5,000-15,000 files at 21.3B (1 GB each)         on disk: ~4 TB at 21.3B
            Records: (int32 length, int64 inputIdx, raw_bytes)
                              │
                              │ At parser end → FlushToDisk → SortedAtomBulkBuilder.Finalize
                              │
                              ▼
                    MergeAndWrite (k-way merge)
                              │
                              │ Open all chunks (BoundedFileStreamPool, auto-sized to chunkCount)
                              │ PriorityQueue<int, ChunkPriorityKey>:
                              │   - Pop next-globally-smallest record
                              │   - Dedupe (skip if same as prev)
                              │   - On unique: assign atomCount, prefix-compress, emit
                              │   - Always emit ResolveRecord(inputIdx → atomCount)
                              │
                              │ Two outputs split here:
                              ▼                                            ▼
              atoms.atoms + atoms.offsets                  ResolveSorter (disk-backed)
              SORTED VOCABULARY                            DiskBackedAssignedIds
              ~80 GB compressed at 21.3B                   ~340 GB at 21.3B
              prefix-compressed, anchor every 64           ExternalSorter<ResolveRecord>
                                                                       │
                                                                       │ EnumerateResolved
                                                                       │ walks resolver in input order,
                                                                       │ takes 3 IDs at a time
                                                                       │ → (S_id, P_id, O_id)
                                                                       ▼
                                                       Bulk GSPO sorter (ExternalSorter)
                                                       24-byte triple records
                                                       sorts by (G, S, P, O)
                                                                                ~700 GB intermediate
                                                                       │
                                                                       │ Drain into B+Tree
                                                                       ▼
                                                                gspo.tdb
                                                                SEALED PRIMARY INDEX
                                                                       │
                                                                       │ Rebuild phase
                                                                       │ Iterate GSPO →
                                                                       │ project to:
                                                                       ▼
                                              gpos.tdb           trigram.hash + .posts
                                              SECONDARY          TEXT INDEX
                                                                       │
                                                                       ▼
                                              REFERENCE STORE — queryable, sealed
                                              total ~1.5 TB final substrate
```

## Stage-by-stage cost map

Time at 21.3B is the current best estimate; the gradient runs (cycle 7 onward) refine these.

| # | Stage | Mechanism | Estimated time @ 21.3B | Bottleneck |
|---|---|---|---|---|
| 1 | Decompress | BZip2 stream (single-threaded) | overlapped with parse | parser-bound; bz2 produces faster than parser consumes |
| 2 | Parse | `TurtleStreamParser` | ~13 h | parser CPU |
| 3 | Intern + buffer | atom bytes into `_spillBuffer` | overlapped with parse | parser CPU |
| 4 | **Spill (sort + write)** | per-chunk introsort + sequential write | **~17 min × ~200 spills, blocking parser** | **sort CPU** (~30% wall-clock at small scales, smaller fraction at 21.3B) |
| 5 | Merge atoms | k-way merge + prefix compress | ~6 h | priority queue + I/O (mostly cached) |
| 6 | Resolver drain | `EnumerateResolved` 3-at-a-time | ~1 h | sequential read |
| 7 | GSPO bulk sort | `ExternalSorter` on 24-byte records | ~3 h | external sort I/O |
| 8 | Rebuild | iterate GSPO, project to GPOS + trigram | ~6 h | tree traversal + writes |

## Disk artifacts and lifetime

| Path | Peak at 21.3B | Lifetime |
|---|---|---|
| `bulk-tmp/sorted-vocab/occurrences/chunk-*.bin` | ~4 TB | parser-end → merge-end |
| `bulk-tmp/sorted-vocab/assigned-ids-resolver/*` | ~340 GB | merge → drain-end |
| `bulk-tmp/gspo/chunk-*` | ~700 GB | drain → GSPO-write-end |
| `atoms.atoms` (growing during merge) | ~80 GB | persistent |
| `atoms.offsets` (growing during merge) | ~36 GB | persistent |
| `gspo.tdb` (B+Tree) | ~700 GB | persistent |
| `gpos.tdb` + `trigram.hash` + `trigram.posts` | ~600 GB | persistent |

**Peak total intermediate: ~5 TB** during the overlap window where chunks, resolver, and GSPO bulk-tmp coexist. Persistent total: ~1.5 TB final substrate.

## Two architectural principles visible from the flow

1. **Vocabulary precedes triples.** Atom IDs are assigned only after the entire vocabulary is sorted and merged. Triple ingestion is two-phase: collect atom-occurrence positions during parse → resolve positions to IDs after merge → drain ID-tuples to GSPO. This is why `FlushToDisk` is so heavy and why partial completion is non-trivial — the resolver maps positions to IDs that don't exist until merge has run.

2. **Each stage produces and consumes a different on-disk shape.** Parser produces bytes-keyed chunks; merge produces ID-keyed resolver records; drain produces ID-tuple records; GSPO sorter produces sorted triples. The disk-volume churn during ingest is structural to "vocabulary first, then resolve, then sort triples." The "what's on disk now" question has six different answers depending on which stage is active.

## Cost-attribution corollaries

These are observations the flow makes obvious, each tied to a limits-register entry:

- **Spill blocks parser** (stage 4 — `spill-blocks-parser.md`): the per-chunk sort runs on the parser thread. Pipelined spill (worker thread + double buffer) eliminates this without changing per-stage work. Sort:write ratio measured at 12-16 :1 across scales.
- **Intermediate disk volume** (stage 4 + stages 6-7 simultaneously — `external-merge-intermediate-disk-pressure.md`): peak ~5 TB at 21.3B. Sets the floor on host class.
- **MergeAndWrite was opaque** (stage 5 — `observability-coverage-gap.md`): closed by cycle 7 instrumentation; the merge phase now emits per-N-records progress and final `merge_completed` event.
- **AtomStore prefix compression** (stage 5 — Resolved by ADR-034 Round 2): 53% reduction at 1M, projected ~75 GB recovered at 21.3B.
- **File-pool sizing** (stage 5 — auto-sized per chunk count, capped at 32K): prevents the FD-ceiling crashes seen in cycles 1 and 4.

## What this map does NOT cover

- **Cognitive profile** — uses HashAtomStore, WAL-backed, mutable. Different stages, different artifacts. A sibling map would be useful when Cognitive bulk-load returns to active development.
- **Query path** — separate flow (parsing → planning → execution → output). The store this document describes is the substrate the query path reads from.
- **Bitemporal layer** — Reference profile is `HasTemporal: false` per ADR-029. Cognitive bulk-load adds temporal indexes that this map omits.

## References

- ADR-029 — store profiles and capabilities
- ADR-030 — bulk-load + rebuild split (Decision 5: Reference rebuild populates GPOS + trigram from GSPO-only bulk output)
- ADR-033 — bulk-load radix external sort
- ADR-034 — SortedAtomStore for Reference (Phase 1: in-memory build; Phase 1B: disk-spilling external merge sort; Round 2: prefix compression)
- ADR-036 — BZip2 streaming decompression (Phase 2: parallel bz2 — convert path only)
- `docs/limits/spill-blocks-parser.md` — sort-blocking-parser cost
- `docs/limits/external-merge-intermediate-disk-pressure.md` — peak intermediate disk volume
- `docs/limits/observability-coverage-gap.md` — instrumentation discipline that made stage 5 measurable
- `docs/architecture/technical/observability-coverage.md` — the broader instrumentation map
- `src/Mercury/Storage/SortedAtomBulkBuilder.cs` — stage 3-4 code surface
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — stage 4-5 (`SpillOneChunk` + `MergeAndWrite`)
- `src/Mercury/Storage/QuadStore.cs` — orchestration across all stages
