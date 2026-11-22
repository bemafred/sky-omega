# Limit: Mercury.Tests takes 15 times longer on the Windows runners, close to the CI job's limit

**Status:**        Triggered — 2026-11-21: Windows x64 stopped at its 90-minute limit in run 44918126706 (Mercury.Tests
                   1 h 28 min, no test failed)
**Surfaced:**      2026-11-20, from the durations of CI runs on `main` (below). sky-omega's Mercury handover noted the margin
                   the same day: Windows arm64 ran 87, 84 and 78 minutes of its 90.
**Last reviewed:** 2026-11-22
**Promotes to:**   [ADR-067](../adrs/mercury/ADR-067-index-pages-are-written-back-lazily.md) (Proposed), for the
                   flushing that makes a write cost disk time; and a change to the costliest tests, for CI now.

## Description

The CI test job runs every test project with `dotnet test`, with a 90-minute limit per job
(`.github/workflows/test.yml`). On the two Windows runners nearly all of a job's time is one project, Mercury.Tests:

| Run (commit) | Windows arm64 | Windows x64 |
|---|---|---|
| 44683948721 (`6f8a6be`) | 1 h 13 min 25 s | 50 min 3 s |
| 44735592602 (`38a2328`) | 1 h 24 min 35 s | 57 min 22 s |
| 44758951418 (`def7c99`) | 1 h 15 min 6 s | 49 min 13 s |
| 44801380410 (`485ed30`) | 1 h 21 min 59 s | 1 h 15 min 10 s |

In run 44801380410 the same project took 5 min 21 s on the macOS arm64 runner and 14 min 34 s on the Linux x64 runner,
and on zbook (Linux x64, local) it takes about 3 min. The other test projects take seconds to under a minute on every
platform. Windows arm64's whole job was 75 to 87 minutes in these runs.

## Why it is slow: measured 2026-11-21

A session write flushes each index's whole file after every page write (`BTreeFile.FlushPage`), so a batch of N quads
costs 4N + 4 device flushes and one insert 8. A test's writes therefore cost what a flush costs on the disk under the
temporary directory:

- On zbook, the same commit took 208 s with the temporary directory on tmpfs (a flush costs nothing) and 1,117 s on
  ext4 (1.9 ms a flush). Twelve test classes carry 83% of the increase, `PruningTests` alone 38%.
- On omega-windows, whose virtual disk flushes in 0.16 ms, the suite takes 3 min 35 s. Windows is not slow; the
  runners' disks make a flush dear. Their own flush cost is not measured yet (ADR-067 Q6).
- The step in CI on 2026-11-14 (Linux x64 5 to 10 min) is the 48 tests that ADR-061 P1 (`33e7701c`) added; the write
  path's cost did not change there.

ADR-067 records the measurements and the design question. What follows is the record as it stood on 2026-11-20.

## Why it is slow: not known (2026-11-20)

CI keeps no time per test: the workflow writes no test report, and the console names a test only when it fails or is
skipped. So which tests take the time on Windows, and why, is not measured. Read, and ruled out: the store's mapped
files are made sparse on NTFS before they are sized (`SparseFile`), so the size of a test store's files is not written
out. Candidates, none measured:

- The flush to disk at each commit of the write-ahead log, which is slow on Windows and may be slower on a CI runner's
  disk.
- Microsoft Defender scanning each file a test store creates under the runner's temporary directory.
- Creating and mapping a store's files on NTFS.

## Trigger condition

- A Windows job stops at its 90-minute limit, so the run fails with no test failed.
- Or tests are added that each open a store, in numbers that move Windows arm64 past about 85 minutes.

## Current state

Monitored by hand, from the job durations. Store tests are kept small: `05a2d8e` shrank tests of 100,000 quads to
7,000 after a timeout.

2026-11-22, two changes for CI now; ADR-067 stays open for the cause:

- `PruningTests.LargeTransfer_BatchesCorrectly` transfers 2,550 quads, not 25,000 (`041af4dc`): 337 s to 30 s on
  zbook's ext4.
- Mercury.Tests runs in parallel. It had run one test at a time since 2026-01-26 (`7c9975f4`), a stop-gap for a stack
  overflow that ADR-011 removed the same day. Measured in parallel, 0 failures: zbook 81 to 89 s on tmpfs (208 s
  serially) and 316 s on ext4 (1,117 s serially); omega-windows 73 and 83 s (215 s serially). Seven tests failed only
  in parallel, each for a reason of its own, and were fixed first. One of them found that on Windows a copy of a
  sparse file writes its whole length: a store the crash target left, under 1 MB allocated, was copied as 4.6 GB, and
  in parallel the copy failed for want of quota. Its stores now have small files.

## Candidate mitigations

The first is a measurement; the others depend on what it shows.

- **Measure each test's time on Windows:** Mercury.Tests with `--report-xunit-trx` on omega-windows (about 80 minutes
  there), or the same report written in CI for Mercury.Tests and kept with each run. Then rank the tests by time, and
  compare one store's open, commit and close on Windows and Linux.
- If Defender's scan is the cost: exclude the runner's temporary directory from it in the workflow.
- If tests that open many stores are the cost: fewer stores, smaller ones, or shared ones.
- Split Mercury.Tests across Windows jobs, or raise the limit. Either buys time; neither explains it.
- Not a candidate as it stands: a durability setting that only tests use. It would be a behaviour flag, and Martin's
  rule is no behaviour flags and no divergent paths.

## References

- The CI runs above, and `.github/workflows/test.yml`.
- [SparseFile](../../src/Mercury.Runtime/SparseFile.cs), and the measurement on omega-windows it records.
- `05a2d8e`, the tests shrunk after a timeout.
