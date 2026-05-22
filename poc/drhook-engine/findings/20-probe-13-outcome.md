# Finding 20: Probe 13 Outcome — PASSED: stepping (over/into/out) via ICorDebugStepper

**Status:**   **PASSED, 3/3.** Stepping works and rides the stopping model: at a stop the engine
creates an `ICorDebugStepper` on the stopped thread, arms a step, and Continues; completion arrives
as a `StepComplete` callback the pump classifies as `StopReason.Step`. Probe 13 stopped at a
`Worker.Tick` breakpoint, then `StepOver` 3×, each surfacing a `StopReason.Step`. All stepper slots
correct on the first functional run.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/13-stepping-smoke.cs` + `11-bp-target.cs`
**Target:**   `Worker.Tick()` loop + setup `Debugger.Break()`; baseline dbgshim.

## How stepping composes with the stopping model

A step is just another resume action. The pump's resume was generalized from a plain continue to a
`ResumeKind` { `Continue`, `StepInto`, `StepOver`, `StepOut` }. When the caller steps, the worker —
already parked at the stop, holding the stop's thread pointer (`_stopThread`) — runs the resume
handler, which arms the stepper on that thread then Continues:

```
DebugSession.Attach: pump.Start((kind, thread) => { Stepping.Arm(thread, kind); return controller.Continue(0); });
```

`Stepping.Arm` (raw V-table): `ICorDebugThread.CreateStepper`@12 → `ICorDebugStepper.Step`@7 (`bStepIn`
TRUE=into / FALSE=over) or `StepOut`@9, then `Release` the stepper (the runtime owns the active step).
The step's `StepComplete` callback was ALREADY classified as a stopping event (finding 16, `CallbackKind.StepComplete`), so it surfaces as `StopReason.Step` with **no new callback wiring** — exactly as
breakpoints did.

## Run result (3/3)

```
hit        : Breakpoint at Worker.Tick — now stepping
step 1..3: STEP complete — debuggee synchronized at the next location
PROBE 13 PASSED — stepped 3× from a breakpoint; each StepComplete surfaced as StopReason.Step.
EXIT=0
```

`DebugSession` exposes `StepInto()` / `StepOver()` / `StepOut()` alongside `Resume()`; a new
in-process test confirms a step resume routes its `ResumeKind` + the captured stop thread to the
resume handler (17 unit tests green).

## What this proves

1. **`ICorDebugStepper` works on CoreCLR 10 / macOS-arm64** — create on the stopped thread, arm,
   Continue, and the step completes with a `StepComplete` callback.
2. **Stepping reuses the breakpoint/stop foundations** — the captured `_stopThread`, the resume
   rendezvous, and the `StepComplete` classification were all already in place; stepping added only
   the stepper interop + the `ResumeKind` plumbing.
3. **Steps re-arm** — each `StepOver` from the previous stop produces the next `Step` stop.

## Scope / what remains

- **v1 is IL-granularity** (`Step`/`StepOut`, no `COR_DEBUG_STEP_RANGE`). Source-LINE stepping (step a
  whole statement) needs PDB line→IL range mapping — a later refinement once symbol reading exists.
- **Stack frames + variables** is the remaining Phase 2 inspection piece: `ICorDebugThread`→
  `EnumerateFrames`→`ILFrame`→locals/args→`ICorDebugValue`. With it, "where did we stop / step to"
  becomes observable (today the probe confirms the step completed, not yet the location).

## References

- Probe: `poc/drhook-engine/13-stepping-smoke.cs`, `11-bp-target.cs`
- Fixture: `fixtures/13-stepping-osx-arm64-20260522T032320Z.txt`
- Engine: `src/DrHook.Engine/Interop/Stepping.cs`, `CallbackPump` (`ResumeKind` + resume handler), `DebugSession.StepInto/StepOver/StepOut`
- Findings 19 (breakpoints — the stop stepping starts from), 16 (stopping model — StepComplete classification)
- Mercury session 2026-05-21 observation `probe-13-passed`
