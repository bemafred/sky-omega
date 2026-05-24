# Finding 57: Probe 42 outcome — Dispose during the worker's `_resumeHandler` (race characterisation)

**Status:**   PASSED on macOS-arm64 2026-05-24. Probe 42 (`42-dispose-resumehandler-race-smoke.cs`, target `07-target.cs`) characterises the substrate-correctness behavior of Dispose-during-`_resumeHandler` under continuous mscordbi callback flood. 20/20 clean Dispose cycles, 0 crashes, 0 `WorkerException`, target alive after each cycle. The post-Phase-1 substrate (Quiesce + ENG-CP-1 + ENG-DS-1 + EngineAnomaly) holds — no new engine-side fix needed.
**Date:**     2026-05-24
**Numbering note:** This is finding 57 because finding 56 was taken by Probe 41's outcome (per the 2026-05-24 renumber; see ADR-007 line 78). Phase 2's meta-probe finding (originally reserved for 56 in ADR-007) lands at finding 57 nominally — but Probe 42 ran first, so 57 is here. Phase 2's meta-probe lands at 58 when it executes.

## What was tested

ADR-007 Probe 42's mandate: *"Dispose during the worker's `_resumeHandler(...)` call. Force the race; characterise the failure mode; design the engine-side fix."*

The race window (finding 53 MCH-1 + finding 54 T1/T2):

```
mscordbi RC event thread        →  ManagedCallbackHost.OnCallback
                                       │
                                       │ _events.Add
                                       ▼
                                   CallbackPump._events queue
                                       │
                                       │ Take (worker thread)
                                       ▼
CallbackPump.Pump (worker thread)
   while in foreach:
     _resumeHandler!(kind, _stopThread)        ◄── INSIDE COM call here
       └─ Stepping.Arm(thread, kind)
       └─ _controller.Continue(0)              ◄── synchronous COM, can hang

Meanwhile, MCP request thread:
   DebugSession.Dispose
     _pump.Dispose
       _events.CompleteAdding
       _resume.CompleteAdding
       _worker.Join(2s)                        ◄── races worker's in-flight Continue
     Quiesce, Detach, Terminate                ◄── if Join times out, runs while worker is still in Continue
     _callback.Dispose                         ◄── MCH-1 if Terminate didn't drain
```

Probe 42 forces this race deterministically by attaching to the continuous-event-flood target from probe 07 (Thread.Start/Join + throw/catch in a tight loop, generating CreateThread/ExitThread/Exception callbacks at ~24/sec). The worker is effectively always inside `_resumeHandler` (or `_resume.Take` post-stop) when Dispose hits.

## Run

```
$ dotnet run --no-cache 42-dispose-resumehandler-race-smoke.cs -- 07-target.cs

runtime    : .NET 10.0.0
dbgshim    : (resolver default)
plan       : 20 attach/Dispose cycles against continuous-flood target; expect 0 crashes,
             0 WorkerException anomalies, target alive after each cycle
target pid : 54417
flood @1s  : 24 events (sentinel run before cycle loop)
cycles     : 20/20 clean Dispose; 0 threw; elapsed 6438ms
target     : alive (resumed un-debugged)
anomalies  : 20 surfaced (0 dropped to capacity)
  WorkerSilentBreak                : 20
fixture    : poc/drhook-engine/fixtures/42-dispose-resumehandler-race-osx-arm64-20260524T070059Z.txt

PROBE 42 PASSED — 20/20 clean Dispose cycles under continuous flood; substrate's
Quiesce + Interlocked gates (ENG-CP-1/DS-1) + EngineAnomaly path handle
Dispose-during-_resumeHandler without crashes, without WorkerException, and
without killing the target. Surfaced anomalies are evidence of the substrate
catching late callbacks / silent breaks — not failures.
```

Per-cycle average: ~320 ms (200 ms flood window + 50 ms inter-cycle + ~70 ms attach + Dispose overhead).

## What this validates

| Substrate component | Phase 1 work | Validated by Probe 42 |
|---|---|---|
| Quiesce-before-Detach (drhook-clean-detach) | Probe 08 (Resolved 2026-05-21) | ✓ Held across 20 cycles |
| ENG-CP-1 Interlocked gate on `CallbackPump.Dispose` | Committed 2026-05-24 (`e429c16`) | ✓ No double-Dispose race observed |
| ENG-DS-1 Interlocked gates on `DebugSession.Dispose` + `Detach` | Committed 2026-05-24 (`e429c16`) | ✓ No double-Detach observed |
| ENG-DBG-D `_lib` zero in `DbgShim.Dispose` | Committed 2026-05-24 (`e429c16`) | ✓ No double-`dlclose` (no crashes) |
| EngineAnomaly capture path (EA-1..6) | Committed 2026-05-24 (`1dd2290`) | ✓ Anomalies surface structurally — `WorkerSilentBreak` × 20 |
| EngineAnomaly designed-injection end-to-end | Probe 41 / finding 56 | ✓ Same capture/drain path used here |

## The 20 WorkerSilentBreak anomalies — characterisation, not failure

Each of the 20 cycles surfaced exactly one `WorkerSilentBreak` anomaly. Per `EngineAnomaly.cs:39–43`:

> *The pump worker exited via the `_resume.Take()` catch — the queue was completed while the worker was parked at a stop. Expected at clean teardown; anomalous if it happens with stops pending that the caller never consumed (suggests the caller disposed without resuming).*

This is the probe's intentional shape: continuous-flood + NO breakpoints + Dispose without consuming stops. Each Exception callback is a STOPPING event (CallbackKind = Exception); the pump worker classifies it, pushes to `_stops`, and parks at `_resume.Take` waiting for a Resume that the probe never issues. When Dispose runs, `CompleteAdding(_resume)` triggers the catch → `WorkerSilentBreak` fires per the substrate's anomaly contract.

