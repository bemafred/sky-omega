# Limit: Mercury.Tests takes 15 times longer on the Windows runners, close to the CI job's limit

**Status:**        Monitoring
**Surfaced:**      2026-11-20, from the durations of CI runs on `main` (below). sky-omega's Mercury handover noted the margin
                   the same day: Windows arm64 ran 87, 84 and 78 minutes of its 90.
**Last reviewed:** 2026-11-20
**Promotes to:**   an ADR, or a fix in the tests, when a Windows job reaches its 90-minute limit, or before tests are
                   added that open many stores.

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

## Why it is slow: not known

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
