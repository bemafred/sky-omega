# Finding 60: Probe 45 outcome — Worker-thread exception path (Phase 1 substrate-correctness CLOSE)

**Status:**   PASSED on macOS-arm64 2026-05-24. Probe 45 (`45-worker-exception-smoke.cs`, target `07-target.cs`) validates the substrate's outer Pump try/catch (EA-4) handles a thrown exception out of the user sink path with all four required guarantees: WorkerException anomaly surfaces (exactly 1), subsequent WaitForStop returns null cleanly (no hang), Dispose completes cleanly with dead worker, target alive (detach-leave-running for Attached). **Closes the Phase 1 substrate-correctness arc: 5/5 probes done.**
**Date:**     2026-05-24
**Numbering note:** Finding 60. Phase 2 meta-probe finding shifts again 60 → 61 (ADR-007 line 78 to be updated).

## What was tested

ADR-007 Probe 45's mandate: *"Worker-thread exception path. If `_resumeHandler` throws, the worker dies silently and future `WaitForStop` hangs. Inject the throw, validate the recovery (worker survives + surfaces, or fails session cleanly with deterministic error). The `WorkerException` anomaly is already wired (EA-4); the probe validates the surfacing under live conditions."*

### Injection mechanism

The Pump's Informational branch invokes `_userSink.OnEvent(e.Name)` for each Informational mscordbi callback. The first such callback against `07-target.cs` (a continuous flood target) is `CreateProcess`. Probe 45's `ThrowingSink.OnEvent` throws `InvalidOperationException` on its first invocation; subsequent calls no-op (counter armed once).

The throw propagates up through Pump's foreach loop, hits the outer try/catch (EA-4), which constructs and dispatches a `WorkerException` anomaly via `_userSink.OnAnomaly`. The worker then exits the foreach naturally (the catch block doesn't restart the loop). Background-thread death is contained by the substrate's catch — no process crash, no `AppDomain.UnhandledException`.

### Substrate-correctness guarantees validated

| # | Guarantee | Mechanism | Validation |
|---|---|---|---|
| 1 | WorkerException anomaly surfaces structurally | EA-4 outer try/catch in Pump calls `_userSink.OnAnomaly` with the typed record | ✓ Exactly 1 anomaly surfaced within 110 ms |
| 2 | Subsequent WaitForStop returns null cleanly | Dead worker means no new `_stops.Add` calls; `BlockingCollection.TryTake` honors the caller's timeout | ✓ Returned null at 2007 ms against 2000 ms timeout (timeout-bounded, not hang) |
| 3 | Dispose completes cleanly with dead worker | `pump.Dispose`'s `CompleteAdding` + `Join` are no-ops against an already-exited thread | ✓ Dispose elapsed 10 ms (no Join wait — worker was already gone) |
| 4 | Target alive after Dispose (detach-leave-running) | DebugSession's Attached-path substrate change (finding 59) still applies even with dead worker | ✓ Target alive after Dispose |

## Run

```
$ dotnet run --no-cache 45-worker-exception-smoke.cs -- 07-target.cs

runtime    : .NET 10.0.0
dbgshim    : (resolver default)
plan       : Attach with ThrowingSink → first OnEvent throws → substrate Pump catch (EA-4)
             fires WorkerException → assert anomaly surfaces, WaitForStop returns null cleanly,
             Dispose completes, target alive.
target pid : 66142
attached   : DebugSession established
injection  : OnEvent invoked at 110ms (throwing-sink fired)
anomalies  : 1 surfaced after 110ms (0 dropped)
  WorkerException                  : 1
  WorkerException details:
    thread=pump-worker  operation=Pump
    observed=InvalidOperationException: probe-45 injected throw on first OnEvent ('CreateProcess')
post-death : WaitForStop returned null after 2007ms (timeout was 2000ms)
dispose    : completed in 10ms (dead worker, no Join wait)
target     : alive (resumed un-debugged)

PROBE 45 PASSED — substrate's outer Pump try/catch (EA-4) caught the injected exception,
fired exactly 1 WorkerException anomaly via OnAnomaly, the dead worker doesn't lie
(WaitForStop returns null cleanly after timeout), Dispose completes cleanly, target survives.
The Worker-thread exception path closes the Phase 1 substrate-correctness arc.
```

Fixture file: `poc/drhook-engine/fixtures/45-worker-exception-osx-arm64-20260524T093412Z.txt`.

## What the substrate gets right

- **Exception containment.** A throwing user sink does not crash the process. The substrate's outer try/catch isolates user-code faults from substrate state.
- **Structured surfacing.** The anomaly carries `thread=pump-worker`, `operation=Pump`, and the exception message inline in `observed` — enough context for an AI consumer to diagnose the substrate-correctness boundary that fired without re-running.
- **State honesty.** Post-death `WaitForStop` returns null at the timeout rather than hanging forever. The substrate's behavior is bounded and predictable even in the failure mode.
- **Teardown robustness.** Dispose with a dead worker is fast (10 ms) and clean — `pump.Dispose`'s contract holds against either a live or already-dead worker thread.

## The injection point — why OnEvent and not `_resumeHandler`

ADR-007 Probe 45's text targets `_resumeHandler` specifically. In practice, both `_resumeHandler` and the user sink callbacks (`OnEvent`, `OnLog`) are invoked from inside Pump's `try` block:

