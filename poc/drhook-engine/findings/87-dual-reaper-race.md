# Finding 87 — Two reapers on one pid: the in-process debugger PAL races the BCL for a `Process.Start` child, and the host FailFasts

**Date:** 2026-10-03
**Status:** Root cause **PROVEN** (observed with a `wait4` interposer); **fix IMPLEMENTED + validated** —
harness topology corrected, engine guard (`AnomalyKind.TargetIsHostChild`) added, constraint documented.

## Symptom

`DrHook.Engine.IntegrationTests` intermittently died mid-run (2 of 14 runs at first sight) with

```
Process terminated.
Error while reaping child. errno = 10
   at System.Environment.FailFast(System.String)
   at System.Diagnostics.ProcessWaitState.TryReapChild(Boolean)
   at System.Diagnostics.ProcessWaitState.CheckChildren(Boolean, Boolean)
   at System.Diagnostics.Process.OnSigChild(Int32, Int32)
```

`errno = 10` is `ECHILD`: the BCL went to reap a child it started with `Process.Start` and the child had
already been reaped by someone else — which the runtime treats as unrecoverable. In the captured instance
an earlier test had *also* failed, with `MTP target did not exit naturally within 5s after Dispose`.

## Root cause (proven)

**Two reapers poll the same pid.**

1. The integration harness started each MTP target with `Process.Start` — the target was a **direct child**
   of the test host, so the BCL tracks it and reaps it on `SIGCHLD`.
2. Once DrHook attaches, the **CoreCLR PAL inside the in-process debug components** (the interposer
   attributes the calls to `libmscordaccore.dylib`; `libdbgshim.dylib` also imports `waitpid`) polls
   `wait4(pid, WNOHANG)` for exit detection — and, the target being our child, **reaps it** when it wins.

Whichever polls first after exit wins. When the BCL wins, the PAL's next poll gets `ECHILD` and handles it
quietly. When the **PAL** wins: the BCL never observes the exit (so `bootstrap.WaitForExit(5000)` times out
— the "did not exit naturally" failure is **the same bug**), its stale child record survives, and at the next
`SIGCHLD` (another child exiting, tests later) `wait4(pid)` → `ECHILD` → `Environment.FailFast`.

**Evidence** — a `DYLD_INSERT_LIBRARIES` interposer over `waitpid`/`wait4`/`waitid` logging the caller's
image (`87-wait-interposer.c`; lldb hung at process launch and was abandoned). Crash reproduced on run 8 of
the instrumented loop:

```
wait4 host=32421 pid=32422 opt=1 ret=0      img=libmscordaccore.dylib   (x923 — polling)
wait4 host=32421 pid=32422 opt=1 ret=32422  img=libmscordaccore.dylib   ← PAL REAPS the host's child
   … two tests later …
wait4 host=32421 pid=32422 opt=1 ret=-1 errno=10 img=libSystem.Native.dylib  ← BCL: ECHILD → FailFast
```

A clean run shows the reverse order (`libSystem.Native` reaps, `libmscordaccore` gets `ECHILD`). VSTest
targets were never affected: the attached testhost is a child of `dotnet test`, not of the host, so the
PAL's polls simply get `ECHILD` (~738 per run, harmless).

## Exposure

- **`drhook-mcp`: not exposed today.** DrHook launches via `posix_spawn` (not BCL-tracked), and nothing in
  `src/DrHook.*` uses `Process.Start` for a debuggee. A PAL reap of an untracked child is harmless.
- **Exposed: any host that `Process.Start`s a target and then debugs it in-process** — the integration
  harness (until this fix), and future embedders (an ADR-015 run-until-anomaly driver, James, a test runner
  that spawns and debugs). The PAL reaper is inherent to ICorDebug on Unix; DrHook cannot remove it.

**Constraint:** *a process hosting DrHook.Engine must not debug a target it started via
`System.Diagnostics.Process`.*

## Fix

1. **Harness topology** (`tests/DrHook.Engine.IntegrationTests/TargetSpawn.cs`): MTP targets run under a
   `/bin/sh -c '"$0" "$@" & wait $!'` parent. The shell is the BCL-tracked child; the target is the shell's
   child (never `exec`'d into the host's child slot); the shell's `wait` makes its exit mirror the target's,
   so `WaitForExit` / `Kill(entireProcessTree)` keep their meaning. Same shape VSTest already had, and the
   shape of real attach (the debuggee is never the debugger's child).
2. **Engine guard** (`DebugSession.SurfaceHostChildReapRace`, called by `Attach` + `AttachAndOwn`): if the
   target's OS parent is the debugger host (`Interop.ProcessParentage.ParentOf` — `proc_pidinfo
   PROC_PIDTBSDINFO` on macOS, `/proc/<pid>/stat` on Linux), emit **`AnomalyKind.TargetIsHostChild`**.
   Surfaced, not refused: the engine cannot tell whether the BCL tracks the child (a `posix_spawn` child is
   safe). `Launch` is unaffected (its own `posix_spawn`).
3. **Docs**: DRHOOK.md pitfalls + the `AnomalyKind` XML doc.

## Validation

- `ProcessParentageTests` (3): a `Process.Start` child's parent is this process; self's parent is another
  live pid; a reaped child yields null. `DrHook.Engine.Tests` 159/159.
- `AnomalyInjectionTest` MTP case asserts **no** `TargetIsHostChild` under the corrected topology.
- **Probe 87** (`87-host-child-reap-race-smoke.cs`): direct `Process.Start` child → exactly one
  `TargetIsHostChild` (`target 34661 is a direct OS child of the debugger host 34660`); `/bin/sh`-parented
  → zero.
- **Repeat-run gate — PASSED (2026-10-03):** 50 consecutive full integration runs under the interposer,
  **50/50 green (15/15 each), zero BCL `ECHILD`** (before the fix: 2 FailFasts in 14 runs, 1 in 8 under the
  interposer). The PAL still reaped 150 children (3 per run) — every one a `posix_spawn` launch the BCL never
  tracked: **zero overlap** between PAL-reaped pids and pids the BCL (`libSystem.Native`) ever waited on.
  That is the safe case, confirming the guard's "surface, don't refuse" choice.

## References

- `src/DrHook.Engine/Interop/ProcessParentage.cs`, `src/DrHook.Engine/DebugSession.cs`
  (`SurfaceHostChildReapRace`), `src/DrHook.Engine/EngineAnomaly.cs` (`TargetIsHostChild`).
- `tests/DrHook.Engine.IntegrationTests/TargetSpawn.cs`, `tests/DrHook.Engine.Tests/ProcessParentageTests.cs`.
- `poc/drhook-engine/87-wait-interposer.c`, `poc/drhook-engine/87-host-child-reap-race-smoke.cs`.
- Related: `PosixSignals.ReapChild` (the DetachLeaveRunning reaper) is *not* involved — the integration
  tests never detach-leave-running; code-read ruled it out before the interposer pinned the PAL.
