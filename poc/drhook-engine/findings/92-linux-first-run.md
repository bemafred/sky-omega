# Finding 92 — First Linux run: the dbgshim lookup, the launch spawn and the test harness were macOS-only

**Date:** 2026-10-25
**Status:** Root causes **PROVEN** on zbook (Ubuntu 26.04.1 LTS, x86_64, glibc 2.43, Ubuntu's distro-built .NET SDK
10.0.113); **fixed**. First Linux data point for [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md)
Phase 9.

## Symptom

On zbook, some DrHook integration tests "hung". Run one test at a time with a 90 s limit, the picture split in two:

- **Every test failed in under 2 s** with `DllNotFoundException: libdbgshim not found`.
- With `DBGSHIM_PATH` forced to the bundled `linux-x64` library, 13/16 passed — every **attach** test. The 3
  **launch** tests (`CaptureState_AtBreakpoint…`, `LaunchInitialBreakpoint_IsRemovableById`,
  `SpanLengthCondition…`) hung until killed. Mid-hang the test host had **no child process**, and its main thread was
  in state `R` with no wait channel: spinning, not blocked.
- Each VSTest test left a `dotnet test` → `vstest.console` → `testhost` tree behind, reparented to PID 1
  (18 after one pass).

## Root causes

### 1. dbgshim was looked up by the distro RID

A distro-built .NET reports a distro RID: `RuntimeInformation.RuntimeIdentifier` and MSBuild's
`NETCoreSdkRuntimeIdentifier` are both `ubuntu.26.04-x64`. The `Microsoft.Diagnostics.DbgShim.*` packages ship only
portable RIDs (`linux-x64`, …). So:

- `DbgShim.Resolve` probed only `runtimes/ubuntu.26.04-x64/native/` and the matching NuGet-cache folder — nothing.
- `DrHook.Engine.csproj` chose its package by `$(NETCoreSdkRuntimeIdentifier)`, so no package matched. The library was
  in bin only because `DrHook.Mcp` references every RID's package.

### 2. `PosixSpawn` encoded the Darwin ABI, and silently corrupted memory on glibc

Checked against zbook's own headers (`/usr/include/spawn.h`, `bits/signum-arch.h`; sizes by compiling `sizeof()`):

| What the code assumed (Darwin) | glibc 2.43 | Effect on Linux |
|---|---|---|
| `posix_spawnattr_t` is a pointer (8 bytes, held in an `nint` local) | a 336-byte struct | `posix_spawnattr_init` writes 336 bytes into 8: **stack corruption** — the spin |
| `posix_spawn_file_actions_t` is a pointer (8 bytes) | an 80-byte struct | the same, 80 into 8 |
| `0x0080` = `POSIX_SPAWN_START_SUSPENDED` | `0x80` = `POSIX_SPAWN_SETSID` | the child is not held at all |
| `SIGCONT = 19` | `SIGCONT = 18`; 19 is `SIGSTOP` | "continue" would stop the child |

glibc has no suspended-spawn flag at all.

### 3. The VSTest group kill relied on bash semantics of `/bin/sh`

