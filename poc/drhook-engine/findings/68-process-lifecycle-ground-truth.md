# Finding 68: Process lifecycle ground truth — empirical signal disposition on macOS-arm64

**Status:**   ADR-008 Phase 0 deliverable. Probes 49–54 establish empirical ground truth for .NET process termination behavior across signal types, target shapes, and process-tree topologies on macOS-arm64. Six probes; all completed cleanly; observations consolidated below. Revises ADR-008 Decisions 1–3 from intuition-based to evidence-based.
**Date:**     2026-05-26

## Summary table

| Probe | Scenario | Outcome | Exit code | Time-to-exit |
|---|---|---|---|---|
| 49 | Well-behaved + `Console.CancelKeyPress` + SIGINT | Graceful cleanup, clean exit | 0 | 33 ms |
| 50 | Well-behaved + `PosixSignalRegistration(SIGTERM)` + SIGTERM | Graceful cleanup, clean exit | 0 | 45 ms |
| 51 (Phase A) | Ignoring + SIGINT (handler sets Cancel=true) | Intercepted, target alive 1 s after | — (alive) | — |
| 51 (Phase B) | Ignoring + SIGTERM (handler sets Cancel=true) | Intercepted, target alive 1 s after | — (alive) | — |
| 51 (Phase C) | Same ignoring target + SIGKILL | Kernel-killed | 137 (= 128 + 9) | 33 ms |
| 52 | Tight CPU loop, NO handler + SIGTERM | CoreCLR default disposition: terminated | 143 (= 128 + 15) | 16 ms |
| 53 (Round 1) | No handler, default + SIGINT | CoreCLR default disposition: terminated | 130 (= 128 + 2) | 17 ms |
| 53 (Round 2) | No handler, default + SIGTERM | CoreCLR default disposition: terminated | 143 (= 128 + 15) | 12 ms |
| 54 (Round A) | Parent + 2 children; SIGTERM to parent only | **Parent exits; children DO NOT cascade** | parent 143 | parent ~17 ms |
| 54 (Round B) | Parent + 2 children; `Process.Kill(entireProcessTree:true)` | **Parent + all children killed** | parent 137 | sub-second tree-kill |

## Empirical findings

### Finding A — Soft signals (SIGINT, SIGTERM) are catchable

Verified by probes 49, 50, 51. .NET surfaces:
- `Console.CancelKeyPress` event for SIGINT (probe 49 confirms catch + handler runs)
- `PosixSignalRegistration.Create(PosixSignal.SIGTERM, …)` for SIGTERM (probe 50 confirms catch + handler runs)

Handlers can mark `args.Cancel = true` / `ctx.Cancel = true` to **suppress default termination**. Probe 51 demonstrates this: ignoring target keeps running for >1 second after both SIGINT and SIGTERM with Cancel=true handlers in place.

### Finding B — SIGKILL is non-catchable

Verified by probe 51 Phase C. The same target that ignored SIGINT + SIGTERM for >1 second was killed by SIGKILL in 33 ms with exit code 137 (= 128 + 9). This is the kernel-enforced escape hatch and cannot be intercepted by user code — by POSIX design.

`Process.Kill()` in .NET on Unix sends SIGKILL by default. This is the substrate's current `TryKillTargetAndSettle` mechanism. It is the "extraordinary action" path in ADR-008's terminology — non-graceful, but guaranteed.

### Finding C — CoreCLR's default signal disposition is well-behaved

Verified by probes 52 + 53. A target with NO explicit signal handlers responds correctly to both SIGINT and SIGTERM:
- SIGINT default → exit code 130 (= 128 + 2), ~17 ms
- SIGTERM default → exit code 143 (= 128 + 15), ~12 ms
- Even a tight CPU-bound loop with no async yield responds to SIGTERM in ~16 ms (probe 52)

**Substrate implication**: the substrate can send SIGTERM and rely on well-behaved targets to exit cleanly within tens of milliseconds *regardless of whether they have an explicit handler installed*. The CoreCLR runtime's signal delivery is robust enough that the substrate doesn't need to assume handler-installation.

### Finding D — Process-tree signal propagation requires explicit traversal

