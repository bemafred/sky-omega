# ADR-067: Index Pages Are Written Back Lazily — A Mapped Store Flushes Its Log at a Commit and Its Pages at a Checkpoint, and Repairs a Page a Power Loss Tore

## Status

**Status:** Proposed — 2026-11-21

Drafted by zbook on 2026-11-21, from the measurement of why Mercury.Tests takes up to 15 times longer on the Windows CI
runners (limit [mercury-tests-windows-duration](../../limits/mercury-tests-windows-duration.md), Triggered the same day:
Windows x64 stopped at its 90-minute limit in run 44918126706). Martin, the same day: "We must mind that the very
reason for using memory mapped files is performance. If we flush too often, we behave badly?", and "we must not loose
the ADR issue, it is very important".

This text states the problem, what is measured and what the field does. It decides nothing yet: the choice of what
Mercury promises under a power loss is Martin's (Q1).

## Why

Mercury maps its index files so that a write goes to the page cache and returns, and the operating system writes the
page back later: many writes to one page become one disk write. That is the reason for mapping the files.

A session write (cognitive mode, not bulk load) does the opposite. Each index flushes the whole of its file after every
page write, so every logical write waits on a device flush, and the page cache coalesces nothing. And the flushes do not
make a power loss safe: a page torn by a power loss is still possible, and nothing repairs it.

So Mercury pays the full price of synchronous I/O for its mapped files, and does not get power-loss safety for it.

## Context

### What a session write flushes (measured on zbook, 2026-11-21)

- `BTreeFile` opens an index file with `FileOptions.WriteThrough` (`BTreeFile.cs`, the constructor) and maps it.
  `FlushPage` flushes every extent of the mapping, the whole file, unless `DeferMsync` is set; the index classes call it
  after each page write (`TemporalQuadIndex`: insert, end, continue and remove a period; the Graph, Minimal and
  Reference indexes likewise).
- `CommitBatch` writes the log's commit and `fsync`s it, then applies the batch to the four indexes, one quad at a
  time: each quad's insert into each index flushes that index's file.
- The atom store is made durable before the commit that refers to it (ADR-058 D6): three flushes per commit.
- Counted with strace on ext4: a batch of 100 quads made 403 more `msync` calls than an empty batch, about 1.1 ms each,
  and the batch took 540 ms; 100 single inserts made 700 `msync` and 99 `fsync`. An `msync` with `MS_SYNC` is a
  device flush, as an `fsync` is.

| A write | Log `fsync` | Index flushes | Atom-store flushes | In all |
|---|---|---|---|---|
| One insert | 1 | 4 | 3 | 8 |
| A batch of N quads | 1 | 4N | 3 | 4N + 4 |

### What it costs (a micro-benchmark of store operations, medians: [`tools/probes/runner-flush-cost`](../../../tools/probes/runner-flush-cost/README.md))

| | zbook, tmpfs | omega-windows, VM disk | zbook, ext4 |
|---|---|---|---|
| One flush to disk | 0.00 ms | 0.16 ms | 1.9 ms |
| One insert | 0.05 ms | 0.9 ms | 9.1 ms |
| A batch of 100 inserts | 3.7 ms | 38 ms | 538 ms |
| Create a store | 0.5 ms | 4.6 ms | 26 ms |
| Reopen a store | 0.4 ms | 35 ms | 11 ms |

On ext4 over 99% of a batch's time is spent waiting on flushes: about 190 quads a second, where the page cache alone
allows over 25,000. Not measured: whether one flush of a large production index file costs more than one of a 64 MB
test file.

### What it does to the tests

- Mercury.Tests on zbook, the same commit (`c088d2d8`): 208 s with the temporary directory on tmpfs, 1,117 s on ext4.
  Twelve test classes carry 83% of the increase; `PruningTests` alone 38% (3.7 s to 347 s).
- On omega-windows, whose virtual disk flushes cheaply, the suite takes 3 min 35 s. On the CI runners: Linux x64 9 to
  13 min, Windows arm64 39 to 85 min, Windows x64 up to its 90-minute limit.
- The step in CI's Linux durations on 2026-11-14 (5 to 10 min) is `33e7701c`, ADR-061 P1. It did not make a write
  dearer (one insert 9.0 ms before and after, a batch of 100 421 and 519 ms); it added 48 tests that write and replay
  logs, 38 s on tmpfs and 108 s on ext4.

### History

- **April 2026, bulk load and rebuild** (1.7.9, 1.7.15, 1.7.20; `docs/validations/bulk-load-gradient-2026-04-17.md`,
  `full-pipeline-gradient-2026-04-19.md`). The same finding, that a flush covers the whole mapping and not a page. Bulk
  load and rebuild defer their flushes to one at the end (`DeferMsync`). Session writes kept a flush per page write on
  purpose: "cognitive-mode writes keep it false" (`BTreeFile.DeferMsync`). This ADR revisits that choice.
