# ADR-008: Process lifecycle discipline — natural exit by default; explicit `Abandon` for forced termination

**Status:** Completed — 2026-05-26 (Increments 1–4 shipped; Phase 8 mass promotion delivers 12/12 CI-enforced integration tests on macOS-arm64 across MTP + VSTest)

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

Five accepted positions. Decisions 1–3 were revised against empirical evidence from Phase 0 (finding 68); the original Proposed-state versions are visible in git history. Decisions 4–5 unchanged from the Proposed draft.

### Decision 1 (Accepted) — Substrate API: SIGTERM-then-SIGKILL escalation; explicit `RequestExit` primitive; explicit `Abandon`

Today's `DebugSession.AttachAndOwn` and `DebugSession.Launch` both *kill-first* on `Dispose` (finding 64) — sending SIGKILL via `Process.Kill`. Finding 68 establishes empirically that .NET CoreCLR delivers SIGTERM correctly to handler-less targets in ~12 ms and to handler-equipped targets in ~45 ms. The discipline-aligned default becomes **soft-signal first, hard kill as fallback**:

```csharp
public void Dispose()  // signature unchanged; behavior change per finding 68
{
    // ... (existing _pump.Dispose + _disposed gate unchanged) ...

    if (_ownsTarget && _targetProcess is not null)
    {
        // Stage 1 — Request graceful exit via SIGTERM (Unix) /
        // GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT) (Windows). Catchable by target;
        // well-behaved CLIs exit cleanly within tens of milliseconds (finding 68).
        bool exitedNaturally = RequestExit(NaturalExitTimeout);

        if (!exitedNaturally)
        {
            // Stage 2 — Target is stuck. Surface as substrate signal, then escalate
            // to non-catchable kill (Process.Kill → SIGKILL / TerminateProcess).
            // The anomaly tells callers "your target violates process lifecycle
            // discipline" — actionable upstream signal, not a substrate excuse.
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.TargetStuckAtDispose, "mcp-request",
                "DebugSession.Dispose (natural-exit wait expired)",
                Observed: $"target {_targetProcess.Id} did not respond to SIGTERM within {NaturalExitTimeout.TotalMilliseconds}ms",
                Expected: "target completes its work and exits naturally on SIGTERM",
                Context: ...));
            TryKillTargetAndSettle();   // existing kill-first; SIGKILL fallback
        }
    }

    // ... (existing Quiesce/Detach/Terminate cleanup, routes via finding 66
    //      death-detection regardless of how target died) ...
}
```

**New substrate primitive `RequestExit`** — exposes the Layer 1 discipline directly to substrate consumers:

```csharp
/// <summary>Send a graceful-termination request to the Owned target — SIGTERM on Unix,
/// CTRL_BREAK_EVENT on Windows. Wait up to <paramref name="timeout"/> for the target
/// to exit naturally. Returns true if the target exited within the window; false if
/// it's still alive. Does NOT force-kill on timeout — caller chooses the next action
/// (call Abandon to force-kill; retry; wait longer; etc.).
/// Layer 1 discipline primitive per ADR-008 / finding 68. The "ask nicely first"
/// surface that complements the kill escape hatch.</summary>
public bool RequestExit(TimeSpan timeout);
```

**`Abandon` method (revised semantics)** — the explicit fast-escalation composition:

```csharp
/// <summary>Forcibly terminate the target after a brief grace period and tear down
/// the substrate session. Extraordinary measure for targets that violate process
/// lifecycle rules — eternal loops, missing graceful-shutdown handlers, deadlocks.
/// Internally: sends SIGTERM, waits <paramref name="briefGrace"/> (default 200 ms),
/// then sends SIGKILL if target still alive, then runs Dispose. The brief grace is
/// a final courtesy — well-behaved targets exit in tens of ms on SIGTERM per
/// finding 68, so 200 ms catches them; misbehaving targets get the non-catchable
/// SIGKILL within sub-second total budget.
/// Use Dispose for the normal path (waits NaturalExitTimeout for natural exit).
/// Use Abandon when you know the target won't end on its own and you've chosen
/// to terminate quickly.</summary>
public void Abandon(TimeSpan? briefGrace = null);
```