Verified by probe 54.
- Round A: SIGTERM to parent-PID-only **does NOT cascade** to children. Parent exits (code 143), children continue running (eventually adopted by `launchd` on macOS as orphans).
- Round B: `Process.Kill(entireProcessTree:true)` traverses the tree and kills all descendants. Parent exits (code 137 = SIGKILL).

**Substrate implication**: substrate's existing `TryKillTargetAndSettle` uses `Process.Kill(entireProcessTree: true)` — that's the correct API for tree-kill semantics on Unix. Signal-to-leader-only via `kill(pid, SIGTERM)` would leave orphans.

### Finding E — Standard Unix exit codes apply

Verified across probes 51/52/53.
- Signal-terminated exit code = 128 + signal_number
- SIGINT (2) → 130
- SIGTERM (15) → 143
- SIGKILL (9) → 137
- Clean exit via `Environment.Exit(0)` → 0

Substrate can use exit code to diagnose target's last-moment state.

## What this revises in ADR-008

### Decision 1 (substrate API): SIGTERM-then-SIGKILL escalation, not direct SIGKILL

ADR-008's current Decision 1 proposed `AttachAndOwn` / `Launch` `Dispose` would wait-for-natural-exit and then fall back to `Process.Kill` (SIGKILL). Finding 68 reveals a richer two-stage discipline:

**Revised Decision 1**:
```
On AttachAndOwn / Launch Dispose:
  Stage 1 — Send SIGTERM (graceful request, catchable by target).
            Wait NaturalExitTimeoutMs for target to exit.
  Stage 2 — If still alive: emit TargetStuckAtDispose anomaly.
            Send SIGKILL (kernel-mandated escape hatch).
            Wait KillSettleMs for kernel to reap.
  Substrate teardown proceeds (per finding 66 death-detection routing).
```

This is the docker stop / systemd-equivalent discipline. SIGTERM gives well-behaved targets the chance to clean up; SIGKILL guarantees termination when needed. The two stages are clearly named in the substrate API (we can expose explicit `RequestExit(timeout)` + `Terminate()` primitives if useful, with `Abandon` being their composition).

### Decision 2 (NaturalExitTimeoutMs): 2000 ms default seems sufficient

ADR-008 proposed 5000 ms. Empirically (probes 49, 50, 52, 53), well-behaved targets — explicit handler or default disposition — exit within 12–45 ms of SIGTERM. 2000 ms is generous (~50× the observed worst case for a well-behaved target).

**Revised Decision 2**:
- `NaturalExitTimeoutMs = 2000` default. Still configurable per-session.
- Targets needing longer cleanup (e.g., long-running flushes) get explicit longer timeout via the per-session overload.

### Decision 3 (Abandon semantics): two-stage internally, single synchronous call externally

`Abandon` exposes the SIGTERM-then-SIGKILL discipline as a single explicit call for callers who want the *fast* version (no waiting for natural exit, escalate quickly). Internally:

```csharp
public void Abandon(TimeSpan? quickGracePeriod = null)
{
    if (_ownsTarget && _targetProcess is not null && !_targetProcess.HasExited)
    {
        SendSigTermAndWait(quickGracePeriod ?? TimeSpan.FromMilliseconds(200));  // brief grace
        if (!_targetProcess.HasExited)
        {
            TryKillTargetAndSettle();  // SIGKILL fallback
        }
    }
    Dispose();
}
```

The brief grace (200 ms) is for cases where caller is *almost certain* target is stuck but wants to give a moment for cleanup just in case. Default to 200 ms — orders of magnitude shorter than the discipline-default 2000 ms but still gives well-behaved targets a chance.

### New substrate primitive: `RequestExit`

Not in original ADR-008. Finding 68 reveals it's worth exposing explicitly:

```csharp
/// <summary>Send SIGTERM (Unix) / CTRL_BREAK_EVENT (Windows TBD) to the target,
/// requesting graceful termination. Returns true if target exited within
/// <paramref name="timeout"/>; false if still alive. Does NOT force-kill on
/// timeout — caller chooses next action (Terminate to force-kill, retry, wait
/// longer, etc.). Layer 1 discipline primitive (ADR-008 / finding 68).</summary>
public bool RequestExit(TimeSpan timeout);
```

