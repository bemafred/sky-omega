# Finding 72: ADR-008 Increment 4a — Phase 8 mass promotion (substrate-correctness scenarios that work with existing integration target)

**Status:**   ADR-008 Increment 4 deliverable, Phase 8a subset. Four substrate-correctness scenarios from probes 42, 44 (phase B), 45 promoted to integration tests using the discipline-aligned natural-exit pattern (Increment 3 / finding 71). Plus existing `AttachDisposeTest` (substrate Dispose during target's brief work). Each scenario validated across MTP + VSTest target shapes (8 tests total). **8/8 PASS in 8.1 s on macOS-arm64**, decisively scaling past the historical MCH-RE-3 fear-threshold of ~6 sessions per MSTest exe (which finding-67/68 already disproved at substrate level). Promotion of probes 41 + 43 deferred to a future Increment 4b — they require target redesign (breakpoint-friendly methods, STOPPING-event-generating bodies).
**Date:**     2026-05-26

## What was promoted

Four new integration test classes, each with one MTP variant + one VSTest variant:

| Test class | Substrate-correctness scenario | Probe origin |
|---|---|---|
| `WorkerExceptionTest` | Throwing sink → WorkerException anomaly + dead-worker WaitForStop returns null + clean Dispose | Probe 45 |
| `InformationalFloodTest` | Dispose during _resumeHandler-active flood; zero WorkerException, zero WorkerSilentBreak | Probe 42 (redesigned) |
| `PauseDisposeTest` | Pause + Dispose without Resume; exactly 1 WorkerSilentBreak; no crash | Probe 44 phase B |
| (existing) `AttachDisposeTest` | Substrate Dispose during target's brief work; natural exit via Stage 1 SIGTERM | (baseline) |

Each test class follows the Increment 3 pattern from finding 71:
- `using Process bootstrap = TargetSpawn.{Mtp|Vstest}(...)`
- `int pid = TargetSpawn.ExtractPid(bootstrap, ...)`
- `using DebugSession session = DebugSession.AttachAndOwn(pid, sink, [naturalExitTimeout])`
- Brief observation window
- Substrate-correctness assertions (anomaly counts, WaitForStop behavior, etc.)
- Implicit Dispose at end of using block
- `WaitForExit(timeout)` Layer 1 discipline assertion

Shared helpers `IntegrationTargetPaths.cs` + `TargetSpawn.cs` keep spawn / PID-extraction / path-resolution in one place. The existing 2 tests (`AttachDisposeTest`, `VstestAttachDisposeTest`) were refactored to use these helpers.

## Validation

```
$ ./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests --output Detailed

MSTest v4.2.3 (UTC 5/14/2026) [osx-arm64 - .NET 10.0.0]
passed AttachAndOwn_MtpTarget_BriefWork_NaturalExit (489ms)
passed AttachAndOwn_MtpTarget_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak (507ms)
passed AttachAndOwn_VstestTestHost_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak (1s 162ms)
passed AttachAndOwn_MtpTarget_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly (498ms)
passed AttachAndOwn_VstestTestHost_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly (1s 177ms)
passed AttachAndOwn_VstestTestHost_BriefWork_NaturalExit (1s 159ms)
passed AttachAndOwn_MtpTarget_ThrowingSink_WorkerExceptionAnomalyFires (1s 354ms)
passed AttachAndOwn_VstestTestHost_ThrowingSink_WorkerExceptionAnomalyFires (1s 730ms)

Test run summary: Passed!
  total: 8, failed: 0, succeeded: 8, duration: 8s 134ms
```

Per-test durations 489 ms – 1730 ms. Total suite 8.1 s. Well within reasonable CI budget.

## MCH-RE-3 fear empirically dismissed

Prior session (finding 67) hit a SIGBUS during a Phase 8 mass-promotion attempt at ~6 sessions per MSTest exe; I mis-framed it as "MCH-RE-3 substrate accumulation bug." Probe 48/48b empirically disproved that at substrate level — substrate handles 10+ sessions in same probe-host cleanly. Finding 67 attributed the MSTest SIGBUS to caller-side lifecycle violations (test code calling `Process.Kill` on a Borrowed-leave-running target).

This finding empirically confirms the diagnosis: **8 substrate sessions in one MSTest exe execute cleanly when the discipline-aligned pattern is followed.** No SIGBUS, no SIGSEGV, no crash, no flakiness. The "MCH-RE-3" never existed at substrate level; the prior failure was the substrate honestly crashing because inputs (test code) violated the Borrowed contract.

ADR-008's Increment 1-3 work + the natural-exit + AttachAndOwn-with-discipline pattern make Phase 8 promotion straightforward.

## ADR-008 scope deferred to Increment 4b

Two test classes from ADR-008's Phase 8 list remain:

- **`AnomalyInjectionTest`** (probe 41): substrate's DepthClamped anomaly fires when caller invokes `GetLocals(depth=999)` past `MaxInspectionDepth = 10`. Requires:
  - A target [TestMethod]/[Fact] with locals (specifically, a method whose stack frame holds variables we can inspect at depth).
  - The test sets a breakpoint on that method, waits for hit, calls `session.GetLocals(depth=999)`, asserts DepthClamped anomaly.
  - Different target [TestMethod] body needed than current `RunBriefObservableWork`.

- **`ConcurrentPauseStopTest`** (probe 43): substrate's pump serialises concurrent PauseRequest + STOPPING events correctly. Requires:
  - A target [TestMethod]/[Fact] that throws/catches in a loop to generate Exception (STOPPING) callbacks.
  - The test attaches, issues Pause concurrently with the STOPPING flood, asserts Pause stop arrives.
  - Different target [TestMethod] body needed than current `RunBriefObservableWork`.

Both require adding new [TestMethod]/[Fact]s to the integration target projects with specific shapes. The pattern from this finding (Increment 4a) extends cleanly — just need different target methods + MTP `--filter` arg to select which method to run.

Deferred because they compound substrate work + target work; finding 71's pattern is best validated with the simpler scenarios first (this finding).

## Files added / modified

### New
- `tests/DrHook.Engine.IntegrationTests/IntegrationTargetPaths.cs` — shared path-resolution helpers
- `tests/DrHook.Engine.IntegrationTests/TargetSpawn.cs` — shared spawn + PID-extraction helpers
- `tests/DrHook.Engine.IntegrationTests/WorkerExceptionTest.cs` — probe 45 promoted
- `tests/DrHook.Engine.IntegrationTests/InformationalFloodTest.cs` — probe 42 promoted
- `tests/DrHook.Engine.IntegrationTests/PauseDisposeTest.cs` — probe 44 phase B promoted

### Modified
- `tests/DrHook.Engine.IntegrationTests/AttachDisposeTest.cs` — refactored to use shared helpers
- `tests/DrHook.Engine.IntegrationTests/VstestAttachDisposeTest.cs` — refactored to use shared helpers

### Substrate code
- Unchanged. Increment 4a is pure integration-test promotion; substrate semantics from Increments 1-3 already in place.

## Cross-references

- [ADR-008](../../../docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) Increment 4 — this finding is the Phase 8a subset.
- [finding 67](67-lifecycle-discipline.md) — discipline that this promotion pattern enforces.
- [finding 71](71-increment3-integration-target-redesign.md) — natural-exit + WaitForExit pattern this finding extends.
- [finding 69](69-increment1-substrate-api.md) — substrate API that makes the discipline-aligned tests work.

## What's next

- **Increment 4b** — promote probes 41 + 43 (AnomalyInjectionTest, ConcurrentPauseStopTest). Requires adding new [TestMethod]/[Fact] bodies to MTP/VSTest integration target projects (breakpoint-friendly + throw/catch shapes).
- **Increment 5** — ADR-007 amendment closing Phase 1 substrate-correctness arc at integration-enforcement level; ADR-008 closure.
