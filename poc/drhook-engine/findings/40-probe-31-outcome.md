# Finding 40: Probe 31 Outcome — PASSED: AsyncBreak (`DebugSession.Pause`)

**Status:**   **PASSED, 2/2** (clean exit 0 both runs; +1 in-process unit test). First Phase 3 substrate
gap closed: `DebugSession.Pause()` interrupts a running debuggee via `ICorDebugController.Stop` and surfaces
a `StopReason.Pause` through the same `WaitForStop` rendezvous used by callback-driven stops. The MCP
`drhook_step_pause` tool is now substrate-ready.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/31-pause-smoke.cs` + `31-pause-target.cs`

## Design — synthetic stopping event through the pump

`ICorDebugController.Stop` is *synchronous* — it synchronizes the process but doesn't fire a callback. So
AsyncBreak doesn't naturally fit the "event arrives → publish StopInfo" model. The clean integration is
to route a **synthetic** stopping event through the pump:

1. `DebugSession.Pause()` → `CallbackPump.RequestPause()` adds a `CallbackKind.PauseRequest`
   `CallbackEvent` to `_events` (same path as a real ICorDebug callback).
2. The pump worker dequeues it, calls the **pause handler** (`controller.Stop(0)`) to synchronize,
   sets `_stopThread = 0`, publishes `StopInfo(StopReason.Pause)`, and parks at `_resume.Take()` —
   exactly like a real stopping callback.
3. The caller's `WaitForStop` pulls the Pause stop; the caller's `Resume()` enqueues on `_resume`;
   the worker wakes and calls `_resumeHandler` → `controller.Continue(0)`.

The invariant the codebase already maintains — *the worker is the sole caller of controller operations* —
is preserved: both `Stop` and `Continue` are owned by this single thread, accessed through delegate
handlers. `CallbackPump.Start` now takes both:

```csharp
public void Start(Func<ResumeKind, nint, int> resume, Action pause);
```

The pause handler is `() => controller.Stop(0)`; the resume handler is unchanged.

## Why this shape was right

I considered (and rejected) handling Pause out-of-band — calling `controller.Stop` directly from
`DebugSession.Pause` and pushing a synthetic `StopInfo` past the pump. That breaks down on ordering:
if a real stopping callback is already queued or in flight, the user pulls TWO stops; each needs a
DIFFERENT resume path (worker-parked vs. direct controller.Continue), and `StopInfo` carries no
discriminator to pick the right one. Routing through the worker serializes pause/resume through the
single thread that already arbitrates `Continue`, so every stop — synthetic or real — resumes the same
way. No new state to track, no per-stop resume dispatch.

## What was added

- **`CallbackKind.PauseRequest`** — synthetic, not from ICorDebug.
- **`StopReason.Pause`** — surfaces via `WaitForStop` like any other stop.
- **`CallbackPump.RequestPause()`** + a pause-handler delegate stored alongside the resume handler.
- **Pump worker branch** for `PauseRequest`: pause handler, publish Pause stop, park on `_resume.Take`,
  then call the resume handler — uniform shape with the real-stopping branch.
- **`DebugSession.Pause()`** — one-line wrapper over `_pump.RequestPause()`.

In-process unit test `RequestPause_CallsPauseHandler_SurfacesPauseStop_AndResumeContinuesOnce` (CI-safe,
no debuggee) asserts: pause-handler fires exactly once, `StopReason.Pause` surfaces, no Continue until
Resume, exactly one Continue after Resume. 47 tests total pass.

## End-to-end (the probe)

Target runs a tight no-breakpoint, no-throw loop (`while (true) n++; …`). The probe attaches at the
startup `Debugger.Break`, resumes, sleeps 150 ms, then `Pause`. Two cycles, to verify the rendezvous
**repeats** (a one-shot would still surface cycle 1's Pause but cycle 2 would hang):

```
cycle 1    : calling Pause on a running debuggee …
             -> stop=Pause  (sole caller of controller.Stop is the pump worker, OK)
cycle 2    : second Pause to confirm the rendezvous repeats …
             -> stop=Pause
PROBE 31 PASSED
```

## Edge cases (documented)

If a callback-driven stop is already in flight when `Pause` is called, that stop surfaces FIRST (FIFO
through `_stops`) and the pause request queues behind it — it will fire after the caller resumes the
prior stop. Brief execution between the resume and the pause is structurally unavoidable, and matches
the `Continue → Pause` semantics any debugger has.

## Scope / next

Closes the smallest remaining Phase 3 substrate gap. Reference for the Phase 3 plan from the
"what's left" assessment:

- [x] **AsyncBreak / Pause** — done (this finding).
- [ ] **Launch** — start a process under debug control (`ICorDebug::CreateProcess`); unlocks
  `drhook_step_run` + `drhook_step_test` (which lifts a discovered PID via VSTEST_HOST_DEBUG and just
  attaches, so Launch is the primitive needed there too — for non-test runs at least).
- [ ] **Breakpoint registry** — `ListBreakpoints` / `RemoveBreakpoint(id)` / `ClearBreakpoints(category?)`.
- [ ] **Persistent exception filter** — register once, surfaces matching exceptions on subsequent
  waits (generalizes `WaitForExceptionPolicyStop`).
- [ ] **Object inspection (depth ≥ 1)** — `ICorDebugValue2::GetExactType` + `ObjectValue::GetFieldValue`,
  string/array rendering. Largest remaining piece.
- [ ] **`SteppingSessionManager` rewrite** backed by `DebugSession` — most of today's 1173 lines is DAP
  plumbing that disappears.

## References

- Probe: `poc/drhook-engine/31-pause-smoke.cs`, `31-pause-target.cs`
- Fixture: `fixtures/31-pause-osx-arm64-…` (+ a second clean run, exit 0)
- Engine: `IManagedCallbackSink.CallbackKind.PauseRequest`, `StopInfo.StopReason.Pause`,
  `CallbackPump.RequestPause` + pause-handler delegate + worker branch, `DebugSession.Pause`,
  `DebugSession.Attach` (passes `() => controller.Stop(0)` as the pause handler)
- Test: `tests/DrHook.Engine.Tests/CallbackPumpTests.RequestPause_CallsPauseHandler_SurfacesPauseStop_AndResumeContinuesOnce`
- Findings 15 (`Quiesce()` — same `controller.Stop(0)` call for clean-detach), 38 (deadline-based loop —
  the model this AsyncBreak slots into without disturbing)
- Mercury session 2026-05-22 observation `probe-31-asyncbreak-pause`