`Attach` (Borrowed) is **unchanged**. Its existing semantics — substrate does NOT kill — were already discipline-correct. The contract is now explicitly documented (caller MUST NOT terminate the target after Attach; if forced termination is needed, use `AttachAndOwn` + `Abandon`).

### Decision 2 (Accepted) — `NaturalExitTimeout` default: 2000 ms, configurable per-session

Finding 68's empirical observations: well-behaved targets exit within 12–45 ms of SIGTERM (probes 49, 50, 52, 53). 2000 ms is ~50× the observed worst-case for well-behaved targets — generous enough to absorb host-load variance and longer-cleanup targets, tight enough that misbehaving targets surface quickly.

(Original Proposed default was 5000 ms based on intuition; revised to 2000 ms based on evidence.)

**Configurable per-session**: `AttachAndOwn(int processId, IDebugEventSink sink, TimeSpan? naturalExitTimeout = null)` — overload accepts a timeout for callers that know their target's expected cleanup duration. `null` uses the 2000 ms default.

**On timeout**: emit `TargetStuckAtDispose` anomaly, fall back to `TryKillTargetAndSettle` (SIGKILL). Substrate's `Dispose` does not throw on timeout — surfaces via the anomaly stream (consistent with substrate-correctness anomalies from findings 65/66).

**Cross-platform note**: 2000 ms is macOS-arm64 empirical default. Phase 9 may tune per-platform (e.g., Windows console-control-event signaling has higher overhead); API stays the same.

### Decision 3 (Accepted) — `Abandon` semantics: two-stage internally (SIGTERM-with-brief-grace then SIGKILL), single synchronous API call

`Abandon` is synchronous and two-stage:
1. Send SIGTERM (graceful request).
2. Wait `briefGrace` (default 200 ms) — well-behaved targets exit in this window per finding 68.
3. If target still alive: send SIGKILL via `TryKillTargetAndSettle` (existing 100 ms `KillSettleMs` for kernel reap).
4. Run `Dispose` (death-detection routes through dead-target path per finding 66; ExitWorkSettleMs 200 ms before Detach).

Total `Abandon` budget under worst case: 200 ms grace + 100 ms kill-settle + 200 ms exit-work-settle + teardown ≈ 600 ms. Well under a second.

The 200 ms `briefGrace` default is the discipline answer to "I want fast termination but I'll still give the target a moment to clean up if it can" — orders of magnitude shorter than the 2000 ms `NaturalExitTimeout` default (which is for the normal-path Dispose), but still respects targets that genuinely respond fast.