`TargetSpawn.Vstest` runs `set -m; dotnet test … &` under `/bin/sh` to give the run its own process group (finding 90),
then kills the group. On Ubuntu `/bin/sh` is **dash**, which refuses `set -m` without a terminal ("can't access tty;
job control turned off") and leaves the job in the caller's group. The group kill then targets a pgid that does not
exist (ESRCH) and the run's tree survives. Reproduced on the Mac with `/bin/dash`.

### 4. Two VSTest tests assumed finding 91's early-attach ordering

On zbook the attach usually lands **after** the testhost reached its `VSTEST_HOST_DEBUG` hold, so the hold's `Break`
fires. `PauseDisposeTest` (VSTest) expected its first stop to be its own Pause; `InformationalFloodTest` (VSTest)
assumed a target that produces no stops and saw the dispose-while-stopped `WorkerSilentBreak`. Finding 91 had noted
"the other five VSTest tests already drive their own Pause or do not wait on a first stop", which held for the Mac's
ordering only.

## Fixes

1. **Portable-RID lookup.** `DbgShim.CandidateRids()` tries the reported RID, then the portable one
   (`DbgShim.PortableRid()`: OS family — `win` / `osx` / `linux` / `linux-musl` — plus process architecture). The
   csproj selects packages by `$(NETCoreSdkPortableRuntimeIdentifier)` (`linux-x64` on zbook, `osx-arm64` on the Mac).
2. **One spawn routine, one implementation per platform.** `PosixSpawn.SpawnRedirected` holds what is common (pipes,
   file actions, `posix_spawnp`) over attribute storage the platform declares **exactly**:
   - `DarwinSpawn`: the types are opaque pointers; `POSIX_SPAWN_START_SUSPENDED`; `SIGCONT` 19. Behaviour unchanged.
   - `LinuxSpawn`: `SpawnAttr` / `SpawnFileActions` declared field for field from glibc 2.43's `spawn.h` (including
     `__cgroup`, new since older glibc). The child holds itself: `/bin/sh -c 'kill -STOP $$; exec "$0" "$@"'`; DrHook
     waits until `/proc/<pid>/stat` shows `T`, arms `RegisterForRuntimeStartup`, then sends `SIGCONT` 18. `exec` keeps
     the pid (and the start time dbgshim keys its startup semaphores on), so the target runs nothing before the
     callback is armed — the same guarantee as Darwin's suspend flag.
   - musl: its types have not been measured, so launch on musl is refused with an explicit error. Attach is unaffected.
   - `PosixSpawnTests` pins the sizes (336 / 80 glibc, pointer-sized Darwin) and runs a real held spawn: held
     (state `T` on Linux), runs only after `Continue`, and keeps its pid through `exec`.
3. **bash for the group spawn.** `TargetSpawn.Vstest` starts `/bin/bash`, which gives the job its own group with or
   without a terminal.
4. **Both orderings handled.** The two VSTest tests consume the hold's `Break` if it arrives first, as
   `ConcurrentPauseStopTest` already did.
5. **A hang is now a failure.** Each integration test has a 120 s limit (`testconfig.json`, `mstest.timeout.test`).
   The engine's native calls have no timeouts of their own, which is how the launch spin hung the whole run.

## Validation

- **zbook:** engine units 167/167; integration suite **70 full runs**: 69 × 16/16, 0 leaked processes; then the 3
  launch tests alone **20 × 3 = 60/60**.
- **Mac (macOS 27, M5):** engine units 167/167; integration suite 5 × 16/16, 0 leaked processes. Solution builds with
  0 warnings.

## Follow-up: the per-test timeout leaked the abandoned test's targets

Found right after `162fc43`, by a process check on the Mac: an MTP target 56 minutes old, still waiting on `--debug`.
When MSTest abandons a test at its timeout, the test's own `using` / `finally` cleanup never runs, so whatever it
spawned outlives the run: an MTP target with `--debug`, a VSTest testhost under `VSTEST_HOST_DEBUG` (both wait for a
debugger forever), or BorrowedIdleDetachTest's idle target. Reproduced on both machines by setting the limit to
300–1500 ms. The timeout turned a hang into a failure but made it leak.

**Fix:** every spawn the harness makes runs in its own process group (bash `set -m`; the MTP spawn and
BorrowedIdleDetachTest now do what the VSTest spawn already did), and records it in `SpawnedProcessGroups`. Its
`[AssemblyCleanup]`, which still runs after an abandoned test, SIGKILLs every recorded group. **Validated:** the
abandon-at-timeout repro leaks nothing on either machine (3 spawn shapes × 3 limits); full suite 5 × 16/16 on each,
0 leaked processes.

The leak check itself had a blind spot worth recording: it matched `testhost`/`vstest` only, so MTP targets and the
`dotnet`-named snapshot target never showed up. The checks now match every target the harness spawns.

## Open

- **One unreproduced failure.** In run 3 of the first five full runs on zbook, `CaptureState_AtBreakpoint` got no
  entry-module hold stop within 20 s. It did not recur in the next 65 full runs or the 60 solo launches. The test now
  fails with the target's state and every event the engine received, so a recurrence is diagnosable from the failure
  alone.
- **musl launch** stays refused until its types are measured on a musl system.
- **The .NET 10 `dotnet test` break** (MTP via the VSTest bridge) is separate and unchanged: run the integration-test
  executable directly (DRHOOK.md).
