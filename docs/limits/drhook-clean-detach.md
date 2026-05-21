# Limit: DrHook.Engine detach races mscordbi's queued-callback flush under load

**Status:**        **Resolved** (2026-05-21, ADR-006 Phase 2 increment 2) — `ICorDebugController::Stop` before `Detach` synchronizes the process so no callback flush is in flight when the shim is torn down. Validated by probe 08 (3/3 clean detach under continuous flood; finding 15). The minimal fix sufficed — the proposed `SetAllThreadsDebugState`/`HasQueuedCallbacks` escalation was not needed.
**Surfaced:**      2026-05-21, by probe 07 (continue-loop live smoke). The continue-loop PASSED (591 callbacks drained); teardown segfaulted under a continuous event flood.
**Last reviewed:** 2026-05-21
**Promotes to:**   ADR-006 Phase 2 increment 2 — clean-detach quiescence (done: `DebugSession.Quiesce()` → `Stop`).

## Description

`ICorDebugController::Detach` is safe only when mscordbi's callback queue is quiet. Against a target that floods managed events (continuous thread creation, exceptions, module loads), mscordbi's RC event thread is still flushing a backlog of queued callbacks when `DebugSession.Dispose` calls `Detach`. `Detach` tears down the shim/process state underneath that running flush, and it segfaults mid-dispatch.

Observed in probe 07 as two `EXC_BAD_ACCESS` crashes, same faulting thread (`CordbRCEventThread::ThreadProc` → `FlushQueuedEvents` → `DispatchRCEvent` → `ShimProxyCallback::CreateThread::Dispatch`):

| Dispose behavior | Fault address | Interpretation |
|---|---|---|
| free callback + Release + unload dbgshim | garbage pointer | a queued CreateThread dispatched into **our freed vtable** |
| **retain** all native resources | near-null (`0x5b`) | dispatched into **mscordbi's own torn-down shim** |

Retaining our resources only moved the fault from our vtable into mscordbi's own shim state — proving the crash is **not** a lifetime bug on our side. It is `Detach` racing mscordbi's queued-event flush. A continuously-flooding target always has a backlog, so `Detach` always races it.

**Confirmation of root cause:** killing the target *before* `Detach` (stopping the flood so the queue drains) makes teardown clean — probe 07's final run exited 0 with a successful "DebugSession disposed". So the quiet-detach path is correct; only detach-from-a-busy-target is unsafe.

## Trigger condition

`DebugSession.Dispose` (or an explicit `Detach`) while the target is actively generating managed events fast enough that mscordbi has queued callbacks pending. Quiet targets (the common DrHook case — inspect a process, occasional breakpoints) are unaffected: probe 05 detached from a 0-queued-callback state with hr=0 and no crash.

## Resolution

`DebugSession.Dispose` now calls `Quiesce()` — `_controller.Stop(0)` — before `Detach()`. `Stop` is synchronous: it returns only once dispatch is quiet and the debuggee is halted, so `Detach` no longer races mscordbi's RC-event-thread flush. Probe 08 (`08-quiesce-detach-smoke.cs`) disposes the engine while the target is *still flooding* — the exact probe-07 crash scenario — and detaches cleanly 3/3, leaving the target running. Continue-loop (`CallbackPump`) was always unaffected; this was purely a teardown-ordering bug.

Empirically, only `Stop` was needed — the `SetAllThreadsDebugState(SUSPEND)` + `HasQueuedCallbacks`-drain escalation in mitigation (1) below proved unnecessary on CoreCLR 10 / macOS-arm64. See finding 15.

## Candidate mitigations

1. **Quiescence protocol (preferred).** Before `Detach`: `ICorDebugController::Stop` (synchronize) → `SetAllThreadsDebugState(THREAD_SUSPEND, …)` (so resuming to drain produces no *new* events) → loop `Continue` while `HasQueuedCallbacks` reports a backlog → `Detach` in the resulting quiet window. All three interfaces already exist in `Interop/CorDebug.cs` (`Stop`, `HasQueuedCallbacks`, `SetAllThreadsDebugState`). This is the netcoredbg-style detach and is the increment-2 target.

2. **Drain-then-detach via the pump.** Keep the `CallbackPump` worker running through teardown, switch it to a "drain mode" that `Continue`s without surfacing to the user sink, and only `Detach` once the queue has been observed empty for a debounce window. Simpler but relies on a timing heuristic rather than `HasQueuedCallbacks`; weaker than (1).

3. **Stop-the-world before detach.** `Stop` then `Detach` without the suspend+drain — may be sufficient if `Stop` synchronizes hard enough that the flush completes before `Detach`. Cheapest to try; validate empirically against the probe-07 target before trusting it.

The fix is increment-2 work because it needs its own empirical validation (the exact CoreCLR/macOS `Stop`/`HasQueuedCallbacks`/`Detach` interaction is not yet probed) — not a guess in `Dispose`.
