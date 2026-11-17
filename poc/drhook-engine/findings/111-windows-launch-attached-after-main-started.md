# Finding 111 — On Windows a launched program could start `Main` before DrHook had attached: the runtime does not wait for the debugger after dbgshim's startup callback

**Date:** 2026-11-16
**Status:** **Cause shown and fixed.** Reproduced on omega-windows, where zbook has had ssh access since 2026-11-16. The
cause was shown by an experiment that delays the attach, and read in dbgshim's source. Fixed in
`DbgShim.LaunchWithDebuggerWindows` and `DebugSession.Launch`. Why Linux and macOS wait for the attach is not
established.

## Summary

`DebugSession.Launch` gets an `ICorDebug` from dbgshim's runtime-startup callback and attaches with it afterwards, on
the launching thread. dbgshim holds the target's runtime only while its callback runs. DrHook's callback signalled and
returned at once, so the runtime was free before DrHook attached.

- **On Linux and macOS** the runtime still waited for the attach: a 1 s delay before it changed nothing.
- **On Windows** it did not wait. When the attach lagged, `Main` was already running, and the attach got mscordbi's
  "fake" `LoadModule` events. The entry-module hold-gate then stopped on a fake one, after `Main` had begun. A
  breakpoint set at that hold missed every call already made.

That is the Windows failure of `CaptureState_AtBreakpoint_AssemblesSelfContainedSnapshot`, "local doubled (beat=1:
n*2) should be 2; got 4", seen in CI on Windows arm64 (run 42175386825, 2026-11-06) and Windows x64 (run 43740903571,
2026-11-15) and left open.

The launch now attaches while the callback holds the runtime. The test failed 7 of 80 runs on omega-windows before the
fix and 0 of 90 after.

## Reproduced

omega-windows: Windows 11 build 26200, arm64, 4 cores, .NET runtime 10.0.15. The test alone, at `499d28e2`:

| Runs | Failed | `doubled` was |
|------|--------|---------------|
| 30 idle | 4 | 4 each time |
| 30 with 4 busy loops | 1 | 4 |
| 20 idle (the experiment build, no delay) | 2 | 4, 4 |

The target calls `Worker.Compute(beat, …)` once per beat, every 50 ms, and the test breaks at `SNAPSHOT_HERE` in it.
`doubled` is `beat * 2`, so 4 means the first hit was beat 2.

## The experiment

Experiment commit `50353a5d` (`499d28e2` plus a `Thread.Sleep(DRHOOK_EXP_ATTACH_DELAY_MS)` between the launch helper
and `FromCordbg`, in both platform branches). It was never on `main` and is kept in `~/diag/attach-exp.bundle` on zbook.

| Machine | Delay before the attach | Failed | `doubled` was |
|---------|-------------------------|--------|---------------|
| omega-windows | 0 | 2 of 20 | 4, 4 |
| omega-windows | 300 ms | 10 of 10 | 8 to 14 |
| omega-windows | 1000 ms | 5 of 5 | 18, 20, 20, 20, 18 |
| zbook (Linux x64) | 0, 300 ms, 1000 ms | 0 of 6 | — |
| macbook (macOS arm64) | 0, 300 ms, 1000 ms | 0 of 6 | — |

On Windows the later the attach, the later the first hit: the program runs on while DrHook has not attached. On Linux
and macOS a full second of delay changes nothing, so something there holds the runtime until the attach.

## What dbgshim does

`dotnet/diagnostics`, `src/dbgshim/dbgshim.cpp` (main, read 2026-11-16), `RuntimeStartupHelper::InvokeStartupCallback`
on Windows: it calls the debugger's callback, `m_callback(pCordb, m_parameter, S_OK)`, and on the way out:

```
// Wake up the runtime
SetEvent(clrRuntimeInfo.ContinueStartupEvent);
```

`Unregister` sets the same event. So dbgshim holds the runtime for exactly as long as the callback runs. A debugger
that attaches inside its callback, as netcoredbg does, attaches before the runtime goes on. DrHook's callback runs on
dbgshim's thread and by DrHook's own rule stays O(1) in stack and runs no user code (SUBSTRATE RULE 1, finding 55), so
it only signalled.