- **ADR-031** found that a 14-minute Dispose blamed on `msync` was the predicate statistics; flushing a mapping with no
  dirty pages is cheap.
- **ADR-058** (2026-11-04) made the log, `fsync`ed at each commit, and the checkpoint the durable record: the checkpoint
  persists the indexes and the atom store before it truncates the log (D1, D2), and an open replays the log (D4).
  Durability does not depend on shutdown (D5). The atoms are durable before the commit that refers to them (D6).

### What the flush per page write protects

- **A killed process** (SIGKILL, the out-of-memory killer, a crash): nothing. The page cache keeps every write to a
  mapped page, and the operating system writes it back. ADR-058's replay restores what the log committed.
- **A power loss or a kernel hang** (elite hung on 2026-10-31): pages that were written and not flushed may or may not
  be on disk, in any order. With a flush after each page write, an index on disk holds every operation up to the one in
  flight; that one may be torn, a split half written, for instance. The log's records name quads, not pages, so
  replaying them does not repair a torn page.
- **Neither is tested under a power loss.** ADR-058's recovery tests kill a process; none discards the page cache or
  tears a write.

### What the field does

The established design writes the log at a commit, writes the data pages back lazily, flushes them at a checkpoint, and
has a way to repair a page that a power loss tore (not measured in Mercury; each is the system's documented design):

- **PostgreSQL:** the log holds a whole page image the first time a page changes after a checkpoint
  (`full_page_writes`). Recovery starts every torn page from that image.
- **SQLite in WAL mode:** the log holds whole page frames; a checkpoint copies them into the database.
- **InnoDB:** the doublewrite buffer, a second copy of each page written before the page itself.
- **LMDB:** copy-on-write pages, never overwritten while live, and two meta pages; a commit flushes the new pages and
  then a meta page. There is no torn tree to repair.

Mercury has the first half of that design (a commit `fsync`s the log; a checkpoint persists the pages) and lacks the
second (a repair for a torn page). The flush per page write stands in for the missing half, and does not replace it.

## The directions

| | Batch of N | One insert | Under a power loss |
|---|---|---|---|
| A. As now: each page write | 4N + 4 flushes | 8 | A commit is on disk when it returns; the operation in flight may be torn, with no repair |
| B. Each index once per commit | 8 | 8 | As A, with the whole batch being applied as the window |
| C. Pages written back lazily, flushed at a checkpoint, a torn page repaired | 1 + the atoms' | 1 + the atoms' | Every committed write survives; a torn page is repaired from the log |
| D. As C without the repair | 1 + the atoms' | 1 + the atoms' | Any index page written since the checkpoint may be torn: rejected |

C is the design the field uses and the one the mapped files are for. Its repair has three known shapes (Q2): page
images in the log (PostgreSQL, SQLite), a second copy of each page (InnoDB), or pages that are never overwritten while
live (LMDB). B is a step that costs little and changes no promise of substance; it is not the answer to Martin's
question, since a single insert still waits on eight flushes.

## Open

- **Q1.** What Mercury promises under a power loss: today, nothing tested. The answer sets the direction.
- **Q2.** C's repair: page images in the log, a second copy, or copy-on-write pages; with what each costs in log size,
  write amplification and code.
- **Q3.** The checkpoint: how often, and what flushing a checkpoint's dirty pages costs, once pages are written back
  lazily.
- **Q4.** The atom store's three flushes per commit (ADR-058 D6) under the same question: the atoms could be logged
  with the records that use them.
- **Q5.** Windows: what `FileOptions.WriteThrough` on a mapped file's handle does to `FlushViewOfFile` and to the
  memory manager's own writeback; measured, not assumed.
- **Q6.** The CI runners' own flush cost, and whether Defender adds to it: measured by a workflow started by hand,
  `.github/workflows/runner-flush-cost.yml`, with the hypotheses and their predictions in
  [`tools/probes/runner-flush-cost`](../../../tools/probes/runner-flush-cost/README.md).
- **Q7.** How a power loss is tested: a block device that records writes and replays them up to any point
  (dm-log-writes, as xfstests uses), or a virtual machine's disk snapshot. Mercury has neither.

## References

- [ADR-058](ADR-058-wal-replay-restores-consistent-store.md): the log replayed at an open; the checkpoint persists.
- [ADR-031](ADR-031-read-only-session-fast-path.md): the Dispose that was not `msync`.
- [ADR-063](ADR-063-an-index-file-grows-by-extents.md) D3: a flush covers every extent.
- [ADR-029](ADR-029-store-profiles.md): the profiles, bulk mode and `SetDeferMsync`.
- `docs/validations/bulk-load-gradient-2026-04-17.md`, `docs/validations/full-pipeline-gradient-2026-04-19.md`.
- [Limit: Mercury.Tests on the Windows runners](../../limits/mercury-tests-windows-duration.md).
