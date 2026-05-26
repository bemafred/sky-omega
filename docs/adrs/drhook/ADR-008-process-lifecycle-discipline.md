# ADR-008: Process lifecycle discipline — natural exit by default; explicit `Abandon` for forced termination

**Status:** Proposed — 2026-05-25

## Context

ADR-007 closes Phase 1 substrate-correctness on macOS-arm64 at the *probe level* — all substrate-correctness probes (41–47) PASS at HEAD against the substrate as of finding 66. Phase 8 (mass promotion of those probes into a CI-enforced integration test suite) was attempted in the 2026-05-25 session and surfaced what I initially mis-framed as a substrate accumulation bug ("MCH-RE-3"). Probes 48 / 48b / 48c systematically eliminated each hypothesis; the empirical chain led to the discipline insight in [finding 67](../../../poc/drhook-engine/findings/67-lifecycle-discipline.md).

The crash mechanism uncovered:

```
Test code calls Process.Kill on a Borrowed-Detach-leave-running target
  → mscordbi RC thread is still mid-cleanup for that detached session
  → Kill triggers ExitProcess work item in mscordbi RC thread
  → Dispatched into our CCW... which substrate has freed via _callback.Dispose
  → UAF → SIGBUS on macOS-arm64
```

The substrate behaved correctly given the inputs. The bug was in the inputs: caller violated the Borrowed contract by terminating a target the substrate had explicitly detached-from-and-left-running.

Martin's framing during the session: a well-implemented process ends naturally when its work is done. Kill is the OS-level escape hatch for processes that won't end on their own — a sign of improper implementation (eternal loops, missing signal handlers, deadlocks), not a normal cleanup mechanism. Applied consistently:

- **Borrowed observer (`DebugSession.Attach`)**: substrate does NOT own. Caller MUST NOT terminate. Wait for natural exit. *(Today: correct by virtue of not having a Kill API; contract documented implicitly.)*
- **Owned observer/creator (`DebugSession.AttachAndOwn` / `DebugSession.Launch`)**: today kills-first on Dispose (finding 64). Per discipline, that's wrong default — should be wait-for-natural-exit, with explicit `Abandon()` as the escape hatch (the "extraordinary action" namespace). *(This ADR proposes that change.)*

A second dimension applies because Sky Omega's DrHook is *a debugger substrate specifically*: debuggers exist precisely to observe processes that may misbehave. We cannot assume targets are well-implemented. Real-world examples include OS-level kills (OOM, user force-quit), target crashes (SIGSEGV / unhandled exception), and the "Claude Chat App on macOS" pattern — software that lacks a working graceful-shutdown and ONLY ends when killed externally. The substrate must therefore default to respecting lifecycle (Layer 1) *and* guard for violators (Layer 2) — both, not one or the other.

This ADR scopes the substrate-evolution work that follows. It does not re-litigate the substrate-correctness work itself (which findings 64/65/66 closed at the substrate-mechanics level); it changes the *default behavior* of the substrate's Owned-path APIs to align with the discipline, and adds the explicit escape hatch for the misbehaving-target cases.

### What's NOT in scope

- **Cross-platform (Linux/Windows)** behavior tuning. Phase 9 of ADR-007 handles that. This ADR's decisions are macOS-arm64-validated; per-platform constants may shift in Phase 9.
- **MCP tool API redesign**. The substrate change here affects `DebugSession.cs` and consumers (`EngineSteppingSession`, integration tests, probes). MCP-tool surface naming stays unchanged; if a tool's behavior shifts (e.g., `drhook_step_run`'s cleanup semantics), that's a follow-up surface decision, not part of this ADR.
- **Substrate-correctness probe redesign for probes that already pass**. Probes 41–47 remain validated. Some probe *targets* will be redesigned per Increment 2 below, but the substrate-correctness assertions stay.

## Decision

Five settled positions. Each is open to amendment during Epistemics review.

### Decision 1 — Substrate API: change defaults, add `Abandon` (Option 1 from finding 67)

Today's `DebugSession.AttachAndOwn` and `DebugSession.Launch` both *kill-first* on `Dispose` (finding 64). This ADR changes that default to **wait-for-natural-exit with timeout-fallback to kill**:

