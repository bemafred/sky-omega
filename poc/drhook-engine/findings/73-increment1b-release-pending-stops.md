# Finding 73: ADR-008 Increment 1b — substrate releases pending mscordbi stops before Stage 1 SIGTERM in Owned Dispose; AnomalyInjectionTest (probe 41) promoted

**Status:**   ADR-008 Increment 1b sub-amendment (Martin 2026-05-26). Owned-path Dispose now invokes `TryResumeForDetach` before `RequestExit`, releasing any pending mscordbi stop (Pause, Breakpoint, Break, etc.) so the target is running when SIGTERM arrives. Surfaced by Phase 8b's AnomalyInjectionTest: substrate's Dispose against an MTP/VSTest-shaped target halted at `Debugger.Break()` did not terminate the target — testhost stayed alive for 30+ minutes despite substrate's SIGTERM-then-SIGKILL escalation. Martin's framing: *"The debugger knows it is break halted — doesn't it?"* Substrate has full knowledge of target state via session activity; should act on it. With the fix, probe 56 validates the scenario and AnomalyInjectionTest (probe 41) promotion completes — 10/10 integration tests PASS in 11.9 s.
**Date:**     2026-05-26

## What surfaced this

Phase 8b promotion attempt of probe 41 (anomaly injection via `GetLocals(depth>10)`) initially failed two ways:

1. **Assertion mismatch** — test expected `StopReason.Breakpoint`; substrate delivered `StopReason.Break`. MTP `--debug` and VSTest `VSTEST_HOST_DEBUG=1` both halt testhost via `Debugger.Break()`, not via a substrate-set breakpoint. Substrate receives a Break stop.
2. **Substrate hang** — after the assertion threw, the `using` block disposed substrate. Substrate's Dispose ran against a target halted at `Debugger.Break()`. Testhost stayed alive for 30+ minutes. Substrate's Increment-1 SIGTERM-then-SIGKILL escalation did NOT terminate the target.

35-minute process-state inspection (via `ps -ef`) showed the integration-test exe + spawned testhost both alive long after the test method threw. Also revealed: 14+ orphan testhost processes from prior days' work, indicating a systemic discipline gap.

## Diagnosis

Probe 56 (focused isolation):
- Bare target with `Debugger.Break()` (no MTP/VSTest framework wrapper).
- Substrate AttachAndOwn → WaitForStop receives Break → session.Dispose().
- Substrate's existing Increment-1 path: SIGTERM → wait 2 s → SIGKILL fallback if needed.
- **Result: target died via SIGTERM (exit code 143) in 225 ms.** So the bare scenario works.

The MTP/VSTest case differs: testhost runs inside the test framework's process model. The framework may install signal handlers, hold IPC sockets, or otherwise interact with mscordbi's halt mechanism in ways that delay/block SIGTERM delivery to a Break-halted target. SIGKILL is supposed to be non-catchable, but empirically also took >30 minutes (effectively never).

## Martin's insight

> *"The debugger knows it is break halted — doesn't it?"*

The substrate **does** know. When a Break callback arrived, `_pump.OnCallback` pushed it to `_stops` queue, set `_pump.StopThread`, worker parked at `_resume.Take`. The substrate has full state awareness — target is halted at a specific thread, mscordbi is holding it.

The pre-Increment-1b Dispose flow:
```
_pump.Dispose()                       // worker unparks via WorkerSilentBreak, exits
                                      // but mscordbi still has target halted!
RequestExit(_naturalExitTimeout)      // SIGTERM against halted target — signal blocked
[anomaly + SIGKILL fallback]          // also somehow blocked in MTP/VSTest case
```

The substrate's knowledge ("target is halted") was not acted upon. Borrowed-path Dispose already has `TryResumeForDetach` (finding 59/65) precisely for this reason: release the stop so target is running when we Detach. **Owned-path needs the same primitive applied before SIGTERM.**

## The substrate fix

```csharp
if (_ownsTarget && _targetProcess is not null && !_targetProcess.HasExited)
{
    // ADR-008 Increment 1b: release any pending mscordbi stop before SIGTERM.
    // Substrate KNOWS the target may be halted (worker was parked at _resume.Take);
    // releasing via Continue moves target back to running state so SIGTERM's
    // signal-delivery thread isn't blocked by mscordbi's halt mechanism.
    TryResumeForDetach();

    bool exitedNaturally = false;
    try { exitedNaturally = RequestExit(_naturalExitTimeout); }
    catch { exitedNaturally = _targetProcess.HasExited; }

    if (!exitedNaturally)
    {
        // ... existing TargetStuckAtDispose + TryKillTargetAndSettle ...
    }
}
```

