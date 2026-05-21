# Finding 15: Probe 08 Outcome — PASSED: quiescent detach (Stop → Detach) is clean under flood

**Status:**   **PASSED, 3/3 reproducible.** `ICorDebugController::Stop` before `Detach` resolves the finding-14 teardown segfault. Disposing the engine while the target is *still flooding* managed events — the exact scenario that crashed probe 07 (EXIT 139) — now detaches cleanly and leaves the target running. **The minimal hypothesis held: `SetAllThreadsDebugState(SUSPEND)` and a `HasQueuedCallbacks` drain loop were NOT needed.** Resolves `docs/limits/drhook-clean-detach.md`.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/08-quiesce-detach-smoke.cs` + `07-target.cs` (the flooding generator)
**Target:**   disposable .NET 10 process churning threads + throw/catch; baseline dbgshim (`Microsoft.Diagnostics.DbgShim.osx-arm64` 9.0.661903).

## What changed

`DebugSession.Dispose` (and the detach path) now calls `Quiesce()` — `_controller.Stop(0)` — before `Detach()`:

```
_pump.Dispose();   // stop our worker (no concurrent Continue)
Quiesce();         // _controller.Stop(0): synchronize — blocks until in-flight dispatch
                   // completes and the debuggee is halted
Detach();
_cordbg.Terminate();
…free resources…
```

`Stop` is synchronous: it returns only once the process is synchronized, i.e. no callback dispatch is in flight and the debuggee threads are halted. With dispatch quiet, `Detach` no longer races mscordbi's RC event thread flushing the queued backlog — the crash in `ShimProxyCallback::CreateThread::Dispatch` (finding 14) cannot occur because there is no concurrent flush to tear down.

## Run result (3/3)

```
events @2s : 301 / 311 / 301           (target confirmed flooding)
disposing  : target still flooding, NO pre-kill (the probe-07 crash scenario)...
detached   : clean Dispose under flood (no segfault)
target post-detach : alive (resumed un-debugged)
PROBE 08 PASSED — quiescent detach is clean under a continuous callback flood; target survived.
EXIT=0
```

The crash was 2/2 reproducible before the fix (probe 07 runs 1 + 2); the fix is 3/3 clean. Probe 07 (continue-loop, with its own pre-kill teardown) still passes — 589 callbacks, EXIT 0 — so no regression.

## What this proves

1. **Synchronize-before-detach is the correct teardown.** The finding-14 root cause (Detach races the queued-callback flush) is closed by removing the concurrency, not by lifetime games. `Stop` is the single necessary call.
2. **The escalation tiers were unnecessary.** The limit doc proposed `Stop` → `SetAllThreadsDebugState(SUSPEND)` → drain `HasQueuedCallbacks` → `Detach`. Empirically, `Stop` alone suffices on CoreCLR 10 / macOS-arm64: it halts dispatch hard enough that the held queue is discarded cleanly by `Detach`. Cheapest-falsifiable-hypothesis-first paid off — no drain loop, no thread-suspend, no timing heuristic.
3. **Detach leaves the target running.** Post-detach the target is still a live .NET process (diagnostic port present) — detach is detach, not kill, even from a synchronized state under load.

## Scope / residual

- Validated on the adversarial flooding target (continuous thread + exception churn). Real DrHook targets are far quieter, so this is a strict over-test of the teardown path.
- `Stop(0)` return HRESULT is not inspected — `Quiesce` is best-effort (a failure falls through to `Detach`). If a future target shows `Stop` failing under some state, inspect/handle there; not observed here.
- The continue-loop's *stopping*-event handling (breakpoint hit / step complete suppressing the auto-Continue) remains the next Phase 2 increment — independent of this teardown work.

## References

- Probe: `poc/drhook-engine/08-quiesce-detach-smoke.cs`
- Fixture: `fixtures/08-quiesce-detach-osx-arm64-20260521T222213Z.txt`
- Engine: `src/DrHook.Engine/DebugSession.cs` (`Quiesce()` + Dispose ordering)
- Finding 14 (the crash this resolves), Finding 12 (CallbacksQueue pattern), Finding 10 (probe 05: quiet detach was already clean — this extends it to the busy case)
- Limit: `docs/limits/drhook-clean-detach.md` → Resolved
- Mercury session 2026-05-21 observation `probe-08-passed`
