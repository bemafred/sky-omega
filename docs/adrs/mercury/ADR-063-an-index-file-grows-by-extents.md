# ADR-063: An Index File Grows by Extents — A Page Is Mapped Once and Never Moves, and a Page Pointer Is Computed, Not Cached

## Status

**Status:** Proposed — 2026-11-14

Martin's word to fix the first defect below was given on 2026-11-14 ("Yes to both", to the question whether the
index file growth is fixed next). The fix is built and its tests pass on omega. The second defect was found while
the fix was prepared and is fixed by the same change. The status is Martin's to change.

Promoted from the limits register: [B+Tree index has no mmap remap](../../limits/btree-mmap-remap.md).

## Context

`BTreeFile` (`src/Mercury.Runtime/BTreeFile.cs`) is the file every B+Tree index of every profile lives in. Two
defects were measured in it on 2026-11-14, on omega (macOS 27, arm64), at `3df1b7e`.

### Defect 1: a file that outgrows its initial size kills the process

`AllocatePage` lengthened the file with `SetLength` when a page lay past its end. The mapping made when the file
was opened was never renewed, so the new page was addressed outside the mapping. `StorageOptions` says of the
index files: "Files grow automatically when capacity is exceeded." They did not.

Measured: a Cognitive store whose index files start at 1 MB took 5,000 quads in five batches, and the process died
in the sixth with `AccessViolationException` in `TemporalQuadIndex.SplitLeafPage` (exit 134). No exception can be
caught, so the rule of ADR-061 M7 (a store that cannot apply a committed write stops) never gets to run.

The default initial size is 1 GB per index (64 MB in `StorageOptions.ForTesting`). At 88 bytes per entry and pages
between half full and full, 1 GB holds between about 6 and 12 million quads. The live memory store on omega uses
1.4 MB of its 1 GB GSPO file, so it is far from the limit. Bulk mode starts an index at 256 GB (Cognitive, Graph),
1 TB (Reference) or 1 GB (Minimal), which is how the 21.3 billion triple loads stayed inside one mapping.

### Defect 2: readers at the same time are given wrong pages

`GetPagePointer` looked the page up in a `PageCache` and added it on a miss. `PageCache` had no lock. Readers of a
store hold `QuadStore`'s read lock, which they share, so several of them called `GetPagePointer` at once. What the
cache kept was a pointer computed as `base + pageId * PageSize`: it saved one multiplication and one addition.

Measured:

- **The cache alone.** Two threads that looked up and added 4,000 page ids got wrong pointers in 200 of 200 rounds,
  and about 490 page ids per round stayed mapped to another page's pointer. Eight threads: 2,000 of 2,000 rounds.
  One thread (control): none.
- **A store.** A Cognitive store of 60,000 quads was closed and opened again, and eight threads asked for random
  subjects under the read lock. The process never finished. It ran at 798 % CPU, and a DrHook snapshot showed all
  eight threads in `TemporalQuadEnumerator.MoveNext` and `CompareKeys`, the walk along the leaves. With one thread
  (control) the run finished and found every quad. The same construction as a test died with
  `AccessViolationException` in `MoveNext`.
- A first run in which the eight threads asked in the same sequential order found every quad. The defect needs two
  threads to miss on different pages at the same moment, which a store opened a moment ago makes likely.

A wrong pointer stayed in the cache until the file was closed. Whether a writer ever wrote through one is not
measured. That this explains the index differences seen in the live memory store is a hypothesis and nothing more.

The existing tests of concurrent reads use 100 quads, which is one or two pages per index, so they could not show
this.

## Decision

### D1. The file is mapped in extents, and an extent is never unmapped before the file is closed

The first extent is the whole file as it is when it is opened. When `AllocatePage` needs a page past the end, the
file is lengthened (doubled, at least, and to a whole number of pages) and the added part is mapped as one more
extent. The file is lengthened before the added part is mapped (ADR-020 section 4).

A page lies in exactly one extent and never moves. A page pointer is therefore valid until `Dispose`. This is the
property the trees need: `SplitLeafPage`, `SplitInternalPage` and the frames of `InsertRecursive` above them hold
page pointers while they ask for a new page, and go on writing through them.

