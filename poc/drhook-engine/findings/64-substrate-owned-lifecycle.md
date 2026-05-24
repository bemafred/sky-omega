# Finding 64: Substrate-owned target lifecycle — `AttachAndOwn` + internal kill-first eliminates MCH-RE-2 structurally

**Status:**   Refactor applied + validated on macOS-arm64 2026-05-24. `DebugSession.AttachAndOwn(pid, sink)` factory added; substrate now holds the `Process` handle internally for OWNED sessions and enforces the kill-first protocol inside `Dispose`. **MCH-RE-2 (finding 62/63) is structurally impossible** through this API surface — callers don't have a place to write a dispose-then-kill ordering because they don't manage the Kill anymore. Both integration tests (`AttachAndOwn_MtpTarget...` + `AttachAndOwn_VstestTestHost...`) PASS in the same MSTest exe (1.5 s total). 59/59 unit tests unchanged.
**Date:**     2026-05-24

## The reframe (Martin's question that drove this)

Finding 63 documented the workaround (kill-first protocol — caller-side discipline). Martin pushed back: *"we seem to lack something that should enforce proper idempotent life cycle management. We should not need to know and code for this in each test."* Right framing. The substrate had:

- **Split knowledge.** Caller owns the OS `Process`; substrate owns mscordbi state. Neither sees both — kill ordering is implicit, not enforced.
- **Two intent encodings.** `_ownsTarget` field existed (Probe 44 / finding 59) but the kill-first protocol for Owned sessions lived in `EngineSteppingSession.CleanupSession`, NOT in `DebugSession.Dispose`. Single intent, two implementations.

The substrate-discipline answer: substrate owns the Process for OWNED sessions; substrate's `Dispose` enforces ordering; caller has no surface to misuse.

## The design (implemented)

Three factories, three intents, single source of lifecycle ordering:

```csharp
public sealed class DebugSession : IDisposable
{
    // BORROWED — observation only; Dispose detaches, target keeps running.
    public static DebugSession Attach(int pid, IDebugEventSink sink);

    // OWNED via PID — substrate acquires Process.GetProcessById(pid), kill-firsts on Dispose.
    public static DebugSession AttachAndOwn(int pid, IDebugEventSink sink);

    // OWNED via spawn — substrate launches via dbgshim, acquires Process for the new PID, kill-firsts on Dispose.
    public static DebugSession Launch(string program, IReadOnlyList<string> args, string? cwd, IDebugEventSink sink);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pump.Dispose();

        // FINDING 64 — substrate-enforced kill-first for OWNED:
        if (_ownsTarget) TryKillTargetAndSettle();

        Quiesce();
        if (!_ownsTarget) TryResumeForDetach();  // Borrowed path (finding 59)
        Detach();
        _cordbg.Terminate();
        // ... refs / callback / dbgshim teardown ...
        _targetProcess?.Dispose();
    }

    private void TryKillTargetAndSettle()
    {
        try
        {
            if (_targetProcess is not null && !_targetProcess.HasExited)
                _targetProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _sink.OnAnomaly(new EngineAnomaly(/* UnexpectedCleanupException */ ...));
        }
        Thread.Sleep(KillSettleMs);  // mscordbi RC thread settle window (100ms)
    }
}
```

Key properties:

- **Single source of lifecycle ordering** — kill-first lives in `DebugSession.Dispose`. `EngineSteppingSession.CleanupSession` no longer manages `_launchedProcess`; that responsibility migrated INTO the engine.
- **Caller can't misorder** — `AttachAndOwn` consumers don't write `Process.Kill` anywhere; the substrate's `Dispose` is the only kill site. The `finally { proc.Kill(...) }` pattern that drove MCH-RE-2 is structurally not constructible.
- **Symmetric across Owned modes** — both `AttachAndOwn(pid)` and `Launch(...)` go through the same `TryKillTargetAndSettle` in Dispose. Substrate has one implementation of kill-first.
- **Borrowed path unchanged** — `Attach(pid)` still does detach-leave-running (Probe 44 / finding 59); no Process acquisition; substrate never calls Kill. Production-observation use case unaffected.

## Integration test refactor — the caller surface shrinks

Before (the dispose-then-kill structure that drove MCH-RE-2):

```csharp
using Process proc = Process.Start(targetExe, "--debug");
// ... read stdout for PID ...
try
{
    DebugSession session = DebugSession.Attach(pid, new NullSink());
    Thread.Sleep(200);
    session.Dispose();    // ← substrate detached, target alive
    Assert.IsFalse(proc.HasExited);
}
finally
{
    proc.Kill(entireProcessTree: true);   // ← dispose-then-kill, RACES mscordbi
}
```

After (substrate-enforced lifecycle):

```csharp
int pid = SpawnAndExtractPid(targetExe);  // bootstrap Process disposed inside helper

using DebugSession session = DebugSession.AttachAndOwn(pid, new NullSink());
Thread.Sleep(200);
// session.Dispose() at end of using → substrate handles kill-first internally
```

No `finally` for Kill. The `using` block IS the lifecycle. Exception-safe by C# language semantics, ordering-safe by substrate enforcement.

## Validation

### Integration tests — MCH-RE-2 structurally eliminated

```
$ ./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests

MSTest v4.2.3 (UTC 5/14/2026) [osx-arm64 - .NET 10.0.0]

Test run summary: Passed!
  total: 2
  failed: 0
  succeeded: 2
  duration: 1s 524ms
```

