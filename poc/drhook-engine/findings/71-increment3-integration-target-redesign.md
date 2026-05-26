# Finding 71: ADR-008 Increment 3 — integration target redesign + existing integration tests adjusted for natural-exit pattern

**Status:**   ADR-008 Increment 3 deliverable. MTP integration target's `IdleForDebuggerObservation` and VSTest integration target's `IdleFact.IdleForDebuggerObservation` redesigned from `Thread.Sleep(30s)` to finite Thread.Start/Join observable work (~500 ms). Test methods renamed: `*_BriefIdle_DisposeCleanly` → `*_BriefWork_NaturalExit`. Both integration tests now use `Process.WaitForExit(timeout)` assertions to validate Layer 1 discipline at the integration-test layer. 2/2 PASS in 1.9 s total — within ADR-008's "completing in <2 s total" target.
**Date:**     2026-05-26

## What changed

### MTP integration target (`tests/DrHook.Engine.IntegrationTargets.Mtp/IdleTarget.cs`)

Before:
```csharp
[TestMethod]
public void IdleForDebuggerObservation()
{
    Thread.Sleep(TimeSpan.FromSeconds(30));
}
```

After:
```csharp
[TestMethod]
public void RunBriefObservableWork()
{
    // 10 Thread.Start/Join × ~50 ms = ~500 ms total.
    // Generates CreateThread + ExitThread mscordbi callbacks per iteration.
    for (int i = 0; i < 10; i++)
    {
        Thread t = new(static () => Thread.Sleep(20)) { IsBackground = true };
        t.Start();
        t.Join();
        Thread.Sleep(30);
    }
}
```

Method body completes in ~500 ms; MTP test orchestration then reports + testhost exits naturally.

### VSTest integration target (`tests/DrHook.Engine.IntegrationTargets.Vstest/IdleFact.cs`)

Same redesign in xUnit `[Fact]` shape — method renamed from `IdleForDebuggerObservation` to `RunBriefObservableWork`, same brief-work body.

### Integration test `AttachDisposeTest.cs` (MTP path)

Renamed test method: `AttachAndOwn_MtpTarget_BriefIdle_DisposeCleanly` → `AttachAndOwn_MtpTarget_BriefWork_NaturalExit`.

Pattern change: after `session.Dispose()`, assert natural exit via `Process.WaitForExit(5000)`. Previously the test had no post-Dispose assertion — substrate killed the target on Dispose (finding 64), test relied on the implicit "if we got here, it worked." Now the test explicitly validates Layer 1 discipline: the bootstrap MUST have exited naturally within 5 seconds of Dispose. If it hasn't, that's either a substrate bug OR a target-implementation bug — actionable upstream signal.

```csharp
using (DebugSession session = DebugSession.AttachAndOwn(pid, new NullSink()))
{
    Thread.Sleep(TimeSpan.FromMilliseconds(200));
    // session.Dispose() at end of using:
    //   Stage 1: SIGTERM → target's [TestMethod] finishes its brief work, MTP reports + exits
    //   No Stage 2, no TargetStuckAtDispose expected.
}

// Layer 1 discipline assertion:
bool exitedNaturally = bootstrap.WaitForExit(5000);
Assert.IsTrue(exitedNaturally,
    "MTP target did not exit naturally within 5s after Dispose — Layer 1 discipline violation ...");
```

The `finally` block remains for defensive process-cleanup on exception paths, but is a no-op on the happy path (bootstrap exited naturally via the new pattern).

### Integration test `VstestAttachDisposeTest.cs` (VSTest path)

Same redesign for the VSTest variant. Method renamed; `WaitForExit(10000)` assertion (longer than MTP's 5 s because VSTest's `dotnet test → vstest.console → testhost` tree has more components to wind down naturally).

## Validation

