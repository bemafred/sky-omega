# Finding 14: Probe 07 Outcome — PASSED: continue-loop drains a live callback stream; detach-under-flood is a characterized limit

**Status:**   **PASSED** on the continue-loop claim. Against a target generating continuous managed events, the assembled engine (`DebugSession` + `ManagedCallbackHost` + `CallbackPump`) drained **591 callbacks in a 4-second window** (166 in the first second), with `Continue` issued once per synchronized stop. Phase 1's single-event ceiling (probe 05 saw **0** callbacks from a parked target) is gone. The probe *also* surfaced a real teardown limit: **`Detach` races mscordbi's RC-event-thread flush of queued callbacks** and segfaults under a continuous event flood. Root cause confirmed; fix (a quiescence protocol) deferred to ADR-006 Phase 2 increment 2.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/07-continue-loop-smoke.cs` (harness) + `07-target.cs` (live event generator)
**Target:**   disposable .NET 10 process churning threads + throw/catch in a loop; baseline dbgshim (`Microsoft.Diagnostics.DbgShim.osx-arm64` 9.0.661903).

## Why a new target was needed

Probe 05 attached cleanly (`DebugActiveProcess`/`Detach`/`Terminate` all S_OK) yet observed **zero** callbacks: modern CoreCLR replays no catch-up Create/Load events on attach, so a *parked* target generates nothing. Phase 1 therefore never proved a callback **stream** flows — only that a single callback can be received in-process (probe 06) and that attach/detach works (probe 05). Probe 07's target instead generates events continuously (each `new Thread(...).Start()` → CreateThread + ExitThread; each `throw`/`catch` → first-chance Exception), so every iteration is a synchronized stop the continue-loop must `Continue` past.

## Run result (clean)

```
target pid : 93155
is-dotnet  : True
attached   : DebugSession established
events @1s : 166
events @4s : 591
histogram  : CreateAppDomain=1, CreateProcess=1, CreateThread=117, Exception2=228,
             Exception=114, ExitThread=114, LoadAssembly=8, LoadModule=8
PROBE 07 PASSED — continue-loop drained 591 callbacks from a live stream.
detached   : DebugSession disposed
EXIT=0
```

The discriminator is unambiguous: **0** events = no delivery; **1** = delivered then wedged (Continue not resuming); **>>1** = the loop drains a stream. 591 is decisively the third case. The histogram matches the target's generators exactly (thread churn + first-chance/catch-handler Exception pairs), plus the small attach-time burst (CreateProcess/CreateAppDomain + 8 assemblies + 8 modules).

## What this proves

1. **The continue-loop works end-to-end against a real debuggee.** Callbacks arrive one at a time on mscordbi's event thread, the `[UnmanagedCallersOnly]` thunks enqueue + return S_OK, the worker drains and `Continue`s, and the next callback fires — sustained over hundreds of events. This is the empirical claim the in-process drain tests (deterministic, CI-safe) could not make.
2. **`Continue` from the worker thread (not mscordbi's event thread) is correct** — the netcoredbg CallbacksQueue decoupling holds in practice; no reentrancy, no missed continues.
3. **The full Layer-3 path composes**: dbgshim attach → source-gen COM RCW (consume) → hand-rolled vtable (receive) → continue-loop → clean detach (in the quiet case).

## The teardown limit (detach races the RC-thread flush)

The first two runs exited **139 (SIGSEGV)** *after* printing the event counts — i.e., the loop worked, but `DebugSession.Dispose` crashed during teardown. Two crash reports, same faulting thread, with a telling progression:

| Run | Dispose behavior | Fault address | Faulting frame |
|---|---|---|---|
| 1 | free callback vtable + Release + unload dbgshim | `0x039c758f06a72af6` (garbage — our freed vtable) | `ShimProxyCallback::CreateThread::Dispatch` |
| 2 | **retain** all native resources (interim) | `0x000000000000005b` (near-null — mscordbi's own torn-down shim) | `ShimProxyCallback::CreateThread::Dispatch` |

Both faults are on **`CordbRCEventThread::ThreadProc` → `FlushQueuedEvents` → `DispatchRCEvent` → CreateThread**: mscordbi's own RC event thread, still flushing a backlog of queued CreateThread callbacks, *after* `Detach()` returned. Retaining our callback (run 2) only moved the fault out of our freed vtable and into mscordbi's *own* shim state, which `Detach` had torn down underneath the still-running flush. So **the crash is not a lifetime bug on our side — it is `Detach` racing mscordbi's queued-event flush.** A continuously-flooding target always has a backlog, so `Detach` always races it.

**Confirmation:** killing the target *before* `Detach` (stopping the flood, so the queue drains) makes teardown clean — run 3 above exited 0 with "DebugSession disposed". This both confirms the root cause and points at the fix.

### Fix direction (ADR-006 Phase 2 increment 2)

Clean detach from a *busy* target needs a quiescence protocol, not a lifetime tweak:
`ICorDebugController::Stop` (synchronize) → `SetAllThreadsDebugState(SUSPEND)` (stop new events) → drain `HasQueuedCallbacks` (Continue the backlog with threads suspended) → `Detach` in the resulting quiet window. The interfaces are already in `Interop/CorDebug.cs` (`Stop`@3, `HasQueuedCallbacks`, `SetAllThreadsDebugState`). Tracked in `docs/limits/drhook-clean-detach.md`.

`DebugSession.Dispose` currently uses the quiet-detach teardown (correct when the queue is empty — probe 05's hr=0 case), with the limit documented inline.

## References

- Probe: `poc/drhook-engine/07-continue-loop-smoke.cs`, `07-target.cs`
- Fixture: `fixtures/07-continue-loop-smoke-osx-arm64-20260521T220343Z.txt`
- Engine: `src/DrHook.Engine/CallbackPump.cs` (the loop), `DebugSession.cs` (wiring + quiet-detach teardown + limit note)
- Limit: `docs/limits/drhook-clean-detach.md`
- Finding 12 (netcoredbg CallbacksQueue pattern — the design this validates), Finding 10 (probe 05: 0 callbacks from a parked target — the gap this closes)
- Mercury session 2026-05-21 observations `continue-loop`, `probe-07-passed`, `detach-flood-race`
