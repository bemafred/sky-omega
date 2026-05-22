# Limit: DrHook.Engine detach races mscordbi's process-exit handler

**Status:**        Triggered (intermittent; working mitigation = quiesce/kill the target before detach). Sibling of [drhook-clean-detach](drhook-clean-detach.md).
**Surfaced:**      2026-05-22, by probe 12 (breakpoint hit). The breakpoint path passed every run; teardown segfaulted intermittently.
**Last reviewed:** 2026-05-22
**Promotes to:**   a teardown-quiescence ADR if/when DrHook.Engine drives long-lived sessions where targets exit mid-session, OR Phase 3 switchover.

## Description

When a debug session is torn down (`DebugSession.Dispose` → quiescent detach) and the target process EXITS at about the same time (e.g. killed by the harness right after detach, or the target exits on its own), mscordbi's RC event thread processes the exit and intermittently segfaults on freed/torn-down state:

```
CordbRCEventThread::ThreadProc → ExitProcessWorkItem::Do()   (EXC_BAD_ACCESS, garbage address)
```

This is the **finding-14 class** (mscordbi's async RC event thread races our teardown), but a distinct variant: [drhook-clean-detach](drhook-clean-detach.md)'s `Quiesce` (Stop-before-Detach) covers the queued-*callback*-flush race, not the *process-exit* work item, which fires when the debuggee dies around detach time. It only appeared once breakpoints were involved (probe 09, a `Debugger.Break` loop with no breakpoints + dispose-then-kill, was 3/3 clean; probe 12 with an active breakpoint + dispose-then-kill segfaulted intermittently) — likely the stopped-at-breakpoint state widens the race window.

## Trigger condition

The target process exits (killed or self-exit) coincident with `Dispose`/`Detach`. Detaching and leaving the target running is unaffected (no exit work item). Narrow, but real when a debuggee crashes or exits during a session.

## Current state

- **Mitigation (validated):** kill / quiesce the target BEFORE `Dispose`, so mscordbi processes the exit while the debugger is cleanly attached, not mid-detach. Probe 12 with kill-first teardown: **6/6 clean** (vs intermittent segfaults with dispose-then-kill). This is the same pattern probe 08 used for the flood case.
- **Falsified fix:** deactivating breakpoints (`Activate(false)`) before detach did NOT help (3/6 segfaults) — removed; the crash is in exit handling, not breakpoint state.
- The breakpoint functionality is unaffected — the hit path passed 9/9; only teardown is at issue, and the verdict/fixture are captured before teardown.

## Candidate mitigations

1. **Process the exit before detaching.** If the target has exited (or we intend to stop it), let the `ExitProcess` callback flow to the pump (it surfaces `StopReason.ProcessExited`) and only then detach a quiesced/dead process. This is the engine-side version of the probe's kill-first.
2. **RC-thread quiescence handshake.** A general fix for the whole finding-14 class would confirm mscordbi's RC event thread has drained ALL pending work items (callbacks AND exit) before releasing native state — but ICorDebug exposes no direct "join the RC thread" primitive, so this needs its own probe-driven design (deferred).
3. **Detach-and-leave-running as the default teardown** where the caller doesn't need the target stopped — sidesteps the exit work item entirely.
