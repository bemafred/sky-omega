# Finding 16: Probe 09 Outcome — PASSED: the stopping-event model (breakpoint/step/break suppress auto-Continue)

**Status:**   **PASSED, 3/3 reproducible.** The continue-loop now classifies callbacks: informational events auto-continue (the increment-1 firehose), but the three STOPPING callbacks (Breakpoint, StepComplete, Break) suppress the auto-Continue, leave the debuggee synchronized, and surface a `StopInfo` the caller controls. Validated against a `Debugger.Break()` target: each Break stops the engine, the debuggee stays frozen while held, and advances only on `Resume`. This is the keystone breakpoints and stepping ride on.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/09-stopping-model-smoke.cs` + `09-break-target.cs`
**Target:**   disposable .NET 10 process calling `Debugger.Break()` in a loop; baseline dbgshim (`Microsoft.Diagnostics.DbgShim.osx-arm64` 9.0.661903).

## De-risk first: does `Debugger.Break()` fire the `Break` callback?

Before building the model, the assumption was checked cheaply by running `09-break-target.cs` through the probe-07 harness (which auto-continues + histograms): **`Break=143`** in a 4 s window. So `Debugger.Break()` does fire `ICorDebugManagedCallback::Break` under our attach, giving a stopping event to test *without* breakpoint-setting machinery (which needs a much larger interop surface — the next increment).

## What changed

- **Classification.** `CallbackKind { Informational, BreakpointHit, StepComplete, Break }`. The host→pump contract became `IManagedCallbackSink.OnCallback(kind, name, appDomain, thread)`; the three stopping thunks (`Breakpoint`@3, `StepComplete`@4, `Break`@5) tag their kind and forward the thread/appDomain pointers. The other 35 thunks stay `Informational`.
- **The pump.** Informational events surface to the user sink + auto-continue (unchanged). Stopping events publish a `StopInfo`, leave the debuggee synchronized, and **park the worker** until the caller resumes — a rendezvous over two blocking queues, keeping the single worker the only caller of `Continue`.
- **Session API.** `WaitForStop(timeout)` → `StopInfo?` (null on timeout; `ProcessExited` when the target exits) and `Resume()`.

## Run result (3/3)

```
attached   : DebugSession established; driving 5 controlled stops
round 1..5: STOP (Break) — auto-Continue suppressed, debuggee synchronized
round 1..5: confirmed frozen (0 stops in a 400 ms held window)
PROBE 09 PASSED — 5 controlled stops; debuggee frozen between resumes, advancing only on Resume.
EXIT=0
```

The **frozen check is the discriminator**: while a stop is held (no `Resume`), a second `WaitForStop(400 ms)` returns nothing. Were the Break auto-continued (the increment-1 behavior), the target would fire ~20 more Breaks in that window and the second wait would return immediately. Zero stops in the held window, across 5 rounds × 3 runs, proves the debuggee is genuinely synchronized and only the caller's `Resume` advances it.

## What this proves

1. **Auto-Continue is correctly suppressed for stopping callbacks** and preserved for informational ones — the two paths coexist (16 unit tests green, including stop-suppresses-Continue-until-Resume and stops-don't-leak-into-the-informational-firehose).
2. **The caller controls execution at a stop.** `WaitForStop`/`Resume` form a clean run/stop cycle; the worker parks (process frozen) and resumes on command. This is exactly the control surface breakpoints and stepping need.
3. **Teardown from a stopped state is clean.** `Dispose` after the loop joins the parked worker (its `Resume`-queue completes), then the increment-2 quiescent detach runs — exit 0, no segfault.
4. **The thread pointer is captured at the stop** (`_stopThread`), ready for stepping (creating an `ICorDebugStepper` on the stopped thread before the resume-Continue) in the next increment.

## Scope / what remains

This validates the stopping *model*. It does NOT yet set breakpoints or step:
- **Breakpoint setting** — resolve a location to an `ICorDebugFunction` (`ICorDebugProcess`→`AppDomain`→`Assembly`→`Module`, metadata token resolution) → `ICorDebugCode.CreateBreakpoint(ilOffset)` → `Activate`. A real breakpoint then arrives as a `Breakpoint` callback and rides this exact model.
- **Stepping** — `ICorDebugStepper` on `_stopThread` → `Step`/`StepRange`/`StepOut` → `Continue`; completion arrives as `StepComplete` (already classified stopping).
- **Stack frames + variables** — `ICorDebugThread`→`EnumerateFrames`→`ILFrame`→locals/arguments→`ICorDebugValue`.

These are the next increments; each is more consume-direction interop on the proven foundation (no new callback-delivery risk).

## References

- Probe: `poc/drhook-engine/09-stopping-model-smoke.cs`, `09-break-target.cs`
- Fixture: `fixtures/09-stopping-model-osx-arm64-20260521T225151Z.txt`
- Engine: `CallbackPump.cs` (classification + park/resume), `IManagedCallbackSink.cs` (`CallbackKind`/contract), `StopInfo.cs` (`StopReason`/`StopInfo`), `Interop/ManagedCallbackHost.cs` (stopping thunks), `DebugSession.cs` (`WaitForStop`/`Resume`)
- Finding 14/15 (continue-loop + quiescent detach this builds on), Finding 12 (CallbacksQueue pattern)
- Mercury session 2026-05-21 observation `probe-09-passed`