- `_userSink.OnEvent(e.Name)` at line 137 (Informational branch).
- `_resumeHandler!(ResumeKind.Continue, 0)` at line 142 (Informational branch).
- `_pauseHandler!()` at line 149 (PauseRequest branch).
- `_resumeHandler!(kind, _stopThread)` at lines 155 / 171 (PauseRequest + STOPPING branches).

Any exception from any of these reaches the same outer catch and produces the same `WorkerException` anomaly. Injection via `OnEvent` is the cleanest test because it's caller-controlled (user provides the sink); `_resumeHandler` is constructed inside `DebugSession.FromCordbg` as a closure over `Stepping.Arm` + `controller.Continue`, which the public API doesn't expose. Same outer catch validates either path.

`_pauseHandler` would require an additional `RequestPause` to exercise; not pursued here because the catch is the same.

## What this does NOT cover

- **OnAnomaly throwing inside the catch.** The probe's `ThrowingSink.OnAnomaly` does NOT throw — if it did, the substrate's catch would propagate the second exception out of Pump uncaught, and the IsBackground=true worker dying with unhandled exception would terminate the process. The substrate has no second-level catch around OnAnomaly. **Worth a substrate-discipline note** (see follow-up below).
- **Repeated injections per session.** Probe 45 injects once; the worker dies. Re-injecting requires re-attaching (substrate doesn't restart the worker after WorkerException). Not in scope for this probe — the contract is "fail session cleanly with deterministic error," which matches.
- **Multi-thread interaction.** Probe 45 is single-threaded apart from mscordbi + the pump worker. Concurrent MCP-thread activity (e.g., Pause + WorkerException) is the territory of Probe 43 (which validated the FIFO contract on _events).

## Engineering follow-up surfaced (not blocking)

- **WE-OA-1: `OnAnomaly` is the substrate's last-resort surface — if it throws, process dies.** Two design options: (1) defensive try/catch around the OnAnomaly call inside Pump's catch, swallowing-by-design; (2) document the contract loudly in `IDebugEventSink.OnAnomaly`'s XML doc that implementations MUST NOT throw. Option 2 is the existing posture (XML doc says "implementations must be thread-safe"); strengthening to "MUST NOT throw" is a one-line doc change. Option 1 hides bugs in user sink implementations.

  **Recommendation:** strengthen the XML doc (option 2). Substrate doesn't hide consumer bugs; consumer is told the contract explicitly.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 1, Probe 45.
- [finding 53](53-threading-memory-model-audit.md) — race window MCH-1 + Pump worker contract.
- [finding 56](56-anomaly-injection-outcome.md) — Probe 41 (anomaly-injection validation; this probe reuses the same anomaly path).
- [finding 57](57-dispose-resumehandler-race-outcome.md) — Probe 42 (Dispose during `_resumeHandler`); validated 0 `WorkerException` under normal load, this probe validates 1 `WorkerException` under injection.
- [finding 58](58-pause-stopping-race-outcome.md) — Probe 43 (Concurrent PauseRequest + STOPPING).
- [finding 59](59-detach-exit-race-outcome.md) — Probe 44 (drhook-detach-exit-race resolution); detach-leave-running for Attached still applies even with dead worker (this probe validates).
- Commit `1dd2290` — EngineAnomaly substrate (the EA-4 outer Pump try/catch wired here).

## Phase 1 substrate-correctness arc — CLOSED

After Probe 45's PASS, Phase 1 substrate-correctness is **5/5 done**:

- **Probe 41 (anomaly-injection):** ✓ PASSED (finding 56).
- **Probe 42 (Dispose during `_resumeHandler`):** ✓ PASSED (finding 57).
- **Probe 43 (Concurrent PauseRequest + STOPPING):** ✓ PASSED (finding 58).
- **Probe 44 (drhook-detach-exit-race resolution):** ✓ PASSED (finding 59).
- **Probe 45 (Worker-thread exception path):** ✓ PASSED (this finding).

Per ADR-007's Validation gate: *"Probes 41–63 pass on macOS/arm64 in CI"* — five of those 23 probes are now PASSED on macOS-arm64. The remaining 18 (Phase 2 meta-probe + Phase 3 child-process attach + Phase 4 test-runner characterisation + Phase 5 substrate capabilities + Phase 6 per-variant validation + Phase 9 cross-platform) are out of Phase 1 scope and address production-suitability for test-runner-hosted debuggees.

**Phase 1 — Teardown + concurrency hardening + memory-model audit + stack-budget audit — is complete on macOS-arm64.** The substrate carries:
- 3 audit findings (53/54/55) characterising threading + teardown + stack budgets.
- 6 engineering fixes (CP-1, DS-1, DBG-D, STK-1/2/3) for the substrate-grade issues those audits surfaced.
- 1 substrate-infrastructure addition (EngineAnomaly + BoundedAnomalySink + MCP drain tool).
- 1 substrate-design addition (Attached-path detach-leave-running with Continue-loop Stop-counter drain).
- 5 substrate-correctness probes (41–45) with finding docs + fixtures.

The next ADR-007 work is Phase 2 — *Probe how to probe properly (the meta-probe)* — which characterises the integration-test promotion mechanism before any of probes 41–45 land in `tests/DrHook.Engine.IntegrationTests/`. Phase 8 then promotes; everything else (Phases 3–6, 9) is downstream of those decisions.
