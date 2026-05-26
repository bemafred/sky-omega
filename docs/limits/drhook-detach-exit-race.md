# Limit: DrHook.Engine detach races mscordbi's process-exit handler

**Status:**        **Resolved 2026-05-24** by [finding 66](../../poc/drhook-engine/findings/66-target-death-detection.md) — target-death detection in `DebugSession.Dispose` short-circuits Quiesce + TryResumeForDetach when target has exited; explicit ExitWorkSettleMs (200 ms) before Detach lets mscordbi exit-work-item complete; Detach preserved (releases CCW from mscordbi, required before `_callback.Dispose` frees CCW memory). Discipline contract reaffirmed by [ADR-008](../adrs/drhook/ADR-008-process-lifecycle-discipline.md): Layer 1 (natural-exit default for well-behaved targets) + Layer 2 (substrate's death-detection guard for misbehaving / externally-killed targets, which is what this limit's substrate change provides). Sibling of [drhook-clean-detach](drhook-clean-detach.md). Sibling resolution: [finding 65](../../poc/drhook-engine/findings/65-probe42-redesign-regression.md) dispatch-settle in TryResumeForDetach (informational-flood class). Sibling resolution: [finding 64](../../poc/drhook-engine/findings/64-substrate-owned-lifecycle.md) `AttachAndOwn` kill-first for Owned (superseded by [ADR-008](../adrs/drhook/ADR-008-process-lifecycle-discipline.md) Increment 1: SIGTERM-then-SIGKILL escalation).
**Surfaced:**      2026-05-22, by probe 12 (breakpoint hit). The breakpoint path passed every run; teardown segfaulted intermittently.
**Resolved:**      2026-05-24 by Probe 47 + finding 66 substrate change. Probe 47: 10/10 clean Dispose against externally-killed Borrowed targets. Probe 44 phase C (previously latent under stale-cache amplifier): 10/10 PASS post-fix. Discipline-aligned framing made explicit 2026-05-26 by [ADR-008](../adrs/drhook/ADR-008-process-lifecycle-discipline.md).
**Last reviewed:** 2026-05-26
**Promotes to:**   N/A — Resolved.

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
