# Finding 74: ADR-008 Increment 4c — ConcurrentPauseStopTest promoted; Phase 8 mass-promotion COMPLETE

**Status:**   ADR-008 Increment 4c deliverable. Probe 43 (concurrent PauseRequest + STOPPING events) promoted as the 6th substrate-correctness integration test class. Required adding a `RunThrowCatchLoop` test method to both integration targets + extending `TargetSpawn` with a `methodFilter` parameter so existing tests are unaffected. Pattern surface: arm the exception filter FIRST so substrate's `WaitForStop` auto-resumes Exception stops transparently, drain any initial Break stop, then PauseRequest → assert Pause stop arrives. Handles ordering variance between MTP (Break stop reliably first) and VSTest (Exception stop sometimes first due to substrate-setup-vs-[Fact]-start race). **12/12 integration tests PASS in ~13.1 s across 3 stable runs**.
**Date:**     2026-05-26

## What was promoted

Test class: `ConcurrentPauseStopTest`, two variants:
- `AttachAndOwn_MtpTarget_PauseDuringStoppingFlood_PauseStopSurfaces`
- `AttachAndOwn_VstestTestHost_PauseDuringStoppingFlood_PauseStopSurfaces`

Probe origin: probe 43 (concurrent PauseRequest + STOPPING) / finding 58.

## What was needed

### New `RunThrowCatchLoop` test methods

Added to both integration targets (MTP `IdleTarget.cs` + VSTest `IdleFact.cs`):

```csharp
[TestMethod /* or [Fact] */]
public void RunThrowCatchLoop()
{
    for (int i = 0; i < 50; i++)
    {
        try { throw new InvalidOperationException("drhook-integration-stopping"); }
        catch { /* first-chance Exception callback */ }
        Thread.Sleep(10);
    }
}
```

50 iterations × 10ms ≈ 500ms natural runtime. Each iteration produces an Exception (STOPPING) mscordbi callback.

### `TargetSpawn` filter mechanism

Extended `TargetSpawn.Mtp(targetExe, methodFilter?)` and `TargetSpawn.Vstest(targetProject, methodFilter?)` with an optional `methodFilter` parameter. When supplied, it generates `--filter "FullyQualifiedName~<methodFilter>"` for MTP's `--debug` arg and `dotnet test --filter ...` for VSTest. When `null` (default), no filter applied — runs all test methods in the target.

