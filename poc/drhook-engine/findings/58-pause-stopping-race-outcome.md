# Finding 58: Probe 43 outcome — Concurrent PauseRequest + STOPPING callback (serialisation validation)

**Status:**   PASSED on macOS-arm64 2026-05-24. Probe 43 (`43-pause-stopping-race-smoke.cs`, target `07-target.cs`) validates the substrate's `_events` queue serialises concurrent Adds from mscordbi (STOPPING + Informational stream from probe 07's flood) and the MCP request thread (synthetic PauseRequest via `RequestPause`) correctly. 10/10 Pause requests surfaced as Pause stops within 5 s budget; 10 Exception stops drained alongside; 0 anomalies; 0 crashes; target alive.
**Date:**     2026-05-24
**Numbering note:** Finding 58 here means Phase 2's meta-probe finding now lands at 59 (ADR-007 line 78 updated to reflect this). Findings are sequential as work happens; ADR-reserved slots shift when probes execute out of plan order.

## What was tested

ADR-007 Probe 43's mandate: *"Concurrent PauseRequest + STOPPING callback. Verify the pump serialises correctly, or design the serialisation."*

The race window (finding 53 contract #1 + finding 54 T4):

```
mscordbi event thread          →  ManagedCallbackHost.OnCallback (STOPPING + Informational)
                                       │
                                       │ _events.Add (mscordbi thread)
                                       ▼
                                   CallbackPump._events queue   ◄── BlockingCollection<CallbackEvent>
                                       ▲
                                       │ _events.Add (MCP thread, via DebugSession.Pause → pump.RequestPause)
                                       │
MCP request thread             →  pump.RequestPause              ◄── synthetic CallbackKind.PauseRequest

Pump worker thread:
   foreach (e in _events.GetConsumingEnumerable())   ◄── single consumer, FIFO
     dispatch by Kind:
       Informational → user sink + auto-Continue
       PauseRequest → _pauseHandler (controller.Stop) → publish Pause stop → park at _resume.Take
       STOPPING     → publish stop → park at _resume.Take
```

The contract — *by construction*, single consumer + FIFO via `BlockingCollection<T>` — should serialise all Adds regardless of source thread. Probe 43 exercises this empirically: concurrent Adds from two threads under load, with strict per-cycle assertions on Pause-stop surfacing.

## Run

```
$ dotnet run --no-cache 43-pause-stopping-race-smoke.cs -- 07-target.cs

runtime    : .NET 10.0.0
dbgshim    : (resolver default)
plan       : 10 Pause cycles against continuous-flood target; each must surface as exactly one
             Pause stop within 5s
target pid : 55993
attached   : DebugSession established
flood @0.5s: 24 events (sentinel before Pause loop)
cycle  1/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    3ms
cycle  2/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    0ms
cycle  3/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    0ms
cycle  4/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=   30ms
cycle  5/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    0ms
cycle  6/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    0ms
cycle  7/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    0ms
cycle  8/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=   27ms
cycle  9/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    0ms
cycle 10/10 : pause-stop-at-stop-# 2  exception-backlog=  1  elapsed=    0ms

overall    : 10 Pause stops + 10 Exception stops + 0 other; elapsed 1647ms
anomalies  : 0 surfaced (0 dropped to capacity)
target     : alive (resumed un-debugged)

PROBE 43 PASSED — 10/10 Pause requests surfaced as Pause stops under continuous
mscordbi STOPPING flood; pump's single-consumer FIFO queue serialises concurrent
_events.Add calls correctly. 10 Exception stops consumed in the drain — evidence
of the concurrent stream.
```

Fixture file: `poc/drhook-engine/fixtures/43-pause-stopping-race-osx-arm64-20260524T072016Z.txt`.

## What this validates

| Substrate claim | Validation |
|---|---|
| `BlockingCollection.Add` is thread-safe for concurrent Adds from different threads | ✓ 10 Pause Adds from MCP thread + flood Adds from mscordbi thread, no lost events |
| Single-consumer FIFO via `GetConsumingEnumerable` | ✓ Every cycle: Pause stop arrives in queue order behind the in-flight Exception |
| `RequestPause` synthetic event triggers `_pauseHandler` (controller.Stop) | ✓ Pause stop published exactly once per RequestPause |
| Pump's switch correctly classifies CallbackKind.PauseRequest vs STOPPING | ✓ 10 + 10 stops matched expected kinds; 0 misclassifications (`totalOtherStops == 0`) |
| Pump survives concurrent dispatch under load | ✓ 0 WorkerException, 0 anomalies, target alive |

## Observed substrate behavior — the deterministic backlog-of-1

Every cycle showed the same pattern: **pause-stop-at-stop-#2, exception-backlog=1**. Reading the timing:

- We call `session.Pause()` — enqueues PauseRequest on `_events`.
- mscordbi has just enqueued one Exception event ahead of our PauseRequest (the flood is ~24 events/sec ≈ 1 event per 42 ms; the previous Exception is fresh at the head).
- Pump processes the Exception first → publishes Exception stop → parks at `_resume.Take`.
- We drain Exception via `WaitForStop` → Resume.
- Pump processes our PauseRequest → calls `_pauseHandler` (controller.Stop) → publishes Pause stop → parks.
- We drain Pause → Resume.

Per-cycle elapsed mostly 0–30 ms. The substrate's interleaving is tight: a Pause request is observed within tens of milliseconds even under a continuous 24/sec flood.

**This deterministic backlog-of-1 is itself characterisation evidence** — the flood's inter-event gap (~42 ms) is large enough that we typically catch exactly one event ahead of our Pause in the queue. Under heavier flood (say 200 events/sec) the backlog would grow.

## What this does NOT cover

- **Multi-thread Pause from concurrent callers.** Probe 43 issues Pause serially from the main thread. The race tested is mscordbi-thread + MCP-thread, not MCP-thread-A + MCP-thread-B. The DS-2 / ESS-1 race-window from finding 53 is upstream of that case and depends on MCP SDK serialization (not yet characterised — separate probe queue).
- **`controller.Stop(0)` hang behavior (T4a-pause).** Finding 54 noted this as a sub-probe under Probe 43: if Stop can hang on a wedged debuggee, the 2 s `_worker.Join` timeout in `Dispose` leaves the worker alive. Probe 43 observed Stop returning promptly every cycle (cycle elapsed mostly 0–30 ms) under the normal flood scenario; the wedged-debuggee shape isn't tested here. **Status:** queue T4a-pause as a separate probe (low priority — no evidence yet that wedged-debuggee + pauseHandler is reachable in practice).
- **PauseRequest while pump is INSIDE `_pauseHandler` from a previous PauseRequest.** The pump processes PauseRequests one at a time; nested PauseRequests queue. Same FIFO contract as STOPPING-after-PauseRequest. Not separately exercised because the substrate path is identical.

## Engine-side fix

**None required.** The substrate's design (single-consumer pump + `BlockingCollection<T>` FIFO) correctly serialises concurrent `_events.Add` calls. The empirical contract holds at 10 cycles + 24/sec flood; the design contract holds by construction.

Per ADR-007 Probe 43's mandate ("Verify the pump serialises correctly, or design the serialisation"): **verified.** No design work.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 1, Probe 43 (Concurrent PauseRequest + STOPPING callback).
- [finding 53](53-threading-memory-model-audit.md) — `_events` BlockingCollection contract + thread-graph identifying mscordbi-thread vs MCP-thread concurrent Add sources.
- [finding 54](54-teardown-audit.md) — T4 (Pause mid-handler) scenario + T4a-pause sub-probe noted.
- [finding 56](56-anomaly-injection-outcome.md) — EngineAnomaly capture path used here (0 anomalies confirms substrate behavior is clean for this probe shape).
- [finding 57](57-dispose-resumehandler-race-outcome.md) — Probe 42 outcome; same anomaly capture surface, different probe shape (Probe 42 did NOT drain, so it surfaced WorkerSilentBreak × 20; Probe 43 DOES drain, so 0 anomalies — the contrast confirms the EngineAnomaly's behavior is correct per its documented contract).
- Commit `e429c16` — ENG-CP-1/DS-1/DBG-D/STK-* engineering fixes (validated here under concurrent Add load).
- Commit `1dd2290` — EngineAnomaly substrate (anomaly count surface used here).

## Phase 1 substrate-correctness status

After Probe 43's PASS, Phase 1 progress:

- **Probe 41 (anomaly-injection):** ✓ PASSED (finding 56).
- **Probe 42 (Dispose during `_resumeHandler`):** ✓ PASSED (finding 57).
- **Probe 43 (Concurrent PauseRequest + STOPPING):** ✓ PASSED (this finding).
- **Probe 44 (drhook-detach-exit-race resolution):** ⏸ pending — most substrate-design work (must design Attached-session path per finding 54).
- **Probe 45 (Worker-thread exception path):** ⏸ pending — straightforward; `WorkerException` anomaly already wired (EA-4), probe must inject a throw into the resume-handler path and assert the anomaly + clean recovery.

3/5 Phase 1 substrate-correctness probes done.
