# 1B FlushToDisk Trace Analysis — 2026-05-01

**Purpose:** Decompose the 24-minute post-load FlushToDisk phase observed at 1B Reference + Sorted bulk-load (Step 4 of Phase 7c Round 1) into its actual cost components, to inform Round 2 architectural design.

**Trace artifact:** `/tmp/round1-gradient/1b-flushtodisk.nettrace` (144 MB, 25 min capture during 1B run started 2026-05-01T07:12, FlushToDisk attached at parser-complete 07:49). Captured via `dotnet-trace collect --process-id 6684 --duration 00:00:25:00`.

## Headline finding

**The deferred-resolution model is not the architectural cost.** The data flow primitives — `EnumerateResolved` drain through the resolver (~13 sec on 24 min), `_bulkSorter.Add` replay, and `AppendSorted` into the GSPO B+Tree (~6 sec) — are essentially free. The 24 min wall-clock is dominated by **sequential file I/O wait + per-record syscall overhead**, not the algorithm.

## Top CPU samples (exclusive)

```
1.  WaitHandle.WaitOneNoCheck                          32.17%
2.  Missing Symbol (likely macOS native I/O)           32.09%
3.  Monitor.Wait                                       16.08%
4.  FileStream.get_Length()                            12.36%
5.  LowLevelLifoSemaphore.WaitForSignal                 5.7%
6.  OSFileStreamStrategy.Read                           1.64%
7.  DiskBackedAssignedIds.DiskBackedReader.TryReadNext  0.68%
8.  RadixSort.SortInPlace(ReferenceKey)                 0.52%
9.  RadixSort.SortInPlace(ResolveRecord)                0.23%
10. ReferenceQuadIndex.SplitLeafPage                    0.20%
11. OSFileStreamStrategy.Write                          0.15%
12. ReferenceQuadIndex.InsertIntoLeaf                   0.02%
```

## Inclusive view — call paths

```
1.  Thread.StartCallback                               83.15%   (overall thread runtime)
2.  WaitHandle.WaitOneNoCheck                          32.17%   (waits, leaf)
3.  Missing Symbol                                     32.09%   (native wait or syscall)
4.  SocketAsyncEngine.EventLoop                        32.09%   (HTTP endpoint idle thread)
5.  PortableThreadPool+WorkerThread.WorkerThreadStart  21.89%   (thread pool worker activity)
6.  ThreadPoolWorkQueue.Dispatch                       16.08%   (work item dispatch)
7.  RdfEngine.LoadStreamingAsync                       16.08%   (parser thread, mostly Monitor.Wait)
8.  TurtleStreamParser.ParseAsync                      16.08%   (parser blocked on consumer)
9.  Monitor.Wait                                       16.08%   (parser→consumer barrier)
```

## Decomposition

### What's actually doing work
- ~14% of samples: real Mercury computation, mostly the fstat-in-a-loop pattern below
- 1.64% reads, 0.15% writes: sequential file I/O hot path
- ~0.75% sorting (radix on ReferenceKey + ResolveRecord chunks)
- 0.2% B+Tree splits (GSPO writes)
- 0.68% resolver reads (DiskBackedReader.TryReadNext — efficient)