All existing 4 test classes (AttachDispose, WorkerException, InformationalFlood, PauseDispose) + AnomalyInjectionTest were updated to pass `methodFilter: "RunBriefObservableWork"`, isolating them from the new `RunThrowCatchLoop` method. Their per-test timings stayed the same (the filter just narrows what MTP/VSTest runs; substrate's behavior is unchanged).

ConcurrentPauseStopTest uses `methodFilter: "RunThrowCatchLoop"` to select the throw/catch [TestMethod]/[Fact].

### Shared substrate-correctness helper

The MTP + VSTest variants share `AssertPauseStopSurfacesUnderStoppingFlood(DebugSession session)`. This handles:

```csharp
session.ArmExceptionFilter("NoSuchTypeWillMatch");  // auto-resume Exception stops
Thread.Sleep(100);                                    // target makes progress
session.Pause();                                      // PauseRequest amid STOPPING flood
StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
if (stop?.Reason == StopReason.Break)
{
    // MTP --debug startup Break stop — drain
    session.Resume();
    stop = session.WaitForStop(TimeSpan.FromSeconds(5));
}
Assert.AreEqual(StopReason.Pause, stop.Reason);
session.Resume();
```

## Why the ordering variance

**MTP `--debug`** halts testhost via an explicit `Debugger.Break()`, then runs tests. Substrate's pump always sees the Break callback first.

**VSTest `VSTEST_HOST_DEBUG=1`** halts testhost differently — empirically, the Break callback can fire AFTER the [Fact] starts executing and throws its first exception, OR substrate's CCW setup races the [Fact] startup. The first stop substrate's pump enqueues may be Exception (not Break).

Earlier iterations of the test assumed Break first → assertion failed for VSTest variant → using block disposed substrate → substrate Dispose against a tangled VSTest tree took 11-17s and intermittently exceeded 10-15s WaitForExit budgets. The refactor (arm filter first; tolerate Break OR Pause as the first observed stop) bypasses the ordering assumption entirely.

## Substrate-correctness validated

The Pause-during-STOPPING-flood scenario:
- Target's [Fact] is in a throw/catch loop — every 10ms an Exception (STOPPING) callback arrives at substrate.
- Substrate's pump worker processes Exception callbacks; with filter armed, each Exception stop auto-resumes via WaitForStop's internal loop.
- Caller (test) calls `session.Pause()` — pump pushes PauseRequest onto `_events` BlockingCollection<T>.
- Single-consumer FIFO: worker eventually processes the PauseRequest after current event, calls `_pauseHandler` (controller.Stop), publishes Pause stop, parks at `_resume.Take`.
- Caller's `WaitForStop` returns the Pause stop (non-Exception, bypasses filter).
- Assertion: Pause stop surfaced. ✓

The substrate's pump serialises correctly. Substrate-correctness from probe 43 / finding 58 is now CI-enforced at integration scale across both MTP + VSTest.

## Validation

```
Run 1:  total: 12, failed: 0, succeeded: 12, duration: 13s 086ms
Run 2:  total: 12, failed: 0, succeeded: 12, duration: 13s 112ms
Run 3:  total: 12, failed: 0, succeeded: 12, duration: 13s 158ms
```

Three consecutive runs, all 12/12 PASS. Per-test:
- ConcurrentPauseStopTest MTP: ~450 ms
- ConcurrentPauseStopTest VSTest: ~820 ms (was 11-17s in the broken-first-iteration suite)
- All other tests: unchanged from Phase 8a + Increment 4b timings

## ADR-008 Phase 8 status — COMPLETE

| Test class | Probe | Status |
|---|---|---|
| AttachDisposeTest (× 2) | (baseline) | DONE |
| WorkerExceptionTest (× 2) | 45 | DONE (Increment 4a) |
| InformationalFloodTest (× 2) | 42 | DONE (Increment 4a) |
| PauseDisposeTest (× 2) | 44 phase B | DONE (Increment 4a) |
| AnomalyInjectionTest (× 2) | 41 | DONE (Increment 4b) |
| **ConcurrentPauseStopTest (× 2)** | **43** | **DONE (this finding)** |
| (probe 47) | 47 | Dropped as redundant |

**12/12 integration tests** covering **6 distinct substrate-correctness scenarios** × 2 runner shapes (MTP + VSTest). All passing reliably across consecutive runs. ADR-007 Phase 1 substrate-correctness arc closes at the CI-enforced level via this integration suite.

## Files changed

- `tests/DrHook.Engine.IntegrationTargets.Mtp/IdleTarget.cs`: added `RunThrowCatchLoop` [TestMethod]
- `tests/DrHook.Engine.IntegrationTargets.Vstest/IdleFact.cs`: added `RunThrowCatchLoop` [Fact]
- `tests/DrHook.Engine.IntegrationTests/TargetSpawn.cs`: added optional `methodFilter` parameter to `Mtp()` + `Vstest()`
- `tests/DrHook.Engine.IntegrationTests/ConcurrentPauseStopTest.cs` (NEW): probe 43 promoted
- `tests/DrHook.Engine.IntegrationTests/AttachDisposeTest.cs`: filter → `RunBriefObservableWork`
- `tests/DrHook.Engine.IntegrationTests/VstestAttachDisposeTest.cs`: filter → `RunBriefObservableWork`
- `tests/DrHook.Engine.IntegrationTests/WorkerExceptionTest.cs`: filter → `RunBriefObservableWork` (both variants)
- `tests/DrHook.Engine.IntegrationTests/InformationalFloodTest.cs`: filter → `RunBriefObservableWork` (both variants)
- `tests/DrHook.Engine.IntegrationTests/PauseDisposeTest.cs`: filter → `RunBriefObservableWork` (both variants)
- `tests/DrHook.Engine.IntegrationTests/AnomalyInjectionTest.cs`: filter → `RunBriefObservableWork` (both variants)

Substrate code unchanged in Increment 4c.

## Cross-references

- [ADR-008](../../../docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) Increment 4 — closed by this finding.
- [finding 58](58-pause-stopping-race-outcome.md) — probe 43 original validation.
- [finding 72](72-increment4a-mass-promotion.md) — Phase 8a (probes 42, 44B, 45 promoted).
- [finding 73](73-increment1b-release-pending-stops.md) — Increment 1b + 4b (substrate fix + probe 41 promoted).

## What's next

- **Increment 5** — ADR-007 amendment closing Phase 1 substrate-correctness at CI-enforced level; ADR-008 → Completed status.
- Substrate retire candidates: probes that have been promoted may no longer need to be maintained as file-based PoCs. Possible cleanup work.
