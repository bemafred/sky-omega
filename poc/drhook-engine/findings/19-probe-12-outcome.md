# Finding 19: Probe 12 Outcome — PASSED: a debugger-set breakpoint is hit (breakpoint setting 4c — the payoff)

**Status:**   **PASSED.** The whole breakpoint-setting arc lands: the engine resolves a method, sets
a breakpoint on it, and the running target HITS it — surfacing as a `StopReason.Breakpoint` the caller
controls. Probe 12 set a breakpoint on `Worker.Tick` and hit it **5×**, frozen between hits, advancing
only on `Resume`. The breakpoint-hit path is **9/9 PASSED** across all runs. A separate intermittent
*teardown* segfault (mscordbi's exit handler racing detach when the target is killed) was characterized
and avoided with the kill-first teardown pattern — it does not affect breakpoint functionality.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/12-breakpoint-hit-smoke.cs` + `11-bp-target.cs`
**Target:**   `Worker.Tick()` loop + a setup `Debugger.Break()` (waits for `Debugger.IsAttached`); baseline dbgshim.

## The full arc, end to end

```
attached   : DebugSession established
stopped    : Break (setup) — setting breakpoint on Worker.Tick
breakpoint : set on Worker.Tick; resuming
hit 1..5: BREAKPOINT — Worker.Tick entry; debuggee synchronized   (frozen 400 ms between hits)
PROBE 12 PASSED — debugger-set breakpoint on Worker.Tick hit 5× via the stopping model; frozen between hits.
```

`DebugSession.SetBreakpoint("11-bp-target", "Worker", "Tick")` composes all three legs:
- **4a** `RuntimeNavigation.FindModule` → the target module pointer,
- **4b** `MetadataResolver.ResolveMethodToken` → `mdMethodDef` `0x06000003`,
- **4c** `Breakpoints.TryCreate`: `ICorDebugModule.GetFunctionFromToken`(slot 9) → `ICorDebugFunction.CreateBreakpoint`(slot 8, function entry) → `ICorDebugBreakpoint.Activate(TRUE)`(slot 3).

The hit arrives as the `Breakpoint` callback, which the pump already classifies as a stopping event
(finding 16) — so breakpoints compose with the stopping model with **no new callback wiring**. All 4c
slots were correct on the first functional run. The (module, function, breakpoint) pointers are kept
alive in `DebugSession` so the breakpoint stays bound; released on Dispose.

## A teardown exit-race the probe caught (separate from the hit path)

With kill-then-dispose ordering, the probe segfaulted intermittently (1/3, then 3/6) **after** printing
PASSED — i.e., the breakpoint validation always succeeded; teardown crashed. The crash stack is
`CordbRCEventThread::ThreadProc → ExitProcessWorkItem::Do()` — mscordbi's RC event thread processing
the target's EXIT (from `KillTree`) on freed state, racing our detach. This is the **finding-14 class**
(RC thread async work races teardown), but the process-exit variant, which `Quiesce` (Stop-before-Detach)
does not cover.

- **Falsified fix:** deactivating breakpoints before detach did NOT help (3/6 segfaults) — removed.
- **Working mitigation:** kill the target FIRST, then dispose (the probe-08 pattern) — mscordbi
  processes the exit while we are cleanly attached, not mid-detach. **6/6 clean** with kill-first.

Tracked as [`docs/limits/drhook-detach-exit-race.md`](../../limits/drhook-detach-exit-race.md). It is
narrow (needs the target to exit coincident with detach) and does not affect normal detach-and-leave-running.

## What this proves

1. **Debugger-set breakpoints work end to end on CoreCLR 10 / macOS-arm64** — resolve, set, activate,
   hit, repeat — entirely BCL + raw interop, no netcoredbg.
2. **Breakpoints ride the stopping model** — a hit is just a `StopReason.Breakpoint`; the caller's
   `WaitForStop`/`Resume` cycle drives it exactly like the `Debugger.Break` validation (finding 16).
3. **Breakpoints re-arm** — the same breakpoint fires on every call (5 hits across loop iterations).

## Remaining in Phase 2 (after breakpoints)

- **Stepping** — `ICorDebugStepper` on the captured `_stopThread` → `Step`/`StepRange`/`StepOut` →
  `StepComplete` (already classified stopping).
- **Stack frames + variables** — `ICorDebugThread`→`EnumerateFrames`→`ILFrame`→locals/args→`ICorDebugValue`.

## References

- Probe: `poc/drhook-engine/12-breakpoint-hit-smoke.cs`, `11-bp-target.cs`
- Fixture: `fixtures/12-breakpoint-hit-osx-arm64-20260522T000827Z.txt`
- Engine: `src/DrHook.Engine/Interop/Breakpoints.cs`, `DebugSession.SetBreakpoint`
- Findings 17 (4a navigation), 18 (4b metadata), 16 (stopping model), 14 (detach-race class)
- Limit: `docs/limits/drhook-detach-exit-race.md`
- Mercury session 2026-05-21 observation `probe-12-passed`