```csharp
public void Dispose()  // unchanged signature; behavior change
{
    // ... (existing _pump.Dispose + _disposed gate unchanged) ...

    if (_ownsTarget && _targetProcess is not null)
    {
        // NEW: wait for natural exit. Substrate trusts well-implemented targets to
        // end on their own. Bounded by NaturalExitTimeoutMs (Decision 2).
        bool exitedNaturally = _targetProcess.WaitForExit(NaturalExitTimeoutMs);

        if (!exitedNaturally)
        {
            // FALLBACK (extraordinary): target is stuck. Emit anomaly + kill.
            // The anomaly is the substrate signal that callers should investigate
            // target-side implementation (eternal loop, missing shutdown signal).
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.TargetStuckAtDispose, "mcp-request",
                "DebugSession.Dispose (natural-exit wait expired)",
                Observed: $"target {_targetProcess.Id} did not exit within {NaturalExitTimeoutMs}ms",
                Expected: "target completes its work and exits naturally",
                Context: ...));
            TryKillTargetAndSettle();
        }
    }

    // ... (existing Quiesce/Detach/Terminate cleanup, unchanged) ...
    // Death-detection (finding 66) routes correctly whether target exited naturally
    // or was killed by the fallback above.
}
```

A new explicit method `Abandon` lets callers force termination when they *know* the target is misbehaving:

```csharp
/// <summary>Forcibly terminate the target and tear down the substrate session.
/// Extraordinary measure for targets that violate process lifecycle rules (eternal
/// loops, missing graceful-shutdown handlers, deadlocks). Use Dispose for the normal
/// path — Dispose waits for natural exit and only kills as a timeout fallback. Use
/// Abandon when you know the target won't end on its own and you've chosen to
/// terminate without waiting.
/// Equivalent to Process.Kill + Dispose, semantically aligned to the
/// "extraordinary action" namespace per ADR-008 Layer 2 (debugger guards for
/// lifecycle violators).</summary>
public void Abandon()
{
    if (_ownsTarget && _targetProcess is not null && !_targetProcess.HasExited)
    {
        TryKillTargetAndSettle();
    }
    Dispose();   // Death-detection (finding 66) handles the now-dead target.
}
```

`Attach` (Borrowed) is **unchanged**. Its existing semantics — substrate does NOT kill — were already discipline-correct. The contract becomes explicitly documented (caller MUST NOT terminate the target after Attach; if forced termination is needed, use `AttachAndOwn` + `Abandon`).

