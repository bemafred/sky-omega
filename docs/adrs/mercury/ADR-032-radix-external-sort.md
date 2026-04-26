# ADR-032: Radix External Sort for Index Rebuild

## Status

**Status:** Completed — 2026-04-26 (shipped 1.7.39–1.7.42 across Phases 1-4: RadixSort primitive, ExternalSorter, GPOS rebuild, trigram rebuild). 100 M Reference rebuild 511 s → 48.64 s (10.5× faster), peak 2,463 MB/s sustained sequential write. Validated end-to-end through 21.3 B Wikidata in Phase 6. See [`adr-032-phase3-gpos-radix-2026-04-22.md`](../../validations/adr-032-phase3-gpos-radix-2026-04-22.md), [`adr-032-phase4-trigram-radix-2026-04-22.md`](../../validations/adr-032-phase4-trigram-radix-2026-04-22.md), and the [21.3 B query validation](../../validations/21b-query-validation-2026-04-26.md) confirming GPOS index correctness at full scale.

## Context

[Phase 5.2 dotnet-trace + iostat measurement](../../validations/adr-030-phase52-trace-2026-04-21.md) established that secondary index rebuild is **disk-I/O-bound by access pattern, not by CPU and not by raw bandwidth.** The 100M Reference rebuild sustains 100-300 MB/s of disk activity (peak 327 MB/s), which is ~7% of the Apple Silicon internal NVMe's sequential bandwidth (~5 GB/s) and ~2% of its random IOPS ceiling (~500K). The SSD has large headroom; the rebuild is not using it.

The 10M warm-cache measurement (run immediately after a cold rebuild) produced **identical disk I/O to the cold run** — caching does not help. This established that the cost is structural to the rebuild's access pattern, not a one-time cold-load.

The disk I/O measured is roughly **3× the rebuild's useful work**: 10M store on disk is ~1.1 GB; useful rebuild work (read GSPO + write GPOS + write trigram) is ~600 MB; actual disk I/O during the active rebuild was ~1.7 GB. That ~3× factor is the signature of B+Tree random-insert write amplification — each leaf page is read, modified, and written every time a key destined for that page arrives. Trigram has the equivalent problem on hash-bucket posting list pages.

[ADR-030 Phase 3](ADR-030-bulk-load-and-rebuild-performance.md) introduced sort-insert specifically to eliminate this write amplification for GPOS — sorted keys mean each leaf is touched exactly once. The implementation worked correctly but did not improve wall-clock at 100M, because the sort itself was as expensive as the write-amplification savings:

- `Array.Sort` with `IComparable<ReferenceKey>` on 32-byte keys → ~2.67 B comparisons at 100M
- `List<ReferenceKey>` growth across 99M entries → repeated array doubling and copy
- 3.2 GB monolithic buffer (32 B × 99M) → memory bandwidth pressure, GC visibility
- Drain phase serialized against the producer — lost the parallelism the broadcast architecture provided

