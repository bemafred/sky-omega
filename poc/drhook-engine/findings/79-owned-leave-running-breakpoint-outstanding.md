# Finding 79 — F-010-2 breakpoint-stop cell: Detach refuses outstanding breakpoints (the likely lost-night bug), fixed

**Date:** 2026-06-03
**Probe:** `62b-owned-leave-running-breakpoint-smoke.cs` (reuses `62-leave-running-target/`)
**Platform:** macOS-arm64, .NET 10.0.0
**Status:** PASS — 5/5 after fix. Pause-stop (probe 62) regression 3/3 PASS.

## What 62b tested

The MCP-realistic axis finding 78 deferred: detach-leave-running while the Owned target is stopped at an actual **breakpoint** (not a synthetic Pause). Flow: `Launch(Owned, hold-gate) → EntryModuleLoaded → arm line breakpoint via the hold-gate → Resume → Breakpoint stop → DetachLeaveRunning → assert heartbeat advances`.

## The bug (5/5 deterministic, pre-fix) — almost certainly the lost-night failure

`DetachLeaveRunning` (Quiesce → Detach — the probe-62-validated *Pause* recipe) **hung** the target at the breakpoint: heartbeat `1 → 1`, target alive but not progressing. Anomalies:
- `Detach`: **`0x80131C21` = `CORDBG_E_DETACH_FAILED_OUTSTANDING_BREAKPOINTS`** — ICorDebug **refuses** to detach while a breakpoint is active.
- `Terminate`: `0x80131C15` = `CORDBG_E_ILLEGAL_SHUTDOWN_ORDER` — cascades from the failed Detach.

This is the breakpoint-specific cell the Pause-stop probe (62) **masked** — 62 had no breakpoints, so Detach succeeded. Had MCP been wired after 62, every agent detach-from-breakpoint would hang exactly as the prior session's unexplained "F-010-2 failure" did. Probe 33's cleanup comment (*"Dispose's Detach path leaves a currently-stopped process synchronized indefinitely"*) was the same observation; the induced-stop confound is now mechanically explained — **it is not the stop per se, it is the outstanding breakpoint blocking Detach.**

## The fix

`DetachLeaveRunning` deactivates all breakpoints before `Quiesce → Detach` (`ClearBreakpoints()` → `ICorDebugBreakpoint.Activate(FALSE)`). The sibling `OUTSTANDING_*` errors — evals (`0x1c18`) and steppers (`0x1c19`) — cannot be in flight at a stop (a func-eval is synchronous; a stepper is consumed by the StepComplete that produced the stop), so no handling is needed for them.

## Result (post-fix)

- **62b (breakpoint-stop): 5/5 PASS** — Detach removes the breakpoint and resumes; heartbeat `1 → ~19`, target alive, 0 `UnexpectedHResult`, 1 benign `WorkerSilentBreak`.
- **62 (Pause-stop) regression: 3/3 PASS** — `ClearBreakpoints` on zero breakpoints is a harmless no-op.

## Scope now

Both stop cells validated (Pause + breakpoint) for Owned leave-running, macOS-arm64. Still deferred:
- **Console-pipe survival** of a `Console`-writing leave-running target (D4/PTY).
- **MCP `DetachAsync` wiring** — now genuinely green-lit (both substrate cells pass).
- **Cross-platform** (Windows/Linux) — Phase 9.

## Files
- `poc/drhook-engine/62b-owned-leave-running-breakpoint-smoke.cs`
- `poc/drhook-engine/62-leave-running-target/Program.cs` — `BEAT_HERE` marker added
- `src/DrHook.Engine/DebugSession.cs` — `DetachLeaveRunning`: `ClearBreakpoints()` before `Quiesce → Detach`
- Fixtures: `poc/drhook-engine/fixtures/62b-owned-leave-running-bp-osx-arm64-*.txt`