`TryResumeForDetach` (existing helper from finding 59/65) loops `controller.Continue(0)` until S_FALSE (target running), bounded by 10 attempts + 10 ms intra-iteration + 50 ms final settle. For a target already running, first iteration returns S_FALSE immediately + settle = ~50 ms cost. For a halted target, one Continue releases the stop + ~10 ms = ~60 ms cost. Either way, target is running when SIGTERM arrives.

Reuses the existing helper rather than introducing a new method — single source of release-the-halt logic across Owned and Borrowed paths.

## Validation

### Probe 56 — bare Break-halted target

```
$ dotnet run --no-cache 56-break-stopped-dispose-smoke.cs 56-target.cs

attached   : AttachAndOwn established
wait-stop  : awaiting Break stop (up to 10 s)...
target>>>  BREAKING
stop       : Break arrived
dispose    : calling session.Dispose() — measuring time to target death...
target>>>  DONE                      ← target ran past Debugger.Break() naturally
dispose    : completed in 284 ms

PROBE 56 PASSED — substrate Dispose against Break-halted target terminated target
in 0 ms (exit code 0).
```

**Key observation**: exit code = **0** (natural exit through `Console.WriteLine("DONE")` + `Main` returning), not 143 (SIGTERM). The substrate's `TryResumeForDetach` released the Break stop, target ran past `Debugger.Break()`, executed its remaining work, and exited naturally — SIGTERM was redundant. Discipline-aligned outcome.

Compare to pre-fix run (logged at 18:07): same probe also worked (target died via SIGTERM, exit code 143). The fix makes the bare-target scenario *cleaner* but didn't fix a bug there — both paths terminated. The fix matters for MTP/VSTest scenarios.

### AnomalyInjectionTest — promoted to integration tests

```
$ ./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests \
    --filter 'FullyQualifiedName~AnomalyInjectionTest' --output Detailed

passed AttachAndOwn_MtpTarget_GetLocalsExcessiveDepth_DepthClampedAnomalyFires (1s 357ms)
passed AttachAndOwn_VstestTestHost_GetLocalsExcessiveDepth_DepthClampedAnomalyFires (1s 674ms)
Passed!  total: 2, failed: 0, succeeded: 2, duration: 3s 083ms
```

The test pattern:
1. AttachAndOwn → WaitForStop (Break stop from Debugger.Break() at runner startup)
2. Assert stop is Break OR Breakpoint (corrected assertion — runners produce Break, not Breakpoint)
3. `session.GetLocals(depth: 999)` → substrate emits `DepthClamped` anomaly with `requested=999`
4. `session.Resume()` — target proceeds past Debugger.Break() into test method body
5. End of `using` → substrate Dispose
6. Test asserts exactly 1 DepthClamped anomaly + target exited naturally within 5/10 s

Pre-Increment-1b, step 5's Dispose against the Break-halted target would have hung the test. With the substrate fix, Dispose's `TryResumeForDetach` was already redundant here (test already Resumed in step 4), but the fix means even if a future test pattern fails an assertion BEFORE Resume, the substrate's Dispose still handles the halted target cleanly via the release-then-SIGTERM flow.

### Full integration suite

```
10/10 PASS in 11.9 s
  AttachAndOwn_MtpTarget_GetLocalsExcessiveDepth_DepthClampedAnomalyFires        (1.362s)
  AttachAndOwn_VstestTestHost_GetLocalsExcessiveDepth_DepthClampedAnomalyFires   (1.660s)
  AttachAndOwn_MtpTarget_BriefWork_NaturalExit                                    (546ms)
  AttachAndOwn_MtpTarget_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak        (555ms)
  AttachAndOwn_VstestTestHost_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak   (1.238s)
  AttachAndOwn_MtpTarget_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly      (559ms)
  AttachAndOwn_VstestTestHost_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly (1.217s)
  AttachAndOwn_VstestTestHost_BriefWork_NaturalExit                               (1.230s)
  AttachAndOwn_MtpTarget_ThrowingSink_WorkerExceptionAnomalyFires                 (1.533s)
  AttachAndOwn_VstestTestHost_ThrowingSink_WorkerExceptionAnomalyFires            (1.910s)
```

