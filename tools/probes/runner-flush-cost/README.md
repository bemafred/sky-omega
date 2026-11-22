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

## Results: run 45013340358 (`0b82d746`), 2026-11-22

The tables and reports each job wrote are in [`results-45013340358`](results-45013340358/).

### The runners

- **Windows x64** (`windows-latest`, Windows 10.0.26100, 4 cores): C: and D:, each a "Msft Virtual Disk" (SAS) of
  150 GB. The temporary directory is on C: (`C:\Users\runneradmin\AppData\Local\Temp`), the checkout on D:
  (`D:\a`). Defender's real-time protection is off as the runner is set up, and C:\ and D:\ are excluded.
- **Windows arm64** (`windows-11-arm`, Windows 10.0.26200, 4 cores): C: only, a "Msft Virtual Disk" of 256 GB, with
  the temporary directory and the checkout (`C:\a`). A "Microsoft NVMe Direct Disk v2" of 220 GB is attached, with no
  partition. Real-time protection is on and tamper-protected: `Set-MpPreference` was ignored, so the condition did not
  take and its jobs ran no numbers under its name. C:\ and D:\ are excluded.
- **Linux x64** (`ubuntu-24.04`, 4 cores): `/`, `/tmp` and `/mnt` on one disk, "MSFT NVMe Accelerator v1.0", ext4.

### store-ops, medians in ms (each condition's whole table is in the results)

| Operation | Linux | Win x64, C: | Win x64, D: | Win arm64, C: |
|---|--:|--:|--:|--:|
| file: append 100 B, flush to disk (a log's commit) | 0.25–0.33 | 3.30–3.70 | 0.32 | 4.77–4.96 |
| mapped, WriteThrough (an index): the next page, flush the view | 0.17–0.18 | 3.09–3.62 | 0.27 | 4.83–4.89 |
| mapped, no WriteThrough: the next page, flush the view | 0.16–0.19 | 2.09–2.45 | 0.18 | 3.14–3.29 |
| mapped, no WriteThrough: the same page, flush the view | 0.11–0.12 | 0.97–1.07 | 0.08 | 1.50–1.57 |
| mapped, no WriteThrough: the next page, flush the view, then the file to disk | 0.20–0.35 | 3.26–3.58 | 0.35 | 4.86–4.98 |
| file: create and delete an empty file | 0.01 | 0.32–0.52 | 0.14 | 0.27 |
| store: one insert (AddCurrent) | 1.44–1.71 | 16.5–17.9 | 1.56 | 24.8–25.5 |
| store: a batch of 100 inserts | 59–67 | 881–964 | 78 | 1,317–1,332 |
| store: create and close (ForTesting) | 5.6–7.0 | 70–77 | 7.9 | 101–107 |

The ranges are over the runs in the same temporary directory: on Windows x64 C: three (as set up, with protection
"off", which was already off, and again), on the others two.

### Mercury.Tests

| Runner, temporary directory | Mercury.Tests | The tests' durations added up |
|---|--:|--:|
| Linux x64, `/tmp` | 148 s | 505 s |
| Windows x64, D: | 415 s | 1,540 s |
| Windows x64, C: (as set up; and with protection "off", the same) | 2,256 s; 1,749 s | 6,301 s (the second) |
| Windows arm64, C: | 2,288 s | 8,657 s |

Every Windows run failed the same 400 tests, which fail at once: `SparqlResultComparer` appended to
`/tmp/compare_debug.log`, which on Windows is `\tmp` on the current drive, and there was none. CI did not see it,
likely because DrHook's tests, run with Mercury.Tests there, make that folder for their sockets
(`DebugStateServer.cs:134`, `/tmp/dh-*.sock`). The writes are deleted (`31b196bb`).

### The hypotheses

- **H1** holds on C: and nowhere else: a log's commit costs 3.3 to 5.0 ms there, ten to twenty times the Linux
  runner's, and one insert 16.5 to 25.5 ms. On D: and on Linux a flush is cheap.
- **H2** is refuted on Windows x64: real-time protection is off as the runner is set up, and both drives are excluded.
  On arm64 it could not be turned off, so it is not tested there; both drives are excluded there too.
- **H3** holds, and is the cause on Windows x64. With the temporary directory on D:, every store row costs what it
  costs on the Linux runner, and Mercury.Tests takes 415 s against 1,749 and 2,256 s on C:. The difference is in the
  classes that write stores: `IndexFileTests` 14%, `LoadExecutorStressTests` 10%, the W3C SPARQL suite 10%,
  `PruningTests` 8%, `CrashRecoveryTests` 7%; twenty classes carry 90%. That C: is a disk reached over a network and
  D: a local one is how Azure gives a machine its disks; it is not verified here.
- **H4** has a small effect, on Windows only: a new page's flush costs about 1.5 times as much with `WriteThrough`
  (C: on x64 3.1 against 2.1 ms, on arm64 4.9 against 3.3; D: 0.27 against 0.18). On Linux none.
- **H5** holds in part: on C: a view's flush alone costs about two thirds of one followed by `FlushFileBuffers` (2.1
  against 3.3 ms). It waits for its page writes, and, from .NET's source, does not flush the device's cache; that
  part is not measured.

### What is left

On D:, Windows x64 still takes 2.8 times the Linux runner's time (415 against 148 s), though the store's operations
cost the same. The tests that differ most grow index files, write one file from many threads, or wait on many
threads: `IndexFileTests` `ReadersOfAStoreAtTheSameTime` 88.9 against 14.8 s, `AtomStoreTests`
`Intern_HighCollisionLoad` 29.5 against 0.6 s, `JsonlMetricsListener_ConcurrentWrites_NoTornRecords` 28.1 against
0.1 s, `OrderedParallelPipelinePoCTests` `ManyWorkersFewItems` 23.6 against 0.0 s. Not measured further. In a
parallel run a test's duration includes its waits on the others, so these are where the time is, not what it is.