Both `AttachAndOwn_MtpTarget...` and `AttachAndOwn_VstestTestHost...` run cleanly in the SAME MSTest exe invocation. The MCH-RE-2 SIGBUS (finding 62) does not reproduce.

### Pre-refactor state confirms the value

At commit `3898fd8` (HEAD before this refactor), running the integration tests together still SIGBUSes (exit code 139). My refactor structurally closes that race — confirmed by stash-and-test.

### Unit tests unchanged

59/59 DrHook.Engine.Tests still pass. No regression in substrate behavior at the unit-test level.

## EngineSteppingSession simplification

`EngineSteppingSession.CleanupSession` previously did the kill-first dance for Launched sessions (lines 640-659 before refactor). After this finding's refactor:

```csharp
// Before — ~30 lines of kill-first orchestration in CleanupSession
if (_launchedProcess is not null)
{
    try { if (!_launchedProcess.HasExited) _launchedProcess.Kill(entireProcessTree: true); }
    catch (Exception ex) { _anomalies.OnAnomaly(...); }
    _launchedProcess.Dispose();
    _launchedProcess = null;
}
try { _session.Dispose(); } catch { ... }

// After — substrate handles kill-first; CleanupSession is just Dispose + bookkeeping
try { _session.Dispose(); } catch (Exception ex) { _anomalies.OnAnomaly(...); }
```

The `_launchedProcess` field is deleted. Single locus of lifecycle ordering — in the substrate where it belongs.

## Adjacent discovery: probe 42 has a separate regression at HEAD

While re-validating probes 41–45 against the refactor, **probe 42 SIGSEGVs at HEAD state (BEFORE my refactor too)**. Confirmed via `git stash` of refactor changes — probe 42 still crashes after the sentinel session, before reaching the cycle loop.

This is **NOT caused by this finding's refactor**. It is a pre-existing regression that surfaced somewhere between finding 57's commit (`824e642`, where Probe 42 passed 20/20) and HEAD (`3898fd8`). The intervening commits include the EngineAnomaly substrate (EA-1..6), engineering fixes (ENG-CP-1/DS-1/DBG-D/STK-*), Probe 44 substrate change (`_ownsTarget` + `TryResumeForDetach`), Probe 45 substrate change, and DBG-PI-1 + WE-OA-1 hygiene fixes.

Probes 41 + 44 + 45 pass cleanly with current substrate. Only probe 42's specific pattern (long-lived flood target + sentinel + 20 sequential Borrowed sessions) is failing now. Hypothesis: the cumulative substrate changes affect mscordbi's per-session state accumulation in a way the 20-cycle pattern exposes. The integration tests use just 2 sessions and pass; probe 42's pattern is more extreme.

**This is queued as a separate substrate investigation — does not block Phase 2 closure.** The refactor + integration tests deliver the substrate-correctness Phase 2 needs. Probe 42's regression is for a future probe (call it MCH-RE-4) to characterize.

## What changes structurally

- **MCH-RE-2 (finding 62) is no longer a Phase 8 blocker.** Mass promotion of substrate-correctness work into integration tests can proceed using the `AttachAndOwn` pattern — substrate enforces lifecycle correctly.
- **`EngineSteppingSession` simpler** — lost ~20 lines of kill-first orchestration, gained nothing it needed.
- **Caller-side test code shrinks** — integration tests lost ~15 lines of `finally`-block kill logic; replaced with a simple bootstrap-then-AttachAndOwn pattern.

## Engine API surface

Three factory methods on `DebugSession`:

| Factory | Intent | Process management | Dispose behavior |
|---|---|---|---|
| `Attach(pid, sink)` | Borrowed observation | Substrate doesn't acquire | Detach-leave-running |
| `AttachAndOwn(pid, sink)` | Owned attach (test/orchestration-spawned target) | Substrate acquires via `GetProcessById` | Kill-first + settle, then teardown |
| `Launch(prog, args, cwd, sink)` | Owned launch (dbgshim-spawned) | Substrate acquires via `GetProcessById(launched_pid)` | Kill-first + settle, then teardown |

`OwnsTarget` is exposed as a public property for diagnostic/introspection; consumers don't need it for correctness.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 2 Probe 46/46b.
- [finding 59](59-detach-exit-race-outcome.md) — Probe 44 substrate change (`_ownsTarget` + `TryResumeForDetach`); MCH-RE-1.
- [finding 62](62-legacy-vstest-promotion.md) — MCH-RE-2 discovery (now structurally eliminated).
- [finding 63](63-mch-re-2-investigation.md) — root-cause analysis (kill-first workaround) that drove this refactor.
- [`docs/limits/drhook-detach-exit-race.md`](../../../docs/limits/drhook-detach-exit-race.md) — Triggered limit; the dispose-then-kill race remains the underlying phenomenon, but `AttachAndOwn` makes it unreachable from caller code.
- [`feedback_no_behavior_flags`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md) — discipline rule that drove the three-factory shape (not one factory with a flag).

## Phase 8 status — UNBLOCKED

Phase 8 mass promotion (probes 41–45 into integration tests) can now proceed:

- Substrate enforces lifecycle correctly via `AttachAndOwn`.
- Multiple substrate-attaching tests in the same MSTest exe coexist cleanly.
- The caller surface is minimal and idiom-correct (`using` block IS the lifecycle).

Probe 42's separate regression remains queued for a future substrate investigation, but doesn't block the promotion mechanism — substrate-correctness assertions in integration tests use the same `AttachAndOwn` pattern as Phase 2's exemplars, and those pass.