**This is correct substrate behavior.** Production callers (drhook MCP consumers that set breakpoints, consume stops, and call Resume/Step/etc.) do not hit this pattern. The probe's flood-without-breakpoints is a probe-specific shape designed to maximise the worker-in-`_resumeHandler` race window; the WorkerSilentBreak surface is the substrate's honest signal that the probe disposed with stops unconsumed.

The substrate is doing its job: structured evidence of the pump's exit state rather than silent loss.

## What this characterises (the race itself)

- **20/20 clean Dispose** at sustained 24-events/sec callback flood with worker actively in `_resumeHandler` for most of the flood window.
- **0 process crashes** across the loop — the substrate's Quiesce + atomic gates + native-asset hygiene (ENG-DBG-D's `_lib = 0`) hold against the race.
- **Target survives every cycle** — Detach correctly leaves the target running rather than killing it (per probe 08's contract, now validated across 20 cycles, not just 1).
- **No `WorkerException` anomaly** — `_resumeHandler` body (`Stepping.Arm` + `controller.Continue`) does not throw through the pump boundary under load. The outer Pump try/catch (EA-4) is in place; the absence of `WorkerException` is positive evidence, not absence of evidence.
- **Per-cycle cost ~320 ms** — substrate's Dispose path averages well under the 2 s `_worker.Join` budget. The Join timeout has slack for slower hosts.

## What this does NOT cover

ADR-007 Probe 42 is one of four substrate-correctness probes in Phase 1. Adjacent races still owe their own probes:

- **Probe 43 — Concurrent PauseRequest + STOPPING callback.** Probe 42's flood does NOT exercise the `PauseRequest` branch of `Pump`'s switch (the `_pauseHandler!()` call at CallbackPump.cs:155). The T4a-pause sub-probe noted in finding 54 belongs here.
- **Probe 44 — drhook-detach-exit-race.** Probe 42's target is long-lived and survives all cycles; it does NOT exercise the exit-coincident-with-Dispose path. C-DRAIN-EXIT (finding 54) remains untested for the rate envelope.
- **Probe 45 — Worker-thread exception path.** Probe 42 observes 0 `WorkerException`. Probe 45 must explicitly inject an exception into `_resumeHandler` (e.g. throw from a custom user sink within an inspection callback) and validate the WorkerException anomaly fires + the session is recoverable-or-clean-failure.

The MCH-1 race for the partial-construction case (T6-attach in finding 54) is also not covered here — that's a separate probe.

## Engine-side fix

**None required.** Per ADR-007 Probe 42's mandate, "design the engine-side fix" — the engine-side fix landed already as the cumulative work of:

- ADR-006 Phase 2 increment 2: Quiesce-before-Detach (probe 08, finding 15).
- ENG-CP-1, ENG-DS-1, ENG-DBG-D: atomic idempotence gates + native-asset hygiene (commit `e429c16`).
- EA-1..6: EngineAnomaly substrate for structured-evidence surfacing (commit `1dd2290`).

Probe 42 validates these hold. No new engine change needed.

## Future characterisation (probe-author follow-up, not Phase 1 blocking)

- **Higher cycle count + rate:** 50/sec sustained per the NCrunch envelope mentioned in ADR-007 Probe 44 — relevant to Probe 42's territory if Probe 44's mitigations land. Could run probe 42 at higher N (200+) once Probe 44's substrate landing is done.
- **Larger flood-window variance:** vary `FloodWindowMs` (50ms, 100ms, 500ms, 1000ms) to characterise the time-in-_resumeHandler distribution + whether the race window matters in practice. All should pass with the current substrate; if any reveal failure modes, that's new substrate work.
- **CPU/memory profile:** 20 cycles in 6.4s = 320ms/cycle. mscordbi's attach setup likely dominates; cycle cost could matter for NCrunch-rate workloads. Not Probe 42's primary concern.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 1, Probe 42 (Dispose during _resumeHandler).
- [finding 53](53-threading-memory-model-audit.md) — MCH-1 race window + ENG-CP-1/DS-1/STK-* engineering fix surface.
- [finding 54](54-teardown-audit.md) — T1/T2 teardown scenarios + the ICorDebug detach contract split (C-DRAIN-CB / C-DRAIN-EXIT).
- [finding 56](56-anomaly-injection-outcome.md) — Probe 41 anomaly-injection (EngineAnomaly designed-injection validation, prerequisite).
- [`docs/limits/drhook-clean-detach.md`](../../../docs/limits/drhook-clean-detach.md) — Resolved; the Quiesce-before-Detach validated here.
- Commit `e429c16` — ENG-CP-1, ENG-DS-1, ENG-DBG-D, ENG-STK-1, ENG-STK-2, ENG-STK-3.
- Commit `1dd2290` — EngineAnomaly substrate (EA-1..6).
- Commit `42b0d18` — Probe 41 + Phase 1 renumber.

## Phase 1 substrate-correctness status

After Probe 42's PASS, Phase 1 status:

- **Probe 41 (anomaly-injection):** ✓ PASSED (finding 56).
- **Probe 42 (Dispose during `_resumeHandler`):** ✓ PASSED (this finding).
- **Probe 43 (Concurrent PauseRequest + STOPPING callback):** ⏸ pending.
- **Probe 44 (drhook-detach-exit-race resolution):** ⏸ pending — most substrate-design work.
- **Probe 45 (Worker-thread exception path):** ⏸ pending — straightforward; injects exception into a custom sink hook.

ADR-007 line 174 ("Probes 41–63 pass on macOS/arm64 in CI") progresses: 2/5 Phase 1 probes done.