A file opened again is one extent, however many it had when it grew.

### D2. A page pointer is computed

`GetPagePointer` finds the extent of the page and adds the page's offset in it. It reads no state that a reader
changes, so any number of threads may call it at once, also while a writer allocates a page: the extents are an
array that is replaced, never changed.

With one extent, which is every file that has not grown since it was opened, the search does not run and the
pointer is one multiplication and one addition. The file at least doubles each time it grows, so the extents stay
few.

`PageCache` is removed, with its tests. `BTreeFile.BasePtr` is removed: it had no caller, and with extents there is
no single base.

### D3. A flush covers every extent

`Flush`, `FlushPage` and the header writes flushed the one mapping, which was the whole file. They now flush every
extent, which is again the whole file.

### D4. `AllocatePage` uses up no page id when the file cannot grow

The page id was taken before the file was lengthened. It is now taken after, so an exception from lengthening or
mapping leaves the file as it was.

### D5. A file that is refused is closed again

When the constructor throws (another consumer's magic number, or the file cannot be mapped), the mapping and the
file are released. They were left open before.

### Not changed

The initial sizes of bulk mode stay as they are. They keep a bulk load in one extent. Whether they should be
smaller now that a file can grow is a separate question.

## Alternatives

- **Map the whole longer file again and unmap the old mapping**, as `HashAtomStore` and `TrigramIndex` do for their
  files. Every pointer held across `AllocatePage` would then be stale, so each tree would have to fetch its
  pointers again after every allocation, in four index classes, and a place that forgot would fail only at the
  moment a file grows.
- **Map the whole longer file again and keep the old mapping.** Pointers stay valid, but a page then has two
  addresses, and a flush of the new mapping would have to be shown to write what was changed through the old one
  on every platform.
- **Start every file so large that it never grows.** This moves the limit and keeps the death at it.
- **Give `PageCache` a lock.** It would make a lock or an interlocked operation part of every page access, to save
  an addition.

## Tests

`tests/Mercury.Tests/Storage/IndexFileTests.cs`. Before the fix, on omega:

| Test | Before the fix |
|---|---|
| `ReadersAtTheSameTime_AreEachGivenThePageTheyAskedFor` | failed: 153 pages were not the one asked for |
| `AFileThatOutgrowsItsInitialSize_HoldsEveryPage_AlsoWhenOpenedAgain` | the process died (`AccessViolationException`) |
| `AnInitialSizeThatIsNotAWholeNumberOfPages_LosesNoPageWhereTheFileGrew` | the process died |
| `AStoreThatOutgrowsItsIndexFiles_HoldsEveryQuad_AlsoWhenOpenedAgain` (Cognitive, Graph) | the process died |
| `ReadersOfAStoreAtTheSameTime_FindEveryQuad` | the process died in `TemporalQuadEnumerator.MoveNext` |
| `APointerTakenBeforeTheFileGrew_ReachesItsPageAfterwards` | passed; it holds D1 against a later change that would move a page |

After the fix all pass. The two probes were run again: eight readers finished and found their quad in 1,280,000 of
1,280,000 lookups, and the store that died at 5,000 quads took 60,000 with its GSPO file grown from 1 MB to 16 MB.

## Not tested

- Any platform but omega. The Linux and Windows runs are CI's. On Windows the offset of an extent is in general
  not a multiple of the 64 KB the system maps at; the code adds the accessor's `PointerOffset` for that, and no
  run has shown it yet.
- That a flush makes the pages of a later extent durable across a power loss.
- A growth that fails (a full disk, no address space).
- The Reference and Minimal profiles through a store: their stores are loaded in bulk mode, which starts the file
  at 1 TB and 1 GB. They use the same `BTreeFile`, which is tested alone.
- A file larger than 16 MB, and the cost of a flush over many extents.

## Consequences

- The promise in `StorageOptions` holds: an index file grows.
- The limits register entry is resolved.
- `Mercury.Runtime` loses a public type (`PageCache`) and a public member (`BTreeFile.BasePtr`), and
  `BTreeFile`'s constructor loses its `pageCacheCapacity` parameter. Nothing in the repository used them.
