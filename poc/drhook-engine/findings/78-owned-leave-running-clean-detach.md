# Finding 78 — F-010-2 Owned detach-leave-running: clean detach validated (held/stopped × clean-detach cell)

**Date:** 2026-06-03
**Probe:** `62-owned-leave-running-smoke.cs` + `62-leave-running-target/` (in-repo, reproducible)
**Platform:** macOS-arm64, .NET 10.0.0
**Status:** PASS — 5/5 stable for the **Pause-stop** cell. Breakpoint-stop variant + console-pipe survival + MCP wiring explicitly deferred (named below).

## Context — the cell the lost-night survival probe never tested

The prior session's `detach-survival-probe.cs` (commit 1048d70) launched a **free-running** target and **rude-EXITed** the debugger (`Environment.Exit`, no `Detach`), then *inferred* "a clean ICorDebug Detach certainly leaves it running." It validated reparent-on-rude-exit, not the F-010-2 capability. No finding was written; the conclusion lived only in a 5am commit message.

F-010-2 (Owned detach-leave-running) requires the opposite axes: an Owned (launched) target currently **synchronized** (the normal debugging state — held at the entry-module hold-gate and/or stopped at a breakpoint), then a **clean** leave-running `Detach`. Probe 33's own cleanup comment already recorded the risk: *"Dispose's Detach path leaves a currently-stopped process synchronized indefinitely (no implicit Continue)"* — detaching a stopped launched target without an explicit resume **hangs** it. That induced hung-target behavior is the most likely explanation for the prior session's unexplained "F-010-2 failure" (induced, misattributed — not a Detach-substrate fault).

## Substrate change

`DebugSession.DetachLeaveRunning()` — the F-010-2 primitive. Runs the teardown shape of the Borrowed live path (pump teardown → synchronize → `Detach` → `Terminate` → ref release) for an **Owned** target, deliberately **skipping** the SIGTERM/SIGKILL the Owned `Dispose` branch issues. Idempotent (shares the `_disposed` gate, so a later `Dispose` is a no-op). Console pipes intentionally **not** closed — a live leave-running target still holds the write ends; closing the read ends would `EPIPE`/`SIGPIPE` it (the console-survival unknown is deferred).

## The v1 → v2 lesson (recorded, not glossed)

**v1** copied the Borrowed *exit-race* recipe: `Quiesce → TryResumeForDetach → Detach`. It produced **3 `UnexpectedHResult` anomalies**:
- `TryResumeForDetach` Continue loop, attempt 3: `0x8013132F` — over-continued; `Continue` returned this because the target was already running.
- `Detach`: `0x80131302` = **`CORDBG_E_PROCESS_NOT_SYNCHRONIZED`** — `Detach` was called on the now-**running** target and rejected it. **The Detach FAILED.**
- `Terminate`: `0x80131C15` — mscordbi left inconsistent after the failed Detach.

The target still "survived" — but via **reparent-on-process-exit** (the same rude-survival the original probe measured), **not** a clean Detach. The probe's first verdict passed on bare survival; it was too lenient.

**Root cause:** ICorDebug `Detach` **requires a synchronized target**. The Borrowed path pre-resumes (to running) specifically to control the **exit-race** for a target about to exit (finding 59 / probe 12). A leave-running target is **not exiting**, so that guard does not apply — and the pre-resume actively breaks `Detach`.

**v2 (the fix):** `Quiesce → Detach` (no pre-resume). The target is synchronized when `Detach` is called; `Detach` detaches fully and mscordbi **implicit-resumes** the target, which runs free un-debugged.

## Result (v2) — 5/5 stable, deterministic

`Launch(Owned, hold-gate) → EntryModuleLoaded → Resume → Pause-stop (beat=7) → DetachLeaveRunning → heartbeat advances 7 → 26 while the debugger process is still alive → target alive`. **0 `UnexpectedHResult`** anomalies; 1 benign `WorkerSilentBreak` (the teardown-while-paused worker unpark).

The **advance-while-debugger-alive** window is the discriminator: it proves the target runs because `Detach` succeeded, not because the probe exited (the v1 / original-survival confound).

## Probe-too-lenient fix

The probe now **falsifies (code 10)** if any `UnexpectedHResult` anomaly surfaces — survival alone is not a pass. `WorkerSilentBreak` is allow-listed as the benign teardown unpark.

## Scope — validated vs. explicitly deferred

**Validated:** Owned + entry-module hold-gate + **Pause-stop** + clean leave-running `Detach`, macOS-arm64, 5/5.

**Deferred (named, not hidden):**
1. **Breakpoint-stop variant** — the MCP-realistic case (the agent detaches from a breakpoint stop). v2 does not pre-resume, so `Detach` removes the breakpoint and resumes; expected to work, but **untested** → probe 62b before MCP wiring.
2. **Console-pipe survival** — a leave-running target that writes to `Console` vs. the D2 DrHook-owned pipes (`EPIPE`/`SIGPIPE` on teardown). Probe 62 isolates this out via a file-only-heartbeat target. The console handover (a PTY, D4) is the separate unknown.
3. **MCP wiring** — `DetachAsync` for Owned still returns the "not yet available" error. Wiring it to `DetachLeaveRunning` is the now-green-lit next increment (after 62b).
4. **Cross-platform** (Windows/Linux) — Phase 9.

## Files
- `poc/drhook-engine/62-owned-leave-running-smoke.cs`
- `poc/drhook-engine/62-leave-running-target/{LeaveRunning.csproj,Program.cs}`
- `src/DrHook.Engine/DebugSession.cs` — `DetachLeaveRunning()`
- Fixtures: `poc/drhook-engine/fixtures/62-owned-leave-running-osx-arm64-*.txt`
