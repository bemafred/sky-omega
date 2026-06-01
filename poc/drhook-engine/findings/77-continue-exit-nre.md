# Finding 77 — drhook_continue NRE when the debuggee exits during continue (post-exit frame walk)

**Date:** 2026-06-01 · **Platform:** macOS-arm64, .NET 10.0.0 · **Result:** substrate fix PASS (probe 61) + unit 119/119; adapter fix pending live MCP re-run

## What surfaced

During the [ADR-011](../../../docs/adrs/drhook/ADR-011-lifecycle-console-dashboard.md) §D3 **live** drain_log smoke (right after finding 76 unblocked the launch): a `suspend=none` logpoint was armed at the loop body of Launch.dll and `drhook_continue` was issued. The logpoint fired and ran to program end, so `WaitForStop` returned `StopReason.ProcessExited` — and `drhook_continue` returned:

```
{"status":"error","message":"Continue failed: Object reference not set to an instance of an object."}
```

The logpoint output was **completely unaffected** — a follow-up `drhook_drain_log` returned all 99 rendered records (`i=1 v=2` … `i=99 v=198`). The NRE was purely in the continue *response* path.

## Root cause

`ContinueAsync` (and four sibling stop-handlers — step over/into/out, launch/attach run-to-bp) build their response with `BuildCurrentState()` → `DebugSession.GetStackFrames()` → `Frames.WalkManagedFrames(_pump.StopThread)`. `WalkManagedFrames` *does* guard `if (pThread == 0) return frames;` — but `CallbackPump` only cleared `_stopThread` on a Pause and reassigned it on every stop; on **ExitProcess it left `_stopThread` stale**. So after exit, `StopThread` returned the last stop's now-dead `ICorDebugThread`, the walk sailed past the `== 0` guard, and the first COM call on the released thread dereferenced null → NRE. The session was also left dangling (`IsActive == true` over a dead process).

## Fix (two layers — scope: substrate guard everywhere + continue handler)

1. **Substrate (`CallbackPump.cs`):** clear `_stopThread = 0` on the `ExitProcess` informational event. The existing `WalkManagedFrames` guard now holds post-exit, so `GetStackFrames()` returns empty instead of NREing — for **all five** stop-handlers.
2. **Adapter (`EngineSteppingSession.ContinueAsync`):** when the stop reason is `ProcessExited`, `CleanupSession()` (clears `IsActive`; the adapter-level log/console/anomaly buffers survive it, so final output can still be drained) and return a clean `processExited` response directing the caller to drain and start a new session — instead of a frame dump.

## Validation

- **Probe 61** (`61-post-exit-frames-smoke.cs`): launch Launch.dll → setup `Break` → resume with no breakpoint → `ProcessExited` → `GetStackFrames()` returns **0 frames, no throw**. PASS(0). Pre-fix this call NRE'd.
- Build green (0 warnings); engine unit **119/119**.
- The end-to-end MCP path (continue → exit → clean `processExited` response, session cleared) re-validates live after the DrHook server reconnects onto this build.

## Scope note → resolved

Initially the dedicated `ProcessExited` handling (cleanup + clean exit response) was applied to **`continue`** only — the path that surfaced it and the common "run a logpoint to program end" flow — with the substrate `_stopThread=0` guard making the other four handlers crash-*safe* but not yet emitting a clean exit response (they'd report an ordinary stop with empty frames + leave `IsActive` set). That uniform handling was then completed: a single `RenderProcessExited(operation, note, hypothesis)` now backs all five stop-handlers — `continue` / step (over/into/out) / `pause` report ran-to-completion; `launch` / `attach` report "exited before reaching the breakpoint." All tear down the session (the drain buffers survive) instead of walking a dead process for a frame. Build green, unit 119/119, integration 12/12.