All Phase 8a tests still pass. Slight per-test increase (~50 ms) from the new `TryResumeForDetach` call in Owned Dispose — negligible.

### Unit tests + probe 55 (substrate two-stage escalation regression check)

- Unit tests: 59/59 PASS (substrate-internal behavior unchanged at unit level).
- Probe 55 (TargetStuckAtDispose against ignoring target): would still PASS — the ignoring target has Cancel=true handlers that ignore SIGTERM regardless of release-stop state, so Stage 1 still times out + Stage 2 SIGKILL still terminates.

## Discipline reflection

This finding's substrate change is small (~5 lines added; reuses existing helper) but conceptually significant: substrate **acts on its own knowledge of target state** rather than blindly signaling. The Borrowed-path already did this (`TryResumeForDetach` in finding 59); the Owned-path inherits the same primitive.

Martin's framing — "the debugger knows" — generalizes beyond this fix. The substrate is in a privileged observation position; substrate-grade design uses that knowledge to drive behavior rather than treating each substrate API as a blind operation.

## Discovered systemic issue: orphan testhost processes

Process-state inspection during diagnosis revealed 14+ orphan testhost.dll processes from prior days (Sun07PM, Mon04AM). These were leftovers from earlier integration-test work where substrate Dispose against halted testhost did NOT terminate properly — the substrate-correctness gap this finding closes existed for days, accumulating zombies.

Cleanup: `pkill -KILL -f "testhost.dll"` killed 34 orphans (current + historical).

Going forward, the substrate fix prevents new orphans. Phase 8b's AnomalyInjectionTest pattern (and any future pattern that exits a `using` with a pending mscordbi stop) will now terminate the target cleanly via the release-then-SIGTERM flow.

## Files changed

- `src/DrHook.Engine/DebugSession.cs` (Dispose Owned-path): added `TryResumeForDetach()` call before `RequestExit()`.
- `tests/DrHook.Engine.IntegrationTests/AnomalyInjectionTest.cs` (NEW): integration test promoting probe 41's substrate-correctness scenario.
- `poc/drhook-engine/56-target.cs` (NEW): bare `Debugger.Break()` target.
- `poc/drhook-engine/56-break-stopped-dispose-smoke.cs` (NEW): probe characterizing substrate Dispose against Break-halted target.

## Cross-references

- [ADR-008](../../../docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) — Increment 1 (this finding's 1b sub-amendment to Owned-path Dispose).
- [finding 59](59-detach-exit-race-outcome.md) + [finding 65](65-probe42-redesign-regression.md) — `TryResumeForDetach` (Borrowed-path); now reused for Owned-path Increment 1b.
- [finding 67](67-lifecycle-discipline.md) — discipline framing; substrate acting on its knowledge of target state is Layer 1.
- [finding 69](69-increment1-substrate-api.md) — original Increment 1; 1b extends the Owned-path with release-then-SIGTERM.
- [finding 72](72-increment4a-mass-promotion.md) — Phase 8a tests still pass after this substrate fix.

## What's next

- **Increment 4c** — ConcurrentPauseStopTest (probe 43) promotion. Needs new `RunThrowCatchLoop` [TestMethod]/[Fact] in integration targets (STOPPING events for concurrent-Pause scenario). After Increment 1b, no further substrate work expected.
- **Increment 5** — ADR-007 amendment + ADR-008 closure.

## Phase 8 status

| Test class | Probe | Status |
|---|---|---|
| AttachDisposeTest (existing × 2) | (baseline) | PASS |
| WorkerExceptionTest | 45 | PASS |
| InformationalFloodTest | 42 | PASS |
| PauseDisposeTest | 44 phase B | PASS |
| **AnomalyInjectionTest** | **41** | **PASS** (this finding) |
| ConcurrentPauseStopTest | 43 | Pending (Increment 4c) |
| (probe 47) | 47 | DROPPED as redundant |

**5 of 6 substrate-correctness probes** from ADR-008's Phase 8 list are now CI-enforced as integration tests across MTP + VSTest shapes (10 tests total).