(Original Proposed Decision 3 said `Abandon` was kill-first with no grace. Revised based on finding 68's evidence that 200 ms catches even handler-equipped well-behaved targets, making the brief grace effectively free.)

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

Six phases. Phase 0 is Epistemics work that MUST complete before the engineering decisions (1–5) are settled. Phase 0's ground-truth finding may revise Decisions 1–5 — they are explicitly *proposed positions subject to evidence*, not final. Engineering increments 1–5 (formerly the "phases" of the prior draft) follow once Phase 0 produces a finding doc with the empirical truth table.

### Phase 0 — Process lifecycle ground truth (Epistemics)

Before any substrate API change, establish empirical ground truth for process termination behavior across signal types, target shapes, and runtime platforms. The Decisions 1–5 below are written against my (Claude's) intuitions about process signals; for a "state of the art" substrate, intuitions are not enough — evidence is.

Hypothesis space the probes must populate:

- **Signal types and catchability**:
  - SIGINT (Unix) / CTRL_C_EVENT (Windows) — catchable, ignorable; what's the .NET CoreCLR default behavior; what does `Console.CancelKeyPress` actually catch?
  - SIGTERM (Unix) / no direct Windows equivalent — catchable; `AppDomain.ProcessExit` semantics; differences between platforms.
  - SIGKILL (Unix) / TerminateProcess (Windows) — NOT catchable, by kernel design. Target has no opportunity to respond. `Process.Kill` in .NET defaults to this.
  - Ctrl+C from terminal vs. programmatic signal — supposed correlation, real differences.
- **Target shapes**:
  - Well-behaved: explicit signal handler that gracefully exits.
  - Ignoring: catches signal, marks `Cancel = true`, keeps running.
  - Default: no handler at all — what's the CoreCLR baseline?
  - Tight-loop: pure CPU-bound, no async safepoints — can the runtime even interrupt?
- **Process tree**:
  - Signal to leader vs. signal to process group.
  - `Process.Kill(entireProcessTree:true)` semantics on Unix vs. Windows.
  - Orphaned descendants — adopted by init on Unix; behavior on Windows.

**Probes 49–54** (this phase's deliverables):

| Probe | Target shape | Signal sent | Validates |
|---|---|---|---|
| 49 | `Console.CancelKeyPress` handler, graceful exit | SIGINT (Unix) / CTRL_C_EVENT (Windows) | Soft signal honored by well-behaved target |
| 50 | `AppDomain.ProcessExit` handler | SIGTERM (Unix) / platform equivalent (Windows TBD) | Soft signal honored via different .NET surface |
| 51 | Catches SIGINT, sets `Cancel = true` — ignores | SIGINT then SIGKILL | Soft signal can be ignored; hard kill cannot |
| 52 | `while (true) { /* CPU-bound, no yield */ }` | SIGTERM then SIGKILL | CoreCLR's signal-checking frequency under tight CPU work |
| 53 | No explicit handler | SIGINT, SIGTERM separately | CoreCLR default signal disposition |
| 54 | Target + 2 spawned children | Signal-to-leader vs. signal-to-pgrp vs. `Process.Kill(entireProcessTree:true)` | Process-tree propagation semantics |

Each probe outputs:
- Target exit code (signal-related or clean 0)
- Time-to-exit (how fast after signal arrival)
- Observable side effects (did the handler run? did "graceful cleanup" complete?)
- Cross-platform notes (probes 49–54 are macOS-arm64 initially; Phase 9 expands to Linux + Windows)

**Phase 0 deliverable: finding 68 — "Process lifecycle ground truth (macOS-arm64 reference)."** Documents what was probed, what was empirically observed, what the substrate API design should commit to vs. what platform-specifics should be configurable.

**Decisions 1–5 status after Phase 0**: each Decision is reviewed against the ground-truth finding. Amend where evidence diverges from current proposed positions. Move ADR-008 from Proposed → Accepted only after this review.

### Phase 0.1 — Cross-platform expansion (deferred to ADR-007 Phase 9)

Probes 49–54 on Linux/x64, Linux/arm64, Windows/x64, Windows/arm64. Substrate API may need per-platform-tuning constants; design Phase 0's substrate-API commitments to be platform-agnostic in shape, platform-specific in tuning.

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
- **Phase 0 is COMPLETE** — probes 49–54 implemented, executed on macOS-arm64, finding 68 published with empirical ground-truth table
- The 5 decisions are reviewed against finding 68's evidence and confirmed (or amended) by Martin
- The phase scope is agreed to be the right granularity (single ADR covers all phases, vs splitting)

It moves **Accepted → Completed** when:
- All 5 engineering increments are done (each with probe + finding + commit)
- Findings 68 (Phase 0 ground truth) + 69+ (Increments) document the work
- Integration tests pass in CI on macOS-arm64 (12/12 delivered; the planned probe 47 → integration test was dropped as redundant — substrate death-detection is structurally tested by the existing Dispose paths and the external-kill scenario doesn't compose naturally with the `WaitForExit` Layer-1 discipline assertion)
- ADR-007 Phase 1 substrate-correctness arc is closed at the integration-enforcement level

**Status as of 2026-05-26:**
- Increment 1 — substrate API SIGTERM-then-SIGKILL escalation + `RequestExit` + `Abandon` + `TargetStuckAtDispose` anomaly ([finding 69](../../../poc/drhook-engine/findings/69-increment1-substrate-api.md))
- Increment 1b — Owned-path Dispose invokes `TryResumeForDetach` before SIGTERM so substrate acts on its knowledge of target halt state ([finding 73](../../../poc/drhook-engine/findings/73-increment1b-release-pending-stops.md))
- Increment 2 — probe target redesign for natural exit; 5 targets annotated as intentional-violator-by-design ([finding 70](../../../poc/drhook-engine/findings/70-increment2-target-redesign.md))
- Increment 3 — integration target + existing integration test redesign; `WaitForExit` Layer-1 assertions added ([finding 71](../../../poc/drhook-engine/findings/71-increment3-integration-target-redesign.md))
- Increment 4 — Phase 8 mass promotion; 12/12 integration tests across 6 substrate-correctness scenarios × {MTP, VSTest} ([finding 72](../../../poc/drhook-engine/findings/72-increment4a-mass-promotion.md) Phase 8a, [finding 73](../../../poc/drhook-engine/findings/73-increment1b-release-pending-stops.md) Phase 8b probe 41, [finding 74](../../../poc/drhook-engine/findings/74-increment4c-concurrent-pause-stop-promoted.md) Phase 8c probe 43)
- Increment 5 — ADR-007 amendment + this ADR's closure (this Status transition)

## Consequences

**For substrate code:**
- `DebugSession` Owned-path Dispose behavior changes (kill-first → `TryResumeForDetach` → SIGTERM-with-`NaturalExitTimeout`(2000 ms default) → SIGKILL-on-timeout). Backwards-compatible for callers that didn't depend on the kill-first timing; observable as longer Dispose time for well-behaved targets (typically tens of ms for SIGTERM-honoring targets, up to 2 s budget for stuck targets, but with much cleaner semantics).
- New `RequestExit(TimeSpan)` primitive (Layer 1 discipline) + new `Abandon(TimeSpan?)` method (fast-escalation composition) + new `AnomalyKind.TargetStuckAtDispose`. Surface expansion, no removal.
- New `PosixSignals` interop helper P/Invokes `libc.kill` (Unix only; Windows path deferred to ADR-007 Phase 9).
- `Attach` (Borrowed) unchanged in behavior; contract documented explicitly. `RequestExit` throws `InvalidOperationException` on Borrowed sessions.

**For substrate-validation work:**
- All probe targets exit naturally (no more `Timeout.Infinite` / `while(true)` patterns).
- Probes use `Abandon` for forced-termination scenarios (probe 47, probe 44 phase C, probe 48c) — substrate-mediated, discipline-aligned.
- Integration tests use `WaitForExit` assertions to validate Layer 1 discipline.
- Phase 8 mass promotion delivers 14 CI-enforced integration tests.

**For MCP tools (`drhook_step_run`, `drhook_step_launch`):**
- Tool semantics unchanged at the surface. Internally, `EngineSteppingSession` uses substrate APIs that now wait-for-natural-exit. For typical interactive debug-and-quit usage, target processes will be given up to 2 s (default `NaturalExitTimeout`) to exit cleanly after `Dispose` — usually invisible (CoreCLR default SIGTERM disposition exits in tens of ms).
- If a user's debug target hangs (eternal loop, etc.), `drhook_step_run` would now block up to 2 s on cleanup and surface a `TargetStuckAtDispose` anomaly. Acceptable cost for the discipline improvement. Future: explicit "abandon session" MCP tool for hung-target cases.

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
- [finding 68](../../../poc/drhook-engine/findings/68-process-lifecycle-ground-truth.md) — Phase 0 empirical ground truth on macOS-arm64 (probes 49–54); revised Decisions 1–3 against intuition-based originals
- [finding 69](../../../poc/drhook-engine/findings/69-increment1-substrate-api.md) — Increment 1 substrate-API delivery
- [finding 70](../../../poc/drhook-engine/findings/70-increment2-target-redesign.md) — Increment 2 probe target redesign
- [finding 71](../../../poc/drhook-engine/findings/71-increment3-integration-target-redesign.md) — Increment 3 integration target redesign + existing-test natural-exit pattern
- [finding 72](../../../poc/drhook-engine/findings/72-increment4a-mass-promotion.md) — Increment 4a Phase 8a mass promotion (8 tests)
- [finding 73](../../../poc/drhook-engine/findings/73-increment1b-release-pending-stops.md) — Increment 1b Owned-Dispose releases pending stops before SIGTERM + Increment 4b probe 41 promoted (AnomalyInjectionTest)
- [finding 74](../../../poc/drhook-engine/findings/74-increment4c-concurrent-pause-stop-promoted.md) — Increment 4c ConcurrentPauseStopTest promoted; Phase 8 COMPLETE (12/12)
- [`feedback_process_lifecycle_discipline`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_process_lifecycle_discipline.md) — memory entry for the discipline; generalizable beyond DrHook.Engine
- Martin's framing during 2026-05-25 session: "We should rely on natural process endings, but being a debugger — we should *guard* for misbehaving targets that *violates* process lifecycle rules." + the Claude Chat App lens for OS-level kill-required processes.