This gives substrate consumers the discipline-aligned primitive directly. `Dispose`'s timeout-fallback becomes a composition: `RequestExit(NaturalExitTimeoutMs)` → if false, `Terminate()` → teardown.

## Cross-platform notes

This finding is **macOS-arm64 only**. Phase 9 will validate on:
- **Linux** (x64 + arm64): expect mostly identical POSIX behavior. Some kernel-level differences in signal delivery semantics under specific conditions (e.g., signal-fd, sigwaitinfo) but the substrate doesn't use those.
- **Windows**: requires entirely different mechanism. Equivalent probes use `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, processGroupId)` for the soft-signal-equivalent. Process.Kill(entireProcessTree:true) on Windows calls TerminateProcess (non-catchable) — same hard-kill semantics. The substrate's RequestExit on Windows must P/Invoke kernel32!GenerateConsoleCtrlEvent.

Windows substrate work is its own probe series (probably 49w–54w naming or a separate finding) when we move to that platform.

## Substrate API surface implications

After finding 68, the substrate's `DebugSession` Owned-path Dispose should:

1. **Detect target state** (already done via finding 66 death-detection).
2. **If alive: RequestExit (SIGTERM) → wait for natural exit (2000 ms default).** New: explicit SIGTERM send via P/Invoke `kill(pid, 15)` on Unix.
3. **If still alive after timeout: emit TargetStuckAtDispose anomaly, fall back to Process.Kill (SIGKILL).** Existing behavior.
4. **If dead (now): proceed through death-detection-aware teardown** (finding 66 path).

The two-stage escalation is the discipline-aligned default. Callers who want to skip Stage 1 use `Abandon()` which uses a much shorter grace period. Callers observing a Borrowed target follow the existing `Attach` contract (no kill at all, target's lifecycle is not ours).

## Probe artifacts

| Probe | Target file | Probe file | Fixture (if generated) |
|---|---|---|---|
| 49 | `49-target.cs` | `49-cancel-key-press-smoke.cs` | — (observational, in-line output) |
| 50 | `50-target.cs` | `50-sigterm-handler-smoke.cs` | — |
| 51 | `51-target.cs` | `51-ignoring-target-smoke.cs` | — |
| 52 | `52-target.cs` | `52-tight-loop-smoke.cs` | — |
| 53 | `53-target.cs` | `53-default-disposition-smoke.cs` | — |
| 54 | `54-target.cs` + `54-child-target.cs` | `54-process-tree-smoke.cs` | — |

Fixtures are observational — probe stdout captures all measurements. Re-run for re-validation; outputs should be reproducible within noise (~5 ms timing variance).

## Cross-references

- [ADR-008](../../../docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) — Phase 0 of which this finding is the deliverable. Decisions 1–3 revised based on findings A–E above.
- [finding 67](67-lifecycle-discipline.md) — the discipline-clarification that motivated this empirical work.
- [finding 64](64-substrate-owned-lifecycle.md) — current substrate kill-first protocol (Process.Kill / SIGKILL). Finding 68 supplies the SIGTERM-then-SIGKILL escalation evidence that revises this.
- [finding 66](66-target-death-detection.md) — death-detection routing in Dispose. Compatible with finding 68's escalation: SIGTERM-wait-SIGKILL still ends with HasExited → routes through death-detection.
- POSIX signal reference: `man 7 signal` (macOS / Linux). Windows console control events: `kernel32!GenerateConsoleCtrlEvent`.
- Standard Unix exit code convention: `128 + signal_number` for signal-terminated processes.

## What's settled (Epistemics → Engineering ready)

ADR-008 moves from **Proposed → ready for Accepted** after this finding's incorporation. Revised Decisions 1–3 (above) supersede the originals; Decisions 4–5 (target design + per-probe alignment) are unchanged.

Engineering Increment 1 (substrate API change) can now begin, against evidence-based design:
- `RequestExit(timeout)` primitive — new
- `Terminate()` primitive — existing `TryKillTargetAndSettle` exposed
- `AttachAndOwn` / `Launch` Dispose: SIGTERM → wait → SIGKILL escalation
- `Abandon(timeout?)` as the explicit fast-escalation composition
- `TargetStuckAtDispose` anomaly on timeout fallback

The substrate's discipline contract is now empirically grounded, not assumed.
