# Finding 89 — A Borrowed session detached from an idle target leaks the CordbProcess; the target's later exit SEGVs the host

**Date:** 2026-10-03
**Status:** Root cause **PROVEN**; **pre-existing** (reproduced on the committed engine); **fix IMPLEMENTED +
validated** by unifying the detach sequence.

## Symptom

Attach (Borrowed) to an idle target → no organic stop → `Pause` → `Dispose` (detach-leave-running) appeared to
succeed. When the target **later exited**, the debugger host died: SIGSEGV, exit 139, in

```
libmscordbi.dylib  ExitProcessWorkItem::Do() + 416
```

Reproduced with a non-GUI idle console app, the Snapshot integration target, and TicTacToe — so not GUI-specific —
and with the **committed** engine exported via `git archive` (not caused by finding 88's change). Busy targets
(the MTP integration target) did not crash.

**Production impact:** this is exactly the June `drhook_capture_visual` workflow — `drhook_attach` (the Pause
fallback for an idle GUI app) → capture → `drhook_detach` — after which closing the app would have killed
`drhook-mcp`.

## Root cause (proven)

The engine's own anomaly channel told the story:

```
UnexpectedHResult  TryResumeForDetach (Continue loop): 0x8013132F on attempt 3   CORDBG_E_SUPERFLOUS_CONTINUE
UnexpectedHResult  Detach:    0x80131302                                         CORDBG_E_PROCESS_NOT_SYNCHRONIZED
UnexpectedHResult  Terminate: 0x80131C15                                         CORDBG_E_ILLEGAL_SHUTDOWN_ORDER
```

The Borrowed branch of `Dispose` ran `Quiesce → TryResumeForDetach → Detach`: it continued the target until
`S_FALSE` so Detach would unwind against a **running** target (probe 12's exit-race recipe). Two wrong premises:

1. mscordbi reports "already running" with **`CORDBG_E_SUPERFLOUS_CONTINUE`**, not `S_FALSE`.
2. **`Detach` requires a synchronized process.** Against a running one it fails `PROCESS_NOT_SYNCHRONIZED`, the
   `CordbProcess` stays alive, `Terminate` then fails, and our managed callback is freed anyway. When the target
   exits, mscordbi's exit work item dispatches into freed memory.

Busy targets only "worked" by luck: they kept generating callbacks, so every Continue returned `S_OK` (the loop
exhausted at 10 — another anomaly) and the process happened to be synchronized again when Detach ran.

**This was drift.** Probe 62 had already found the same `PROCESS_NOT_SYNCHRONIZED` failure for the **Owned**
leave-running path and fixed it there (drop the pre-resume; clear breakpoints; quiesce; detach). The Borrowed
branch — whose tail was "kept inline rather than shared" — kept the defective recipe.

## Fix

One detach sequence, `DebugSession.DetachSynchronized()` = `Quiesce → ClearBreakpoints → Detach`, used by both the
Borrowed `Dispose` branch and `DetachLeaveRunning`. Quiesce comes first because `Dispose` can be reached with the
target running and breakpoint deactivation belongs on a synchronized process. `TryResumeForDetach` (still used by
the Owned `Dispose` to release a pending stop before SIGTERM, ADR-008 1b) now treats `SUPERFLOUS_CONTINUE` as
"running" rather than an anomaly.

Residual: a target that exits at the very instant of Detach is the narrow race the pre-resume was guarding;
an already-exited target takes the dead-target branch (finding 66).

## Validation

- Repro (`attach → Pause → Dispose → kill target`): idle console app, Snapshot target, TicTacToe — all now
  `Detach`/`Terminate` clean, host survives. MTP target: teardown went from 9 `LateCallback` + "Continue loop
  exhausted" to **zero** anomalies beyond the expected `WorkerSilentBreak`.
- New integration test `BorrowedIdleDetachTest` — **fails on the pre-fix engine** (verified: SEGV + the three
  HRESULTs on the Snapshot target), passes after.
- Integration suite 16/16; `DrHook.Engine.Tests` 162/162; probe 88 confirmed.
- **Repeat-run gate — PASSED (2026-10-03):** 50 consecutive full integration runs, **50/50 green (16/16 each)**,
  after the teardown unification.
- **Live, through the reconnected `drhook-mcp`:** `drhook_attach` TicTacToe → `drhook_capture_visual` →
  `drhook_detach` (`uiLiveness: alive`, only the expected `WorkerSilentBreak`) → app closed → `drhook-mcp` still
  running.

## References

- `src/DrHook.Engine/DebugSession.cs` (`DetachSynchronized`, `Dispose` live-target branch, `DetachLeaveRunning`,
  `TryResumeForDetach`), `tests/DrHook.Engine.IntegrationTests/BorrowedIdleDetachTest.cs`.
- Probe 62/62b (the Owned-path discovery of the same failure); finding 66 (dead-target path); probe 12.