On POSIX dbgshim's callback is invoked through the runtime's platform layer (`PAL_RegisterForRuntimeStartup`). What
makes the runtime wait there for an attach made after the callback returned was not read.

## The fix

The callback holds the runtime until the launching thread has attached:

- **The callback** signals as before, and then, when asked to hold, waits on `StartupContext.Attached`, at most 15 s.
  A wait keeps it O(1) in stack and runs no user code. The attach still runs on the launching thread.
- **`LaunchWithDebuggerWindows`** takes `attachWhileHeld(pCordb, pid, exitWatch)` and calls it between the callback's
  signal and its release. It then sets `Attached`, and only after that unregisters. dbgshim's `Unregister` waits for its
  startup thread, which is held in the callback, so the other order would wait for the bound.
- **`DebugSession.Launch`, Windows branch:** `Process.GetProcessById` and `FromCordbg`, which includes
  `DebugActiveProcess`, run inside that delegate.
- **Failure:** if the attach throws, the target is ended as on any failed launch, and the caller releases the
  `ICorDebug`.
- **POSIX is unchanged:** it waits already.

Past the 15 s bound the runtime runs on and the attach happens late, as before the fix. An attach takes milliseconds,
so the bound only matters for a launching thread that never signals.

## Measured

The fix commit `bd1bccaf` (its code differs from this commit's only in the summary comment of `DebugSession.Launch`) on
omega-windows:

| Check | Runs | Failed |
|-------|------|--------|
| The test alone, idle | 60 | 0 |
| The test alone, 4 busy loops | 30 | 0 |
| `DrHook.Engine.IntegrationTests` (25, 3 skipped on Windows) | 2 | 0 |
| `DrHook.Engine.Tests` (230, 22 skipped on Windows) | 2 | 0 |

An experiment on top of the fix (`9a49039b`, kept in `~/diag/win-attach.bundle` on zbook) put the delay inside the held
window, before `DebugActiveProcess`:

| Delay inside the held window | Failed | Without the fix |
|------------------------------|--------|-----------------|
| 300 ms | 0 of 10 | 10 of 10 |
| 1000 ms | 0 of 5 | 5 of 5 |

So the hold, not the timing, keeps `Main` from starting first. The 1000 ms run also shows that `DebugActiveProcess`
works while dbgshim holds the runtime at its startup event, which the design depended on.

### Through `drhook-mcp`

2026-11-16, omega-windows. A PowerShell client (`win-mcp-launch.ps1`, in zbook's scratch directory) ran the built
`drhook-mcp` over stdio, as an MCP client does, with one server process per row. Each launch was a `drhook_launch` of
`dotnet DrHookSnapshotTarget.dll` with the initial breakpoint at `SNAPSHOT_HERE`, then `drhook_locals` for the argument
`n`, then `drhook_stop`. The first call has `n` = 1.

| Commit | Delay | Launches | Late | `n` was |
|--------|-------|----------|------|---------|
| `86af6a1b` (`main`, this fix) | none | 30 | 0 | 1 each time |
| `9a49039b` (the fix, the delay inside the held window) | 300 ms | 10 | 0 | 1 each time |
| `50353a5d` (no fix, the delay before the attach) | 300 ms | 5 | 5 | 7 each time |

So the hold works when the attach runs inside the MCP server's request, and the check sees a late attach when there is
one. No target was left running.

## What it meant for users

On Windows any `drhook_launch` could miss a breakpoint in code that runs in the program's first moments, and its
entry-module hold could stop after `Main` had begun. How often depended on how long the launching thread took from the
callback to `DebugActiveProcess`: here 1 in 10 to 1 in 30.

## Not tested

- **Windows x64.** Only omega-windows (arm64) was available; CI runs `windows-latest` (x64).
- **A deterministic regression test.** It would need a delay seam in the product. The `CaptureState` test fails about
  1 run in 10 on Windows without the fix, and CI runs it on both Windows runners on every push.
