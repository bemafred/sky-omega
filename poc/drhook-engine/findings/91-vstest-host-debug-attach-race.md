# Finding 91 — VSTEST_HOST_DEBUG's Break is racy against attach: an early attach skips the hold entirely

**Date:** 2026-10-03
**Status:** Root cause **PROVEN** (VSTest source + event trace); **fixed** in the one test that depended on the Break.

## Symptom

`AttachAndOwn_VstestTestHost_GetLocalsExcessiveDepth_DepthClampedAnomalyFires` failed **deterministically when run
alone** (12/12, `Expected Break or Breakpoint stop; got ProcessExited`) yet passed in every full-suite run. It failed
identically on the session-start commit `2ab2212`, so it predates the 2026-10-03 engine changes.

## Root cause (proven)

1. The `Process Id: NNNN` line the harness parses is printed by **vstest.console** (`ProxyOperationManager`: "Host
   debugging is enabled…") as soon as it **launches** the testhost — before the testhost has run any code. The
   testhost prints its own, different line ("Waiting for debugger attach…") only once it reaches the hold.
2. The hold (`microsoft/vstest` `DebuggerBreakpoint.WaitForDebugger`):
   ```csharp
   if (Debugger.IsAttached) return;            // ← an attach that already landed skips the hold
   …
   while (!Debugger.IsAttached) Task.Delay(1000).GetAwaiter().GetResult();
   Break();
   ```
3. DrHook's attach completed ~15–20 ms after the pid line (event trace: `attach-returned` at ~280–300 ms after
   spawn, then `CreateProcess`, `CreateAppDomain`, thread `NameChange`s, exit ~300 ms later — **no `Break`**). The
   testhost reached `WaitForDebugger` with a debugger already attached, returned immediately, ran the `[Fact]` to
   completion and exited → first stop `ProcessExited`.
4. Inside the full suite the attach path is warm/slower relative to the testhost and usually loses the race, so the
   testhost is already in its delay loop and the Break arrives — the failure was masked.

`ConcurrentPauseStopTest` had already met this race ("the original WaitForStop pattern (expecting Break first) failed
… in VSTest variant") and been refactored around it — but the race was never characterized or generalized, and the
`GetLocalsExcessiveDepth` VSTest variant kept the Break-first pattern.

## Fix

The test needs only *a* stop with a valid stop thread (the anomaly fires on the `GetLocals(depth: 999)` request), so
it forces one: `session.Pause()` → accept `Pause`, or the held testhost's `Break` if that queued ahead. Deterministic
for both an early (hold skipped) and a late (testhost holding) attach. The other five VSTest tests already drive their
own Pause or do not wait on a first stop.

## Validation

- The fixed test alone: **10/10** (was 0/12). Every integration test run alone: **16/16 pass**.
- Full suite ×5: 16/16 each, 0 orphaned testhosts.

## Rule for VSTest-hosted attach

Never depend on `VSTEST_HOST_DEBUG`'s Break as the first stop. The printed pid precedes the hold; force the stop
(Pause) or arm a pending breakpoint while stopped.

## References

- `tests/DrHook.Engine.IntegrationTests/AnomalyInjectionTest.cs` (VSTest variant), `ConcurrentPauseStopTest.cs`.
- microsoft/vstest `src/Microsoft.TestPlatform.Execution.Shared/DebuggerBreakpoint.cs`, `src/testhost.x86/Program.cs`.
- Finding 90 (the same run's second, debugger-waiting testhost).