```
$ ./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests --output Detailed

MSTest v4.2.3 (UTC 5/14/2026) [osx-arm64 - .NET 10.0.0]
passed AttachAndOwn_MtpTarget_BriefWork_NaturalExit (725ms)
passed AttachAndOwn_VstestTestHost_BriefWork_NaturalExit (1s 158ms)

Test run summary: Passed!
  total: 2, failed: 0, succeeded: 2, duration: 1s 935ms
```

Per-test budget:
- MTP variant: 725 ms total. Composed of: ~30 ms target spawn + ~50 ms MTP `--debug` handshake + 200 ms substrate observation window + substrate Dispose (Stage 1 SIGTERM + Detach + Terminate ≈ ~150 ms when target exits immediately on SIGTERM) + WaitForExit verification (target already gone by then).
- VSTest variant: 1158 ms total. Composed of: ~500 ms `dotnet test → vstest.console → testhost` spawn + VSTEST_HOST_DEBUG handshake + 500 ms substrate observation + substrate Dispose + WaitForExit verification.

Total 1.9 s. Within ADR-008's "<2 s total" target.

Unit tests: 59/59 still PASS.

## Substrate path observability

With Increment 1's SIGTERM-then-SIGKILL escalation + Increment 2's bounded probe targets + Increment 3's bounded integration targets, the discipline-aligned path now works end to end:

```text
Caller spawns bootstrap (transient — substrate doesn't own).
  ↓
Substrate.AttachAndOwn(pid) — substrate Owns the target's debug-session lifecycle.
  ↓
Target runs its brief observable work (Increment 2/3 redesign).
  ↓
Test body does substrate-correctness assertions during observation window (~200-500 ms).
  ↓
session.Dispose() — substrate Stage 1 SIGTERM. Target's natural-exit path is honored.
  CoreCLR default SIGTERM disposition exits target cleanly (~tens of ms).
  Substrate observes natural exit via finding 66 death-detection.
  No TargetStuckAtDispose anomaly. No Stage 2 SIGKILL fallback.
  ↓
Test asserts bootstrap.WaitForExit(timeout) — Layer 1 discipline validated.
  ↓
Test passes if exited naturally; FAILS with actionable signal if not.
```

The substrate change (Increment 1) and target redesigns (Increment 2/3) compose into a coherent discipline.

## What's NOT covered

- **Phase 8 mass promotion** (Increment 4): 14 substrate-correctness integration tests for probes 41–47 × {MTP, VSTest}, all using this natural-exit pattern. That's Increment 4.
- **Cross-platform** (Phase 9): Linux + Windows. Windows needs `GenerateConsoleCtrlEvent` in substrate's `RequestExit` (currently `PlatformNotSupportedException`). Phase 9 handles this.

## Discipline observation

The pre-Increment-3 pattern had targets `Thread.Sleep(30s)` and tests with no post-Dispose assertion — relying on the substrate's old kill-first protocol to make tests pass quickly regardless of target shape. The new pattern asserts the natural-exit lifecycle explicitly. Targets demonstrate the discipline they're testing. Tests validate the discipline they're enforcing.

A target that gets stuck (eternal loop bug introduced by a refactor, etc.) would now fail the `WaitForExit` assertion with a clear message: *"Layer 1 discipline violation (substrate may have left target stuck, OR target is mis-implemented)."* That's actionable signal for the test author/maintainer.

## Cross-references

- [ADR-008](../../../docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) Increment 3 — this finding's deliverable.
- [finding 67](67-lifecycle-discipline.md) — discipline articulation.
- [finding 69](69-increment1-substrate-api.md) — substrate API change that this finding's test pattern depends on.
- [finding 70](70-increment2-target-redesign.md) — probe target redesign; same pattern applied to integration targets here.

## What's next

- **Increment 4** — Phase 8 mass promotion: 14 substrate-correctness integration tests (probes 41–47 × {MTP, VSTest}), all using the natural-exit + WaitForExit-assertion pattern this finding established.
- **Increment 5** — ADR-007 amendment + ADR-008 closure.