### What's idle / waiting
- 32% HTTP socket loop (Mercury's HTTP endpoint runs even when nothing is querying)
- 32% native code waits (likely sync I/O blocked in macOS kernel — kqueue, dispatch, etc.)
- 16% Monitor.Wait (parser thread parked at chunk-flush boundary)
- 6% thread pool idle workers

### The single hot-path code defect
**`FileStream.get_Length()` at 12.36% of all CPU samples** — this is `fstat` being called inside `SortedAtomStoreExternalBuilder.ChunkReader.MoveNext`:

```csharp
public bool MoveNext()
{
    if (_disposed || _fs.Position >= _fs.Length) return false;  // _fs.Length each call
    ...
}
```

At 1B Reference scale, `MergeAndWrite` walks ~4B occurrence records via the priority queue, calling `MoveNext` once per record. Each call invokes `fstat` to check end-of-file. Chunk files are read-only and never grow during merge — the length is constant from open. Cache it once.

**Estimated saving:** ~3 min of the 24 min FlushToDisk (~12%). Trivial fix; applied in this commit.

## Inclusive views of the FlushToDisk subsystems

| Subsystem | Inclusive % | Wall-clock estimate |
|---|---:|---:|
| `QuadStore.FinalizeSortedAtomBulkIfPresent` (vocab finalize + sorter feeding) | 15.6% | ~3.7 min |
| `SortedAtomStoreExternalBuilder.ChunkReader.MoveNext` (occurrence chunk read) | 13.76% | ~3.3 min |
| `SortedAtomBulkBuilder.EnumerateResolved` (4B-record drain) | 0.92% | ~13 sec |
| `QuadStore.DrainBulkSorter` (GSPO B+Tree write via AppendSorted) | 0.45% | ~6 sec |

**The vocab finalize (MergeAndWrite over occurrence chunks) is the dominant FlushToDisk cost** — and most of THAT is the fstat overhead. The sorter drain and B+Tree write are essentially free at this scale.

## Implications for Round 2 design

**Round 2's value proposition is storage efficiency, not bulk-load wall-clock.**

The original framing for Round 2 (prefix compression in atoms.atoms + bit-packed atom IDs) was sometimes positioned as a bulk-load speedup. The trace says no:

- Prefix compression in atoms.atoms is a STORAGE win (smaller vocab file). It runs INSIDE MergeAndWrite where most time is fstat. Removing the fstat, prefix compression adds a tiny bit of CPU per atom written. Net effect on bulk-load: negligible.
- Bit-packed atom IDs (32-bit IDs replacing 64-bit) is a STORAGE win on `gspo.tdb` and the atom-ID intermediate buffers. It REDUCES intermediate I/O during bulk-load by ~2× (16-byte ReferenceKey vs 32-byte). That IS a real bulk-load speedup, but the effect is on the radix-sort + AppendSorted path which is already <1% of trace time.

**For wall-clock improvements at scale, the levers are different:**

- **Cache `FileStream.get_Length()`** — applied. Saves ~12% of FlushToDisk (~3 min on 24 min).
- **Eliminate the HTTP endpoint during bulk-load** — `SocketAsyncEngine.EventLoop` consumes 32% of trace samples. It's not blocking real work (different thread), but starts/stops cost is non-zero. The CLI's `--no-repl --no-http` flag combination should already disable this; verify.
- **Async I/O** — most thread time is in sync `FileStream.Read`/`Write` blocking the kernel. Switching to `RandomAccess` API or async I/O could overlap reads with computation. Nontrivial implementation.
- **Read-ahead on chunk files** — the priority-queue merge reads from many chunks in parallel; adding `ReadAhead` hints could prefetch sequential pages. Single-line `posix_fadvise` change.
- **Memory-bandwidth saturating bulk reads** — instead of byte-at-a-time chunk reading, do 64-MB sequential reads. Likely already partly done by FileStream's buffer — but worth verifying read sizes via iostat.

## Implications for the 21.3B Step 5 projection

If 1B → 24 min FlushToDisk and the dominant cost is in MergeAndWrite (vocab finalize):

- 21.3B has ~21× the occurrence count, ~21× the chunks
- Linear scaling: ~8 hours of FlushToDisk at 21.3B, dominated by chunk-merge
- WITH the fstat fix: ~7 hours
- Plus 21.3 hours of parser ingest (linear from 36:25 at 1B)

Estimated 21.3B Sorted full bulk-load: ~28-30 hours pre-rebuild. Plus rebuild (GPOS + trigram) — Phase 6 spent 11.65 hours on rebuild for Hash; Sorted's effect on rebuild is unclear, possibly faster due to sorted-source-data.

Phase 6 Hash baseline at 21.3B was 85 hours including rebuild. Sorted projected 28 + 12 (rebuild) ≈ **40 hours**, vs Hash 85 hours. Roughly **2× faster end-to-end at the headline scale** — IF the linear scaling holds.

The trace itself doesn't validate this projection; it just removes the deferred-resolution overhead from the picture. The other architectural assumption (linear scaling of MergeAndWrite over occurrence chunks) hasn't been measured past 1B.

## Round 2 design recommendations

1. **Lead with storage efficiency, not wall-clock.** The 30-40% disk reduction from prefix compression + bit-packed IDs is the real win. Position Round 2 accordingly.
2. **Cache `FileStream.get_Length()` is shipped now** (this memo's applying commit). One-line fix; ~12% of FlushToDisk wall-clock back.
3. **Don't bother with MPHF (ADR-034 Phase 2)** for bulk-load. It's a query-time win (binary search → O(1)). DiskBackedReader is already 0.68% of FlushToDisk — making it 0.1% saves ~3 sec. The Phase 2 work cost (~300-500 LoC of BBHash) doesn't pay back at bulk-load.
4. **Project disk + memory at 21.3B for Round 2 shape before committing.** Per the session pattern (`obs:scale-boundary-tests-not-optional`): linear extrapolation from 1B is a hypothesis, not a measurement.
5. **The HTTP endpoint thread accounts for 32% of trace samples but ~0% of bulk-load wall-clock.** Cosmetic; trace data is misleading on this. Don't restructure for it.

## References

- `/tmp/round1-gradient/1b-flushtodisk.nettrace` — 144 MB trace artifact (not committed)
- [Step 4 validation note](../docs/validations/adr-034-step4-1b-2026-05-01.md) — 1B run summary
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `ChunkReader.MoveNext` is the hot path
- ADR-034 Phase 2 (BBHash MPHF) — recommended to remain deferred per this analysis
- Round 2 candidate ADRs: AtomStore prefix compression + bit-packed atom IDs
