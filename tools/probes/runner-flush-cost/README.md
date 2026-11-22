# What makes Mercury.Tests slow on the Windows runners

Two probes and a workflow of 2026-11-22, for [ADR-067](../../../docs/adrs/mercury/ADR-067-index-pages-are-written-back-lazily.md)
Q6 (the CI runners' own flush cost, and whether Defender adds to it) and Q5 (what `FileOptions.WriteThrough` on a
mapped file's handle does to `FlushViewOfFile`), and the limit
[mercury-tests-windows-duration](../../../docs/limits/mercury-tests-windows-duration.md).

## The question

Mercury.Tests, run in parallel since `55d7b63d`, took these times in CI runs 44995443554 (`55d7b63d`) and 45010588311
(`fc4c7d00`), and these on the machines we have:

| Where | Mercury.Tests |
|---|---|
| Linux x64 and arm64 runners, macOS arm64 runner | 2 to 3.5 min (their whole job 3 to 4 min) |
| Windows x64 runner | its job 27 min (run 44995443554) |
| Windows arm64 runner | 46 min (run 44995443554) |
| zbook, temporary directory on tmpfs / on ext4 | 81 to 89 s / 316 s |
| omega-windows (Windows 11 arm64, a virtual machine) | 73 and 83 s |

So Windows is not slow in itself; something on the Windows runners is. A session write waits for disk flushes (8 for
one insert, 4N + 4 for a batch of N, ADR-067), and on zbook the suite's time follows a flush's cost. omega-windows's
flush costs 0.16 ms, probably because its hypervisor answers a flush from its own cache (not verified): it shows that
Windows is not slow, not what a flush that reaches a disk costs on Windows.

One thing read before the run, in .NET 10's source (`MemoryMappedView.Windows.cs`, `MemoryMappedView.Unix.cs`): a
mapped view's `Flush` is `msync(MS_SYNC)` on Linux and macOS, a flush to the device; on Windows it is
`FlushViewOfFile` alone, which writes the changed pages and does not flush the device's cache (ADR-058 notes it does
not reach the disk without `FlushFileBuffers`). `BTreeFile.FlushPage` is therefore not the same operation on the two
platforms. The index files are opened with `FileOptions.WriteThrough`.

## Hypotheses, and what each predicts (written before the run)

- **H1, a flush is dear on the runners' disks.** `file: append 100 B, flush to disk` and the `mapped ... flush the view`
  rows cost far more on the Windows runners than on the Linux runner; one insert costs about 8 of them; and in the
  suite the time is in the classes that write, as it was on zbook's ext4 (`PruningTests` and eleven others).
- **H2, Defender scans what the tests write.** With real-time protection off, the rows that create, open and delete
  files (`file: create and delete an empty file`, `store: create and close`, `store: delete ...`) cost much less, the
  flush rows about the same, and the suite takes much less time.
- **H3, the disk under the temporary directory.** With the temporary directory on the runner's other disk (D: on
  Windows x64; the arm64 runner has its workspace on C: and may have no D:), the flush rows cost much less.
- **H4 (Q5), WriteThrough makes a view's flush write through.** On Windows, `mapped, WriteThrough (an index) ... flush
  the view` costs far more than `mapped, no WriteThrough ... flush the view`. On Linux the two cost the same (zbook,
  ext4: 1.22 and 1.25 ms).
- **H5, on Windows a view's flush does not reach the disk.** On Windows, `mapped, no WriteThrough: the next page, flush
  the view` costs much less than the same with `then the file to disk`, and much less than a log commit.

The hypotheses are not exclusive. Each is refuted by its prediction failing: H2 if turning real-time protection off
changes neither the rows nor the suite's time beyond the repeat's spread.

## Running it

The workflow is started by hand: Actions, "Runner flush cost", Run workflow; or `gh workflow run runner-flush-cost.yml`.
Its `store-ops` jobs run `what-a-store-operation-costs.cs` on the Windows x64, Windows arm64 and Linux x64 runners,
four times each: as the runner is set up, with the temporary directory on the other disk, with Defender's real-time
protection off (Windows), and as set up again (the spread of a repeat). Its `suite` jobs run Mercury.Tests with an
xunit TRX report, as CI runs it and under each condition, and rank the tests with `the-tests-by-time.cs`. Each job's
summary has the tables; its artifact has them and the report.

Locally:

```bash
dotnet run tools/probes/runner-flush-cost/what-a-store-operation-costs.cs -- "zbook, ext4"     # TMPDIR picks the disk
dotnet test --project tests/Mercury.Tests --report-xunit-trx --report-xunit-trx-filename mercury-tests.trx --results-directory out
dotnet run tools/probes/runner-flush-cost/the-tests-by-time.cs -- out/mercury-tests.trx 30
```

## zbook, for reference (2026-11-22, `fc4c7d00`)

`TMPDIR` on ext4 (NVMe); on tmpfs every row is under 0.1 ms except the store's (a batch of 100: 3.8 ms).

| Operation | Median ms | p90 ms | n |
|---|--:|--:|--:|
| file: create, write 4 KB, flush to disk, close | 2.19 | 3.92 | 50 |
| file: create and delete an empty file | 0.04 | 0.06 | 200 |
| file: append 100 B, flush to disk (a log's commit) | 1.72 | 1.97 | 200 |
| mapped, WriteThrough (an index): the next page, flush the view | 1.22 | 2.04 | 100 |
| mapped, WriteThrough (an index): the same page, flush the view | 0.88 | 0.93 | 100 |
| mapped, no WriteThrough: the next page, flush the view | 1.25 | 2.21 | 100 |
| mapped, no WriteThrough: the same page, flush the view | 1.00 | 1.03 | 100 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 3.16 | 3.46 | 100 |
| store: create and close (ForTesting) | 27.33 | 37.97 | 15 |
| store: one insert (AddCurrent) | 9.59 | 10.27 | 100 |
| store: a batch of 100 inserts | 441.48 | 536.42 | 10 |
| store: SPARQL INSERT DATA of one quad | 10.08 | 11.20 | 50 |
| store: SPARQL SELECT of one subject | 0.02 | 0.03 | 200 |
| store: checkpoint | 10.96 | 12.05 | 10 |
| store: reopen and close (10 writes in its log) | 9.01 | 9.60 | 10 |
| store: delete a closed store's directory | 0.57 | 0.68 | 15 |
| pool: rent a used store (it is cleared), insert, return | 33.35 | 34.63 | 20 |

## Results

Not yet run.
