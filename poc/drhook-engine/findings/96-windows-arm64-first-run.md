# Finding 96 — first Windows run (arm64): the engine works; only the test harness was POSIX

**Date:** 2026-10-26
**Status:** **Observed** on omega-windows. Harness **fixed** for Windows. Part of [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 9 (Windows/arm64).

## Machine

omega-windows: Parallels VM, Windows 11 (build 26100) on arm64, 4 cores, 8 GB, NTFS. .NET SDK 10.0.302 (pinned by
`global.json`); `libdbgshim` from `Microsoft.Diagnostics.DbgShim.win-arm64`. The first time any DrHook code ran on
Windows.

## Observed

The integration suite at 1.8.18, unchanged:

| Tests | Result | Cause |
|---|---|---|
| 3 that start their target through `DebugSession.Launch` (`LaunchInitialBreakpoint_IsRemovableById`, `SpanLengthCondition_…`, `CaptureState_AtBreakpoint_…`) | **passed** | — |
| 12 that start their target through `TargetSpawn` (6 scenarios × MTP and VSTest) | failed in 0–18 ms | `Win32Exception`: cannot start `/bin/bash` |
| `BorrowedIdleDetachTest`, `DeletedExecutableListingTest` | skipped | POSIX-only by construction |

So on first contact the engine launched a target, bound and removed a breakpoint, compiled and evaluated a
condition without func-eval, and assembled a state snapshot on Windows arm64. Every failure was in the harness.

## Why the harness was POSIX

`TargetSpawn` started every target under `/bin/bash` for two POSIX reasons:

- **Finding 87, the dual-reaper race:** a debuggee that is this host's `Process.Start` child is reaped by both the
  CoreCLR PAL's `wait4` and the BCL. Windows has no reaping, so the race does not exist there.
- **Finding 90, orphaned testhosts:** a run is killed by its process group, because a testhost orphaned while waiting
  for a debugger is no longer in the run's process tree. Windows has no process groups.

## Fix

- On Windows, targets are direct `Process.Start` children: the MTP target with `--debug`, or `dotnet test` with
  `VSTEST_HOST_DEBUG=1`.
- **`SpawnedProcessJob`** takes the place of the process group. The test host puts **itself** into a
  `KILL_ON_JOB_CLOSE` Job Object before the first spawn. A child joins its parent's job inside CreateProcess, so every
  process the harness starts, and every process those start, is a member from creation. Assigning each target after
  `Process.Start` instead would leave a window in which its own children escape: a race against the clock.
  - `KillAllButHost` kills every member except the host. That is exactly the current run, because the assembly is
    `[DoNotParallelize]`. It ends each VSTest run and runs at `[AssemblyCleanup]`.
  - If the host dies, closing its handle kills whatever is left in the job.
- **`SpawnedProcessJobTest`** checks finding 90's orphan case: `cmd` starts `ping`, `cmd` alone is killed, and the
  parentless `ping` must still end with `KillAllButHost`.

## Validation

- omega-windows: integration suite **16 passed, 2 skipped, 0 failed in 3 of 3 runs**, and no `testhost`, `vstest`,
  `dotnet` or target processes left afterwards.
- omega (macOS): 17 passed, 1 skipped (the new Windows-only test); the POSIX path is unchanged.

## Not yet covered on Windows

- `BorrowedIdleDetachTest` (finding 89): its target topology is a shell; a Windows variant is still to be written.
- The probe corpus (02–56) and the MCP server (`drhook-mcp`, whose rendezvous socket is AF_UNIX) on Windows.
- Known Windows gaps in the engine, from its own code (`DebugSession.cs`), not yet exercised here:
  - **Launch stdio is not isolated:** `CreateProcessForLaunch` gives the target the debugger's own console handles,
    so under `drhook-mcp` a launched target's `Console.WriteLine` would land on the MCP channel (the problem
    ADR-011 D2 solved for POSIX).
  - **Launch with environment overrides** throws `PlatformNotSupportedException`.
  - **`RequestExit`** (the graceful stop, SIGTERM on POSIX) reports an anomaly and returns false; the
    `GenerateConsoleCtrlEvent` path is unwritten.
- Windows x64.