[Phase 5.2's A/B trace](../../validations/adr-030-phase52-trace-2026-04-21.md) further established that the parallel rebuild + sort-insert architecture (1.7.36 + 1.7.37) generated allocation pressure (453 s of finalizer-thread work at 100M) and lock chatter (552 s of `Monitor.Enter_Slowpath`) that 1.7.34 had zero of. Both reverted in 1.7.38.

The *concept* of sort-insert is correct. The execution was wrong on three dimensions:
- **Sort algorithm.** `Array.Sort` with a comparator on fixed-width composite keys is the wrong tool. Comparator overhead dominates.
- **Buffer strategy.** A monolithic in-memory buffer requires the working set to fit in RAM, with `List<T>` growth and `ToArray` copies along the way.
- **Coverage.** Sort-insert was applied to GPOS only. Trigram — which also random-amplifies on its hash-bucket posting lists — was untouched, and is in fact the larger I/O cost.

This ADR addresses all three. The hardware is fixed (Apple Silicon, soldered NVMe, no add-in cards or RAID); the only lever is changing the access pattern from random to sequential. **Convert hits ~5 GB/s on this same hardware because it is sequential.** Sort-insert applied correctly should converge the rebuild's disk pattern toward the same ceiling.

### Why this is an ADR, not "swap the sort function"

Each of the three changes is an architectural decision with non-trivial implications:

- **Radix sort** has different invariants from comparator sort. It requires a fixed-width key representation and a byte-extraction function; it is not stable across in-place reordering passes unless implemented carefully; it has different performance characteristics on partially-sorted vs random inputs.
- **External streaming** changes the failure model. Temp files on disk must be cleaned up on crash, on Dispose, and on explicit cancel. Disk space must be reserved. The k-way merge is its own subsystem.
- **Trigram sort** is a structural change to a previously random-write code path. It needs its own sort key (`(trigram_hash, atom_id)` rather than `ReferenceKey`), its own append phase (batch-insert into posting lists rather than per-atom append), and its own correctness tests (ordering invariant on posting lists must be preserved by the bulk path).

None of these are rollback-cheap once integrated. ADR scope.

## Decision

### 1 — Radix sort for the 32-byte `ReferenceKey`

`ReferenceKey` is `[Graph: long][Primary: long][Secondary: long][Tertiary: long]` packed sequentially, 32 bytes, sorted MSB-first lexicographically (Graph → Primary → Secondary → Tertiary). This is exactly the structure radix sort is designed for: fixed-width, byte-addressable, no comparator needed.

**Algorithm:** least-significant-digit (LSD) radix sort with 8-bit digits and stable bucketing.

```
input:  ReferenceKey[] data of size N
output: data sorted lexicographically by Graph→Primary→Secondary→Tertiary

processing order (LSB to MSB across the full 32 bytes):
  Tertiary bytes 7,6,5,4,3,2,1,0  (8 passes)
  Secondary bytes 7,6,5,4,3,2,1,0 (8 passes)
  Primary bytes 7,6,5,4,3,2,1,0   (8 passes)
  Graph bytes 7,6,5,4,3,2,1,0     (8 passes)
total: 32 passes worst case
```

**Per pass:**
- Histogram: count occurrences of each byte value (256 buckets) in this position.
- Prefix sum: convert counts to write offsets.
- Distribute: write each entry to its bucket's offset in an output buffer; advance offset.
- Swap input ↔ output buffer.

**Optimizations applicable to Mercury's atom IDs:**

- **Skip zero-only passes.** Atom IDs are positive `long` values typically using 30-40 bits. The high 3-5 bytes of each field are zero across all entries. Detect with a histogram on the first row; if 256 buckets collapse to bucket 0, skip the pass. Realistic effective passes: 12-16 instead of 32.
- **16-bit digits where dense.** For the low 4 bytes of each field where most variation lives, optionally process two bytes per pass (65536 buckets, half the passes). Trades buckets-per-pass overhead against pass count. Probably not worth the complexity until measured.
- **Branchless write-offset update.** Each distribute step is a tight loop: `output[offsets[byte]++] = entry`. No comparisons, no branches except loop bounds. Highly cache-friendly when the buffer fits in L2.

**Measured performance (Phase 1, microbenchmark vs `Array.Sort` with comparator):**

| N | `Array.Sort` | `RadixSort` | Speedup |
|---|---:|---:|---:|
| 100K | 16.6 ms | 3.7 ms | 4.4× |
| 1M | 55.2 ms | 34.8 ms | 1.6× |
| 10M | 690.3 ms | 313.4 ms | 2.2× |

Both algorithms allocate zero managed bytes. The 1M speedup narrows because the working set (32 MB) is just past L2 cache, so memory bandwidth dominates both algorithms equally; at 10M and above the radix sort's linear scaling pulls ahead. The 100M sort projects to ~3.1 s (linear extrapolation) vs the 80.6 s measured for `Array.Sort` in the actual rebuild context — about a **20× difference at scale**, the gap widening beyond pure microbenchmark conditions because the rebuild's `Array.Sort` paid additional comparator-delegate overhead.

The relevant framing: the radix sort is fast enough that **sort time stops being load-bearing in the rebuild**. Phase 5.2 measured sort as ~14% of the 100M rebuild; after radix, sort drops to <1%. The remaining wall-clock cost belongs to the I/O pattern, which Phases 2-4 address.

### 2 — External streaming, not a monolithic buffer

The 1.7.37 implementation buffered all 99M GPOS keys in a single 3.2 GB array (`List<ReferenceKey>` grown via doubling, then `ToArray`). At 21.3 B Reference, that approach wants a 680 GB buffer — impossible.

**External merge-sort structure:**

1. **Read phase.** Stream the GSPO scan in chunks of `C` entries (tunable; baseline 16M entries = 512 MB). For each chunk:
   - Compute the GPOS key for each entry from the GSPO entry.
   - Radix-sort the chunk in place.
   - Write the sorted chunk to a temp file (`{store}/rebuild-tmp/gpos-chunk-{N}.bin`).
2. **Merge phase.** Open all chunk files. K-way merge using a binary min-heap of `(currentKey, chunkReader)` entries. For each pop: emit to the GPOS B+Tree's `AppendSorted`; advance that chunk's reader; push the next entry back into the heap. Continue until all readers are exhausted.

**Memory budget:**
- One sort buffer in memory at a time: 16M × 32 B = **512 MB**.
- K-way merge readers: one read-ahead buffer per chunk. At 100M / 16M = ~7 chunks × 1 MB read-ahead = **~7 MB**.
- Total transient: **~520 MB** vs the 1.7.37 path's 3.2 GB monolithic buffer.

At 21.3 B: 21.3B / 16M chunks = ~1330 chunks. K-way merge of 1330 streams via single-level heap is manageable (logarithmic comparisons per pop). If the open file count becomes problematic, hierarchical merge: merge groups of 64 chunks into 64 super-chunks, then merge those — two-level merge with at most 64 open files at any time.

**Temp file lifecycle:**
- Created under `{store}/rebuild-tmp/` directory at rebuild start.
- Deleted individually as each chunk is fully consumed by the merge.
- The directory is removed on rebuild completion.
- On crash or cancellation: `QuadStore.Open` checks for `rebuild-tmp/`; if present, the prior rebuild was incomplete, so the secondary index files are invalid (already true today — the contract `StoreIndexState != Ready` covers this). Cleanup deletes the temp directory and re-runs rebuild.

### 3 — Apply the same pattern to the trigram index

Trigram rebuild today is fully random-write-amplified: every atom that contains literals is decomposed into trigrams, each trigram is hashed to a bucket, and the corresponding posting list page is read + appended + written. With 10M atoms producing ~30M trigram entries scattering across ~1M buckets, the same posting list page is touched many times — exactly the same write amplification problem as GPOS, never addressed.

Sort-insert applies identically:

**Trigram sort key:** `[Hash: uint32][AtomId: long]` = 12 bytes. Sort by `Hash` first (so all atoms targeting the same posting list arrive contiguously), then by `AtomId` (so each posting list ends up sorted within itself, a useful invariant for read-time iteration).

**Sort + append flow:**
1. Stream the GSPO scan, extracting `(Hash, AtomId)` pairs for every literal atom's trigrams.
2. Chunk-sort with the same radix-sort + external-merge structure as GPOS.
3. Append phase: walk the merged sorted stream. For each unique `Hash`, batch-append all its `AtomId` entries to the corresponding posting list in one operation (one read of the list head, one allocation if growth needed, one sequential write of all new entries).

**Expected I/O drop:** Today, M trigram entries scatter across K buckets, with each bucket page read + written `M/K` times on average — write amplification factor `M/K`. At 10M atoms × 3 trigrams/atom / 1M buckets ≈ 30× amplification. After sort-insert, each bucket page is read once and written once — amplification factor 1.

The trigram path is the larger I/O cost in absolute terms (the 1.7.34 trace shows trigram inclusive time at 448 s vs GPOS at 76 s). Fixing trigram is therefore the higher-leverage change of the two.

### 4 — Single-thread architecture preserved

This ADR does not reintroduce parallelism. The Phase 5.2 trace established that parallel rebuild on this workload trades compute for overhead (allocation pressure, lock chatter, threadpool churn) without measurable wall-clock benefit on this hardware. Radix sort + external streaming runs on a single thread per index; GPOS rebuild and trigram rebuild run sequentially.

If parallelism becomes worthwhile in the future, it should be reconsidered against the **post-radix** baseline, not the pre-radix baseline. The cost/benefit math will be different once write amplification is gone — possibly the sort phase becomes the new bottleneck and a parallel radix would help, possibly not. That decision belongs to a future ADR with measurement.

### 5 — `AppendSorted` contract retained from ADR-030 Phase 3

The `ReferenceQuadIndex.AppendSorted(in ReferenceKey key)` API and its B+Tree append behavior were correct in ADR-030 Phase 3 — they were the consumers of the sorted stream, not the sort itself. The reverted-from-1.7.37 code path includes that method's removal; it should be reintroduced as part of this ADR's implementation, with the same contract:

- Caller guarantees keys arrive in **non-decreasing** order per the `ReferenceKey` comparison.
- Implementation appends to the rightmost leaf; allocates new leaves on the right edge as needed.
- Internal-node construction is bottom-up at end-of-run (or periodic).
- DEBUG-mode assertion: each appended key compares ≥ the previous; failure is a hard exception.
- RELEASE: contract violation is undefined behavior (the tree may be corrupted).

This contract was already validated in 1.7.37 — the three tests added in that commit (`AppendSorted_QueryResultsMatchRandomInsert`, `AppendSorted_DuplicateKey_IsSilentNoOp`, `AppendSorted_LargeMonotonicRun_ExceedsLeafAndPromotes`) should be reintroduced unchanged.

### 6 — Radix sort as a Mercury-internal primitive, caller-owned scratch buffer

The radix sort implementation lives in `src/Mercury/Storage/RadixSort.cs` (or similar). It is exposed only as `internal` — Mercury's external API surface does not gain a sort function. Two internal entry points:

```csharp
internal static class RadixSort
{
    // Sort data in place. scratch must be the same length as data; the
    // sort uses it for ping-pong between passes and leaves it in
    // unspecified state on return. No allocations inside the sort.
    internal static void SortInPlace(Span<ReferenceKey> data, Span<ReferenceKey> scratch);

    internal static void SortInPlace(Span<TrigramEntry> data, Span<TrigramEntry> scratch);
}
```

Internal because: (a) the API surface is intentionally minimal per [ADR-003](ADR-003-buffer-pattern.md); (b) the radix sort is tightly coupled to `ReferenceKey` and `TrigramEntry` byte layouts and isn't a general-purpose sort. If a third caller emerges later, it can be promoted then.

### 7 — Memory management — caller-owned buffer, zero allocations in the sort

The chunk-sort scratch buffer is the largest transient allocation in this design and the one most relevant to Mercury's zero-GC discipline. It must not be allocated per chunk.

**Ownership rule:** the rebuild allocates the scratch buffer **once at rebuild start**, passes it as a `Span<T>` to `RadixSort.SortInPlace` for every chunk, and releases it (drops the reference) at rebuild end. One allocation, one collection — both in Gen 2 / LOH, both single events.

**Sizes:**
- GPOS rebuild: `ReferenceKey[16_000_000]` = 512 MB on the LOH for the rebuild duration.
- Trigram rebuild: `TrigramEntry[16_000_000]` = 192 MB on the LOH for the rebuild duration.
- The two rebuilds run sequentially (per Section 4); their buffers do not overlap in memory at any moment. After GPOS rebuild completes and the buffer is released, the trigram rebuild allocates its own.

**Why direct allocation over `ArrayPool<T>.Shared`:**

`ArrayPool<T>.Shared` rounds up to the next power-of-two bucket size. A 16M `ReferenceKey` request (512 MB) would receive a 1 GB array — wasted memory. The shared pool also has a default maximum bucket size that 512 MB exceeds, so the rent path falls back to fresh allocation each time. Direct `new ReferenceKey[N]` gives an exactly-sized array, lives in LOH for the rebuild, collected once.

If a use case emerges where the sort runs frequently rather than once per rebuild, a dedicated `ArrayPool<T>` instance with an appropriately-configured bucket size becomes the right tool. For the rebuild path that runs once and dominates wall-clock, the simpler "allocate once, reuse, release" pattern matches the shape of the work.

**Inside the sort: zero allocations.**

The 256-bucket histogram per pass is `stackalloc uint[256]` (1 KB on the stack — well below stack-overflow limits per [ADR-009](ADR-009-stack-overflow-mitigation.md)). The prefix-sum write-offset array is the same. No heap allocation occurs inside `SortInPlace`. This is verifiable via the existing zero-allocation test infrastructure (per CLAUDE.md's `Allocation` test category).

**Buffer lifetime in the external-merge phase:**

The k-way merge in Section 2 streams entries from temp files. Each chunk reader uses a small read-ahead buffer (~1 MB) to amortize syscall cost. These buffers are allocated as `byte[]` per chunk reader at merge start, held for the merge duration, released when the reader is exhausted. At 100M / 16M = ~7 chunks × 1 MB = ~7 MB total — small enough not to require pooling.

## Consequences

### Positive

- **Eliminates the dominant I/O cost.** Write amplification ~3× → ~1× useful, projecting wall-clock proportional to disk-bandwidth utilization for the actual write volume. The 100M Reference rebuild's 559 s should drop substantially — the question is just by how much (validate, don't predict).
- **Memory budget shrinks 6×.** 3.2 GB monolithic buffer → 520 MB streaming. At 21.3 B, the difference is "fits in 128 GB" vs "impossible." This unblocks scale that the 1.7.37 design could not reach.
- **Trigram I/O collapses by ~30×** (write amplification factor at 10M scale). This is the larger of the two index costs and the larger of the two ADR wins.
- **Sort time drops 10×.** Comparator-free byte-bucketing + skip-zero-passes optimization gets the in-memory sort down to a trivial cost. Sort is no longer load-bearing.
- **Architectural simplicity preserved.** Single-thread, no broadcast channels, no GC pressure, no lock chatter. The 1.7.34 baseline is preserved with one new internal primitive added.
- **Radix sort composes with future trigram queries.** Posting lists end up internally sorted by `AtomId`, which enables efficient set-intersection during multi-trigram queries (a future read-side optimization).

### Negative

- **Temp disk space required.** Worst case the temp files contain the full sorted chunk set, equal in size to the eventual GPOS index. 100M Reference: ~4 GB temp; 21.3 B: ~850 GB temp. The store directory must have at least 2× the eventual index size in free space during rebuild. Mercury's current `--min-free-space` default is 100 GB for bulk; rebuild's free-space check needs to follow the same discipline.
- **Crash-recovery surface area grows.** A crash mid-rebuild leaves temp files. The recovery path (described in Section 2) is straightforward but is new code that must be tested.
- **Radix sort is a fixed-width-key algorithm.** It does not generalize to variable-width keys. If a future profile introduces a non-fixed-width key type, the radix sort path won't apply and that path will need a different sort. (`TemporalKey` is already wider than `ReferenceKey` but is also fixed-width; the algorithm will work with a different byte count.)
- **Two distinct sort code paths** — one for `ReferenceKey` (32 B), one for `TrigramEntry` (12 B). Generic-over-keysize is possible but the simplicity cost of two specialized implementations is small. Worth choosing simplicity here.

### Risks

- **Radix sort correctness on signed `long` fields.** Atom IDs are positive `long` but `long` is signed. Naïve byte-bucketing on the high byte treats negative numbers as larger than positive, breaking lexicographic order. Mitigation: flip the high bit during byte extraction (standard "biased radix" trick), unflip on read. Document explicitly. Test with deliberately negative keys even if production data won't have them.
- **External merge correctness at scale.** A 1330-way merge at 21.3 B is correct but unexercised. Mitigation: gradient validation at 1M / 10M / 100M / 1B before 21.3B; correctness check at each scale (every key in input appears in output, in sorted order, exactly once or zero times depending on dedup).
- **Temp file cleanup on Dispose paths.** If `QuadStore.Dispose` runs mid-rebuild (e.g. process killed cleanly), the temp directory needs to be cleaned up or marked for cleanup-on-next-open. Mitigation: temp directory deletion in the rebuild's `finally` block; on-open check for orphan temp directory.
- **The trigram batch-append might exceed posting list page size.** If a single trigram has more atoms than fit in a single posting list page, the batch-append needs to allocate an overflow page. Today's per-atom random-insert path handles this incrementally; the batch path needs equivalent overflow handling. Mitigation: extend overflow allocation to accept a count parameter (allocate enough pages up-front, fill sequentially).

## Implementation plan

**Phase 1 — Radix sort primitive**
- `RadixSort.SortInPlace(Span<ReferenceKey>, Span<ReferenceKey>)` — LSD bytewise, signed-long handling, skip-zero-passes optimization, `stackalloc` histograms.
- `RadixSort.SortInPlace(Span<TrigramEntry>, Span<TrigramEntry>)` — same algorithm, 12-byte key.
- Unit tests: random input, sorted input, reverse-sorted input, all-same input, deliberately-negative input, edge cases (size 0, 1, 2).
- Allocation test: `SortInPlace` produces zero managed allocations across a sort of 1M entries (caller-owned scratch is the only allocation, made by the test).
- Microbenchmark: at 1M, 10M, 100M, against `Array.Sort` with comparator. Expect ~10× speedup.

**Phase 2 — External merge-sort wrapper**
- `ExternalSort<T>` taking a stream of T and an in-place sort function; produces a sorted stream.
- Temp-file management with crash recovery.
- K-way merge via binary min-heap.
- Tests: equivalence of sorted output to in-memory `Array.Sort` on the same input, at 1M and 10M (large enough to exercise multiple chunks). Crash-injection test: kill mid-merge, verify recovery on next open.

**Phase 3 — GPOS rebuild integration**
- Reintroduce `ReferenceQuadIndex.AppendSorted` (the sort-insert API from reverted 1.7.37; tests included).
- Replace the random-insert GPOS rebuild loop with: stream GSPO scan → chunked radix sort to temp → external merge → `AppendSorted`.
- Validation: 100M Reference rebuild, expect wall-clock drop and disk I/O drop (measured via `iostat` per Phase 5.2 protocol).

**Phase 4 — Trigram rebuild integration**
- New trigram rebuild path: extract `(Hash, AtomId)` from GSPO scan → chunked radix sort to temp → external merge → batch-append per posting list.
- Tests: query equivalence vs the existing per-atom random-insert path on representative datasets.
- Validation: 100M Reference rebuild with trigram, expect substantially larger wall-clock drop than GPOS-only (trigram is the larger I/O contributor).

**Phase 5 — Gradient and full-Wikidata validation**
- 1M / 10M / 100M / 1B Reference rebuild with both indexes via radix path.
- Compare to 1.7.38 baseline at each scale.
- 21.3 B Reference end-to-end (bulk + rebuild). Target: combined wall-clock < 24 hours.

**Phase 6 — ADR transitions**
- Status Proposed → Accepted after Phase 3 + Phase 4 pass correctness tests at 100M.
- Status Accepted → Completed after Phase 5 publishes 21.3 B benchmark.
- Document any emergent issues for follow-up.

## Open questions

- **Chunk size.** 16M entries × 32 B = 512 MB is a reasonable default. Should it be tunable per host (function of available RAM)? Or fixed and well-tested? Lean toward fixed initially, parameterize if a real-world need emerges.
- **Radix digit width.** 8-bit digits give 256 buckets per pass and 32 worst-case passes. 16-bit digits give 65536 buckets and 16 passes. The L1/L2 cache sweet spot for the bucket-counter array is probably 8-bit (256 × 8 B = 2 KB fits in L1). Validate with a microbenchmark before optimizing.
- **Should `AppendSorted` enforce sortedness in RELEASE builds?** Today's reverted contract is DEBUG-only. The branch-per-entry cost in RELEASE is real but small (~1ns) and the safety win is large (a contract violation corrupts the tree silently). Worth re-measuring in this rebuild context — if the radix path is correct, the assertion will never fire in production.
- **Trigram posting list overflow batching.** Today's random-insert path handles overflow page allocation incrementally. The batch path could pre-compute the overflow count (count of `AtomId`s for each `Hash`, divide by page capacity, allocate that many pages up front). Worth measuring whether pre-allocation reduces fragmentation.
- **Should the scratch buffer be revisited as a `MemoryMappedFile` instead of LOH?** The 512 MB LOH allocation is fine for a single rebuild but if the rebuild becomes part of an interactive workflow (rather than a once-per-deploy operation), persistent fragmentation of the LOH could matter. An mmap-backed scratch region would avoid the LOH entirely. Defer until measurement shows it's worth the complexity.
- **Does the same pattern apply to the (future) Cognitive profile rebuild?** Cognitive has `TemporalKey` (wider than `ReferenceKey`, includes valid-time bounds) and four secondary indexes (GPOS, GOSP, TGSP, Trigram). The radix sort generalizes to wider fixed-width keys; the external merge structure is reusable. Whether the parallelism question reopens for Cognitive is a future ADR concern, after this ADR ships and Reference numbers are in.

## References

- [ADR-029](ADR-029-store-profiles.md) — Reference profile + 32-byte `ReferenceKey` definition
- [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) — original sort-insert + parallel rebuild ADR; Phase 2 + Phase 3 reverted in 1.7.38
- [Phase 5.2 trace + I/O validation](../../validations/adr-030-phase52-trace-2026-04-21.md) — write amplification measurement, A/B against 1.7.34, motivation for this ADR
- [Phase 5.1.b validation (parallel rebuild neutral)](../../validations/adr-030-phase2-parallel-rebuild-2026-04-21.md) — historical, superseded by Phase 5.2
- [Phase 5.1.c validation (sort-insert neutral)](../../validations/adr-030-phase3-sort-insert-2026-04-21.md) — historical, superseded by Phase 5.2
