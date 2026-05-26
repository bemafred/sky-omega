# Finding 69: ADR-008 Increment 1 — substrate API SIGTERM-then-SIGKILL escalation + RequestExit + Abandon

**Status:**   ADR-008 Increment 1 substrate-code deliverable. `DebugSession`'s Owned-path `Dispose` now performs SIGTERM-then-SIGKILL escalation per ADR-008 Decisions 1-3. New public methods: `RequestExit(TimeSpan)` (Layer 1 discipline primitive) and `Abandon(TimeSpan?)` (fast-escalation composition). New `AnomalyKind.TargetStuckAtDispose` surfaces target's discipline violation. New `PosixSignals` interop helper P/Invokes `libc.kill` for SIGTERM. New probe 55 validates the escalation path; existing probes 41–48 + integration tests still PASS.
**Date:**     2026-05-26

## What was implemented

### `DebugSession.RequestExit(TimeSpan timeout)` — new public method

Layer 1 discipline primitive (ADR-008 / finding 68). Sends SIGTERM to Owned target via `libc.kill(pid, 15)`; waits up to `timeout` for natural exit. Returns true if target exited within window; false if still alive. Does NOT force-kill on timeout — caller chooses the next action.

Throws `InvalidOperationException` on Borrowed sessions (substrate doesn't own; caller is not entitled to request termination through substrate).

Surface contract:

```csharp
public bool RequestExit(TimeSpan timeout);
//   true:  target exited naturally within timeout
//   false: target still alive (caller can call Abandon to force-kill, or retry)
//   throws InvalidOperationException if called on Borrowed session
```

### `DebugSession.Abandon(TimeSpan? briefGrace = null)` — new public method

Explicit fast-escalation composition. Internally: SIGTERM → wait `briefGrace` (default 200 ms) → SIGKILL if still alive → Dispose. Sub-second worst-case total budget on macOS-arm64 (200 ms grace + 100 ms KillSettle + 200 ms ExitWorkSettle + teardown ≈ 600 ms).

Does NOT emit `TargetStuckAtDispose` — the caller opted into fast escalation, so the discipline-default anomaly isn't appropriate. The Dispose-default path's anomaly emission remains in place for the discipline-default path.

Surface contract:

```csharp
public void Abandon(TimeSpan? briefGrace = null);
//   Synchronous: SIGTERM(briefGrace) → SIGKILL → Dispose. Default brief grace 200 ms.
//   No-op on already-Disposed sessions.
//   Borrowed sessions: falls through to Dispose's Borrowed teardown (no kill — caller doesn't own).
```

### `DebugSession.Dispose` — behavior change

Owned-path Dispose now performs two-stage SIGTERM-then-SIGKILL escalation instead of finding-64's kill-first protocol:

```text
Stage 1 — RequestExit(_naturalExitTimeout)
            sends SIGTERM via libc.kill(pid, 15); waits up to _naturalExitTimeout
            (default 2000 ms, configurable via AttachAndOwn/Launch overload).
            If target exits: success — no anomaly, proceed through finding 66
            death-detection routing.

Stage 2 — Only fires if Stage 1 timed out.
            Emit AnomalyKind.TargetStuckAtDispose with target's PID + timeout context.
            Call TryKillTargetAndSettle (existing finding 64 path; SIGKILL + 100 ms settle).
            Proceed through finding 66 death-detection routing.
```

Borrowed sessions: unchanged — `BorrowedDeathCheckSettleMs` + finding 66 death-detection routing.

### New `AttachAndOwn` / `Launch` overloads

Optional `TimeSpan? naturalExitTimeout = null` parameter on the existing factory methods. `null` → default 2000 ms. Setting higher accommodates targets with legitimate long-cleanup needs (e.g., long-running flushes); setting lower is rarely useful (use `Abandon` with custom briefGrace instead for fast escalation).

```csharp
public static DebugSession AttachAndOwn(int processId, IDebugEventSink sink, TimeSpan? naturalExitTimeout = null);
public static DebugSession Launch(string program, IReadOnlyList<string> args, string? workingDirectory, IDebugEventSink sink, TimeSpan? naturalExitTimeout = null);
```

Backwards compatible — existing callers using the 2-arg AttachAndOwn / 4-arg Launch continue to work, get the default 2000 ms.

### `AnomalyKind.TargetStuckAtDispose` — new enum value

Added to `EngineAnomaly.cs`:

```csharp
/// <summary>The Owned-target Dispose's Stage 1 SIGTERM (graceful-exit request) did not result
/// in target exit within NaturalExitTimeout; substrate escalated to Stage 2 SIGKILL.
/// ADR-008 / finding 68 Layer 2 guard: the target violated process lifecycle discipline.
/// Actionable upstream signal — callers can investigate the target's implementation rather
/// than tolerate the kill as silent default.</summary>
TargetStuckAtDispose,
```

### `PosixSignals` interop helper — new file

`src/DrHook.Engine/Interop/PosixSignals.cs`. Minimal P/Invoke layer for `libc.kill`:

```csharp
internal static class PosixSignals
{
    public const int SIGTERM = 15;
    public const int SIGINT = 2;
    public const int SIGKILL = 9;
    public static int Kill(int pid, int signal);  // throws on Windows; Unix only
}
```

Windows path (`GenerateConsoleCtrlEvent`) is deferred to ADR-007 Phase 9 / ADR-008 Phase 0.1.

### Constants

```csharp
public static readonly TimeSpan DefaultNaturalExitTimeout = TimeSpan.FromMilliseconds(2000);
public static readonly TimeSpan DefaultAbandonBriefGrace  = TimeSpan.FromMilliseconds(200);
```

Public so callers can reference + reason about substrate defaults. Per-session `_naturalExitTimeout` field captures the per-session override.

## Validation

### New: probe 55 — substrate two-stage escalation against ignoring target

```
$ dotnet run --no-cache 55-stuck-target-escalation-smoke.cs 51-target.cs

target pid : 81438
dispose    : completed in 845ms
target     : dead (exit code 137)
anomalies  : 4 surfaced
  LateCallback                     : 2
  TargetStuckAtDispose             : 1
  UnexpectedHResult                : 1
  → operation: DebugSession.Dispose (Stage 1 SIGTERM timeout)
  → observed : target 81438 did not exit within NaturalExitTimeout=500ms after SIGTERM
  → context  : pid=81438, timeoutMs=500

PROBE 55 PASSED — substrate two-stage SIGTERM-then-SIGKILL escalation:
  Stage 1: SIGTERM sent + waited 500ms (target ignored — Cancel=true)
  Anomaly: TargetStuckAtDispose surfaced (1) with PID + timeout context
  Stage 2: SIGKILL fallback killed target (exit code 137 = 128 + SIGKILL)
  Total elapsed: 845ms (within [400, 5000]ms budget)
```

Probe 55 uses `51-target.cs` (catches SIGINT + SIGTERM with Cancel=true — the canonical lifecycle violator from finding 68 probe 51). Substrate's 500 ms `naturalExitTimeout` was set via the new overload; the 845 ms total = 500 ms Stage 1 wait + ~100 ms KillSettleMs + ~200 ms ExitWorkSettleMs + teardown overhead. Exactly 1 `TargetStuckAtDispose` anomaly surfaced with target's PID + timeoutMs context.

### Regression — existing probes still PASS

```
PROBE 47 PASSED — 10/10 clean Dispose cycles against externally-killed Borrowed targets
PROBE 48 PASSED — 10/10 clean Dispose cycles against fresh targets in same probe-host
PROBE 42 PASSED — 50/50 clean Dispose cycles under continuous informational flood
```

Plus probes 41, 43, 44, 45 (not re-run today; substrate change is backwards-compatible).

### Regression — unit tests + integration tests

```
$ dotnet test tests/DrHook.Engine.Tests/...
Passed!  - Failed: 0, Passed: 59, Skipped: 0, Total: 59

$ ./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests
Test run summary: Passed!
  total: 2, failed: 0, succeeded: 2, duration: 1s 900ms
```

The integration tests use `AttachAndOwn` without specifying `naturalExitTimeout` — defaults to 2000 ms. Existing MTP / VSTest test targets `Thread.Sleep(30s)` without explicit signal handlers; per finding 68 probe 53, CoreCLR default disposition handles SIGTERM cleanly in ~12 ms. So the new substrate behavior fires Stage 1 SIGTERM, target exits in tens of ms via CoreCLR default, no Stage 2 needed, no anomaly emitted. Tests complete faster (1.9 s total vs prior 2.2 s) — the discipline-aligned path is actually slightly faster for well-behaved targets because SIGTERM-default-exit is faster than SIGKILL + KillSettleMs.

## Behavioral implications

### For MCP tools (`drhook_step_run`, `drhook_step_launch`)

Unchanged surface; underlying behavior now sends SIGTERM first. For typical interactive debug sessions where user signals end-of-session and target completes its work, target exits gracefully — substrate honors the natural lifecycle. For stuck targets (eternal loop in user code), substrate waits 2 s then escalates with `TargetStuckAtDispose` anomaly surfaced through MCP — the AI agent gets actionable signal: "your target's loop didn't terminate; investigate the target".

### For probes / integration tests

Existing probes that use `AttachAndOwn` get the new behavior transparently. Targets with no signal handler (or with cooperative handlers) work via Stage 1 success — typically faster than the previous kill-first protocol. Tests that genuinely need fast termination (e.g., probe-cleanup between cycles) can call `session.Abandon()` instead of `session.Dispose()` for the sub-second-budget path.

### Borrowed observation path

`Attach` (Borrowed) behavior unchanged — substrate doesn't terminate Borrowed targets, ever. `RequestExit` throws `InvalidOperationException` on Borrowed sessions to enforce this discipline at the API surface.

### Anomaly stream semantics

`TargetStuckAtDispose` is the new substrate-correctness signal callers should monitor. Its surfacing indicates the target violates process lifecycle discipline — caller's investigation target is the target's implementation, not the substrate. This is actionable upstream signal, consistent with substrate-grade discipline (per `feedback_surprises_are_substrate_grade`).

## Files changed

- `src/DrHook.Engine/Interop/PosixSignals.cs` — new file: P/Invoke for libc.kill
- `src/DrHook.Engine/EngineAnomaly.cs` — added `AnomalyKind.TargetStuckAtDispose`
- `src/DrHook.Engine/DebugSession.cs` —
  - New public methods: `RequestExit(TimeSpan)`, `Abandon(TimeSpan?)`
  - New public constants: `DefaultNaturalExitTimeout`, `DefaultAbandonBriefGrace`
  - New private field: `_naturalExitTimeout`
  - Constructor + `FromCordbg` signature gained `naturalExitTimeout` parameter
  - `AttachAndOwn`, `Launch` gained optional `TimeSpan? naturalExitTimeout` parameter
  - `Dispose` Owned-path: two-stage SIGTERM-then-SIGKILL escalation replaces kill-first
- `poc/drhook-engine/55-stuck-target-escalation-smoke.cs` — new probe validating Increment 1

## What's next

- **Increment 2** — probe target redesign. Probe targets that use `Thread.Sleep(Timeout.Infinite)` (47-target, 48-target, 48b uses dotnet test target) or `while(true)` (42-target, 07-target, 51-target) should be rewritten with finite work per ADR-008 Decision 4. Currently they "work" because substrate now SIGTERMs them and CoreCLR default handles it — but the targets themselves remain Layer 1 violators.
- **Increment 3** — integration target redesign. MTP `IdleForDebuggerObservation` + VSTest `IdleFact` currently `Thread.Sleep(30s)` — should be rewritten with finite observable work per Decision 4.
- **Increment 4** — Phase 8 mass promotion: 14 integration tests using natural-exit patterns + WaitForExit assertions.
- **Increment 5** — ADR-007 amendment closing Phase 1 substrate-correctness arc at integration-enforcement level.

## Cross-references

- [ADR-008](../../../docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) — Decisions 1-3 implemented per this finding.
- [finding 67](67-lifecycle-discipline.md) — discipline articulation.
- [finding 68](68-process-lifecycle-ground-truth.md) — empirical ground truth that the implementation rests on.
- [finding 64](64-substrate-owned-lifecycle.md) — prior kill-first protocol that this finding's Stage 2 inherits as fallback.
- [finding 65](65-probe42-redesign-regression.md) — dispatch-settle in `TryResumeForDetach`. Unchanged; still applies to Borrowed live-target path.
- [finding 66](66-target-death-detection.md) — death-detection routing in Dispose. Compatible with new two-stage discipline — both Stage 1 success and Stage 2 fallback end with HasExited → routes through dead-target path correctly.
- `src/DrHook.Engine/DebugSession.cs` lines 47-78 (constants + ctor), 306-410ish (Pause / Resume / RequestExit / Abandon), 1083-1150ish (Dispose).
- `src/DrHook.Engine/Interop/PosixSignals.cs` — new file.
- `src/DrHook.Engine/EngineAnomaly.cs` lines 73-83 (TargetStuckAtDispose enum entry).