**Why Option 1 over Option 2 (new API names)**: avoids API surface fork; preserves the `AttachAndOwn` / `Launch` names that match their semantic role (substrate owns the target's debug-session lifecycle); the behavior change is observable but backwards-compatible for callers that didn't depend on the kill-first timing (most don't — they Dispose at end-of-use, target dies as side effect).

### Decision 2 — `NaturalExitTimeoutMs` default: 5000 ms, configurable per-session

The natural-exit wait needs a bound. Too short → kills well-behaved targets that take a beat to wind down (legitimate I/O drain, async cleanup, etc.). Too long → bad UX, slow tests, hung MCP tools.

**Default: 5000 ms** (5 seconds). Empirically generous for typical test-runner shutdown + brief CLI tools; not so long that misbehaving targets indefinitely hang Dispose.

**Configurable per-session**: `AttachAndOwn(int processId, IDebugEventSink sink, TimeSpan? naturalExitTimeout = null)` — overload accepts a timeout for callers that know their target's expected work duration (e.g., a probe running 1000-iteration target → 30s; a long-running debug session → 60s). `null` uses the 5000 ms default.

**On timeout**: emit `TargetStuckAtDispose` anomaly, fall back to kill via existing `TryKillTargetAndSettle`. Substrate's `Dispose` does not throw on timeout — it surfaces via the anomaly stream (consistent with substrate-correctness anomalies from findings 65/66).

**Cross-platform note**: 5000 ms is macOS-arm64 empirical default. Phase 9 may tune per-platform; the API stays the same.

### Decision 3 — `Abandon` semantics: synchronous, kill-then-Dispose

`Abandon` is synchronous: it kills the target (with `KillSettleMs` settle as today's `TryKillTargetAndSettle`), then runs the rest of `Dispose` (death-detection routes through the dead-target path per finding 66).

Rationale: an async `Abandon` would require the caller to track when teardown completes; the typical use case is "I'm done, force-quit, clean up" — a synchronous answer fits the caller's mental model. The settle inside `TryKillTargetAndSettle` (100 ms) plus `ExitWorkSettleMs` (200 ms in dead-target path) is sub-second total — well within a normal `Dispose`-equivalent cost.

`Abandon` does NOT take a timeout parameter — it's explicitly the "I've chosen to kill, no waiting" semantic. Callers who want bounded waiting use the timeout-overload of `AttachAndOwn` and let `Dispose` time-out + fall back to kill.

### Decision 4 — Target design constraint: targets that exit naturally

Targets used by Sky Omega probes and integration tests **MUST exit naturally** under normal substrate observation. This is a substrate-discipline constraint on *substrate-validation targets*, not a constraint on real-world targets (real-world targets may be ill-implemented — the substrate's job is to handle that gracefully, per Layer 2 guard).

Concretely:

| Pattern | Allowed | Why / Alternative |
|---|---|---|
| `Thread.Sleep(Timeout.Infinite)` | **No** | Forces kill. Use bounded sleep or finite work. |
| `while (true) { Thread.Sleep(20); }` | **No** | Eternal loop. Use `for (int i = 0; i < N; i++) { ... }`. |
| `Thread.Sleep(TimeSpan.FromSeconds(30))` | **Discouraged** | Substrate-observed targets need legitimate observable work, not idle waiting. |
| Finite repetition of substrate-observable work | **Yes** | Probes/tests assert specific substrate behavior; target should generate that behavior within finite bounded work. |
| Listening for shutdown signal | **Yes for production-shape targets**, not needed for probes | Real-world targets may legitimately run until signaled; probes don't model this. |

For probes that need *long-running* observable work (e.g., probe 42's 50-cycle informational flood, probe 43's concurrent Pause + STOPPING under load): the model becomes **one target per cycle, each doing finite work**, NOT one long-lived target observed across many cycles. Probe 48's pattern already validates this (10 fresh targets, each finite). This may require restructuring probes 42/43/44/45 around finite-target lifecycle.

### Decision 5 — Per-probe alignment: finite targets per probe-cycle

Each substrate-correctness probe's target is redesigned for finite-natural-exit, even when the probe loop is multi-cycle. The probe pattern becomes:

```
for each cycle:
    spawn fresh target (finite work, will exit naturally)
    Attach (Borrowed) OR AttachAndOwn (Owned)
    observe substrate-correctness assertion
    Dispose
    [optional] WaitForExit(timeout) → assert target exited naturally
    [no Process.Kill]
end
```

For probes that need *sustained* observable behavior within a single substrate session (e.g., "Dispose during heavy callback flood"), the target's finite work is *long enough* (e.g., 1000 Thread.Start/Join iterations ≈ a few seconds of CPU-bound activity) that the substrate's Dispose timing fits comfortably within the target's natural lifetime.

For probe 42's 50-cycle pattern specifically: the new model is 50 *spawn-fresh-target* cycles, each with a target that does ~100 iterations of Thread.Start/Join (~50ms work) and exits. Total probe runtime increases modestly (50 spawn-cycles × ~200 ms per cycle ≈ 10 s, vs current 30 s); semantic alignment is preserved (substrate handles N back-to-back Borrowed sessions cleanly).

## Phases

Five engineering increments, in dependency order. Each is a probe + finding + commit unit.

### Increment 1 — Substrate API change

**Substrate code:**
- `DebugSession.Dispose` behavior change: wait-for-natural-exit (Owned-path) with `NaturalExitTimeoutMs` fallback to existing kill-first
- New `DebugSession.Abandon()` method
- `AttachAndOwn` overload accepting `TimeSpan? naturalExitTimeout`
- `Launch` overload accepting `TimeSpan? naturalExitTimeout`
- New `AnomalyKind.TargetStuckAtDispose`
- API doc updates (XMLdoc on `Attach` / `AttachAndOwn` / `Launch` / `Abandon` reflecting the discipline contract)

**Validation:**
- New probe (call it 49 — natural-exit-on-Dispose) validates the wait-then-fallback-kill path with a target that exits naturally before timeout, AND a target that takes longer than timeout (substrate falls back to kill + emits `TargetStuckAtDispose`)
- 59/59 unit tests still pass (existing substrate-internals are unchanged at unit-test level)
- Probes 41–47 still pass (they may need adjusted target lifetimes, scoped to Increment 2)

**Finding:** finding 68 (or sequential) documenting the substrate API change + probe 49 validation.

### Increment 2 — Probe target redesign

**Target redesigns:**
- `07-target.cs`: bounded throw/catch loop (e.g., 1000 iterations) then return
- `42-target.cs`: bounded Thread.Start/Join loop (e.g., 1000 iterations) then return
- `47-target.cs`, `48-target.cs`, `48b-target.cs`: brief observable work then return (e.g., a few Thread.Start/Join then exit)
- Each updated target has READY sentinel as before, plus a "done" marker on stdout for probes to optionally wait on

**Probe updates:**
- Probes 42/43/44/45/47/48/48b updated for fresh-target-per-cycle pattern (Decision 5)
- Probes assert via `WaitForExit` that targets exited naturally (validates Layer 1 discipline)
- Probes that need to kill (probe 44 phase C, probe 47, probe 48c) use `session.Abandon()` instead of explicit `Process.Kill` — substrate-mediated, discipline-aligned

**Validation:** all probes 41–47, 48/48b/48c PASS against new substrate + new targets. Existing fixtures replaced.

**Finding:** finding 69 (or sequential) documenting the target redesign + probe behavior changes.

### Increment 3 — Integration target + existing integration test redesign

**Integration target redesigns:**
- MTP `IdleForDebuggerObservation`: replaced with `RunBriefObservableWork` (finite Thread.Start/Join × N then return)
- VSTest `IdleFact.IdleForDebuggerObservation`: same fix in xUnit `[Fact]` shape

**Existing integration tests:**
- `AttachDisposeTest.AttachAndOwn_MtpTarget_BriefIdle_DisposeCleanly` → renamed `AttachAndOwn_MtpTarget_BriefWork_NaturalExit`, uses `WaitForExit` assertion after Dispose
- `VstestAttachDisposeTest.AttachAndOwn_VstestTestHost_BriefIdle_DisposeCleanly` → same pattern

**Validation:** 2/2 integration tests PASS with discipline-aligned semantics, completing in <2 s total.

### Increment 4 — Phase 8 mass promotion (the deliverable that gates ADR-007 Phase 1 closure)

**Promote substrate-correctness probes into integration tests:**

| Probe | MTP integration test | VSTest integration test |
|---|---|---|
| 41 (anomaly injection) | `AnomalyInjectionTest.Mtp` | `AnomalyInjectionTest.Vstest` |
| 42 (Dispose during `_resumeHandler`) | `InformationalFloodTest.Mtp` | `InformationalFloodTest.Vstest` |
| 43 (concurrent Pause + STOPPING) | `ConcurrentPauseStopTest.Mtp` | `ConcurrentPauseStopTest.Vstest` |
| 44 (drhook-detach-exit-race, both phases) | `KillCoincidentTest.Mtp` | `KillCoincidentTest.Vstest` |
| 45 (worker-thread exception path) | `WorkerExceptionTest.Mtp` | `WorkerExceptionTest.Vstest` |
| 47 (external target death) | `ExternalDeathTest.Mtp` | `ExternalDeathTest.Vstest` |

Plus the 2 from Increment 3: 14 integration tests total.

**All tests use discipline-aligned patterns**: no explicit `Process.Kill`, `WaitForExit` assertions, `Abandon` for substrate-mediated forced termination where the scenario requires it (e.g., probe 47 / probe 44 phase C tests model external/forced death via target self-termination or substrate `Abandon`, not test-code `Kill`).

**Validation:** 14/14 PASS in one MSTest exe invocation. No SIGSEGV / SIGBUS. CI-enforced on macOS-arm64.

### Increment 5 — ADR-007 amendment + closure

- ADR-007 amended: Phase 8 checklist updated to reflect Increment 4 deliverables; Phase 1 substrate-correctness arc marked TRULY complete (probe-level + CI-enforced) post-Increment 4
- `docs/limits/drhook-detach-exit-race.md` cross-reference to this ADR (the Resolved status from finding 66 is reaffirmed; the discipline contract is now explicit)
- CLAUDE.md updates if any API surface name changes (none planned per Decision 1, but verify)
- This ADR (ADR-008) status: Proposed → Accepted → Completed across Increments 1–4

## Validation

This ADR moves **Proposed → Accepted** when:
- The 5 decisions are reviewed + confirmed (or amended) by Martin
- The phase scope is agreed to be the right granularity (single ADR covers all 5 increments, vs splitting)

It moves **Accepted → Completed** when:
- All 5 increments are done (each with probe + finding + commit)
- Findings 68 / 69 / etc. document the engineering steps
- 14 integration tests pass in CI on macOS-arm64
- ADR-007 Phase 1 substrate-correctness arc is closed at the integration-enforcement level

## Consequences

**For substrate code:**
- `DebugSession` Owned-path Dispose behavior changes (kill-first → wait-then-kill-on-timeout). Backwards-compatible for callers that didn't depend on the kill-first timing; observable as longer Dispose time for well-behaved targets (up to 5s for natural exit instead of ~300 ms kill-first today, but with much cleaner semantics).
- New `Abandon` method + new `AnomalyKind.TargetStuckAtDispose`. Surface expansion, no removal.
- `Attach` (Borrowed) unchanged in behavior; contract documented explicitly.

**For substrate-validation work:**
- All probe targets exit naturally (no more `Timeout.Infinite` / `while(true)` patterns).
- Probes use `Abandon` for forced-termination scenarios (probe 47, probe 44 phase C, probe 48c) — substrate-mediated, discipline-aligned.
- Integration tests use `WaitForExit` assertions to validate Layer 1 discipline.
- Phase 8 mass promotion delivers 14 CI-enforced integration tests.

**For MCP tools (`drhook_step_run`, `drhook_step_launch`):**
- Tool semantics unchanged at the surface. Internally, `EngineSteppingSession` uses substrate APIs that now wait-for-natural-exit. For typical interactive debug-and-quit usage, target processes will be given up to 5 s to exit cleanly after `Dispose` — usually invisible (typical interactive debug session ends with a `Quit` command that exits the target cleanly).
- If a user's debug target hangs (eternal loop, etc.), `drhook_step_run` would now block up to 5 s on cleanup. Acceptable cost for the discipline improvement. Future: explicit "abandon session" MCP tool for hung-target cases.

**For the substrate's strategic position:**
- The substrate becomes *documentably* discipline-respecting at the API surface. This is part of substrate-grade work — the substrate doesn't just behave correctly; it teaches callers what discipline looks like by enforcing it via API shape.
- The two-layer discipline (Layer 1 default + Layer 2 debugger-guard) becomes substrate epistemic property — surfaceable via API documentation and via the substrate's anomaly stream (`TargetStuckAtDispose` is a substrate-correctness signal AND a substrate-discipline signal pointing at the target's implementation).

## References

- [ADR-007](ADR-007-teardown-concurrency-test-debug.md) — substrate-correctness arc and Phase 8 mass-promotion that this ADR unblocks
- [ADR-006](ADR-006-drhook-engine.md) — DrHook.Engine substrate; this ADR evolves its API per the discipline insight
- [finding 64](../../../poc/drhook-engine/findings/64-substrate-owned-lifecycle.md) — substrate-owned lifecycle (AttachAndOwn kill-first protocol that this ADR rethinks)
- [finding 65](../../../poc/drhook-engine/findings/65-probe42-redesign-regression.md) — dispatch-settle in `TryResumeForDetach`; live-target informational flood race closed
- [finding 66](../../../poc/drhook-engine/findings/66-target-death-detection.md) — target-death detection; Layer 2 guard for misbehaving / dying targets (preserved under new framing)
- [finding 67](../../../poc/drhook-engine/findings/67-lifecycle-discipline.md) — discipline articulation that drives this ADR
- [`feedback_process_lifecycle_discipline`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_process_lifecycle_discipline.md) — memory entry for the discipline; generalizable beyond DrHook.Engine
- Martin's framing during 2026-05-25 session: "We should rely on natural process endings, but being a debugger — we should *guard* for misbehaving targets that *violates* process lifecycle rules." + the Claude Chat App lens for OS-level kill-required processes.
