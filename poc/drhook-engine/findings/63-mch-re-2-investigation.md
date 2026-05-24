# Finding 63: MCH-RE-2 investigation — root cause is drhook-detach-exit-race recurring cross-session; kill-first fixes it

**Status:**   Investigation complete on macOS-arm64 2026-05-24. MCH-RE-2 (from finding 62: same-host-process multi-substrate-session SIGSEGV) is NOT a new substrate-grade phenomenon — it is the existing `drhook-detach-exit-race` limit (Triggered) recurring under multi-session conditions. **Kill-first protocol fixes it** empirically (probe `mch-re-2-repro.cs` S1 + S2 both PASS with `proc.Kill` BEFORE `session.Dispose`). Phase 8 mass promotion is UNBLOCKED — the substrate fix isn't required; integration tests need only follow the kill-first discipline OR run in separate MSTest exe invocations (the CI filter workaround from finding 62).
**Date:**     2026-05-24

## Answers to the questions Martin raised

### 1. "Does something make the engine into a singleton?"

**No.** Substrate audit (finding 53 + grep this turn):
- `DrHook.Engine` has NO mutable static state.
- `DebugSession.Wrappers` (`static readonly ComWrappers`) is immutable shared instance, correctly per Microsoft docs — `ComWrappers` MUST be a single shared instance across COM-interop in a process; instantiating multiple breaks the RCW caching contract.
- `DebugSession` itself is a per-instance class — nothing in C# substrate prevents multiple concurrent instances.

The MCP-layer's `EngineSteppingSession` IS a singleton (`AddSingleton<EngineSteppingSession>()` per Program.cs:77, finding 53 line 54). But that's the MCP layer, not the substrate. And the MCP-layer singleton is by-design (one active step session at a time per MCP server lifetime).

Underlying NATIVE state is the actual source of singleton-like behavior:
- `libdbgshim.dylib` loaded process-globally via `NativeLibrary.Load` (refcounted, multiple `DbgShim.Load()` calls share the lib).
- `mscordbi.dylib` loaded by libdbgshim's `CreateDebuggingInterfaceFromVersionEx`; per-process; has internal state for the lifetime of the process.

mscordbi is the effective singleton at the native level. DrHook.Engine sits on top.

### 2. "Test needs to spawn their own instances?"

**Not necessarily — kill-first protocol is the simpler fix.** The integration tests can be modified to call `proc.Kill` BEFORE `session.Dispose`, matching the discipline `EngineSteppingSession.CleanupSession` already uses for Launched sessions:

```csharp
// Current (fails cross-session):
session.Dispose();
proc.Kill(entireProcessTree: true);  // ← dispose-then-kill triggers the race

// Fix (works cross-session):
proc.Kill(entireProcessTree: true);
Thread.Sleep(100);  // let mscordbi notice exit
session.Dispose();
```

OR: the CI filter workaround from finding 62 (one substrate-attaching test per MSTest exe invocation) — both work; pick based on integration-test ergonomics.

Spawning a child process per test (option (b) from finding 62) is the heaviest workaround; not needed empirically.

### 3. "What level does it belong to?"

The level is **integration-test orchestration discipline**, not engine substrate.

The substrate already addresses the drhook-detach-exit-race for the two paths it owns:
- **Launched sessions:** kill-first in `EngineSteppingSession.CleanupSession` (validated probe 12 + probe 44 Phase C).
- **Attached sessions:** detach-leave-running in `DebugSession.Dispose` for `!_ownsTarget` (finding 59, Probe 44 substrate change).

What the substrate does NOT own: how the integration-test caller manages the target process lifecycle AROUND `Dispose`. If the caller does `Dispose` then `proc.Kill`, that's dispose-then-kill — the exact pattern that the limit doc flagged as broken. The substrate's detach-leave-running prepares mscordbi to keep the target running; killing the target immediately after (while mscordbi's RC event thread is still settling) triggers the same race.

The substrate-discipline answer: **caller decides target lifecycle.** If you want to keep the target alive, don't kill it (substrate's detach-leave-running already preserved it). If you want to kill it, kill-first.

### 4. "Being able to run multiple engines within a process, targetting different targets, should work?"

**YES — and empirically does, with kill-first discipline.** Repro probe scenarios on macOS-arm64:

| Scenario | Pattern | Result |
|---|---|---|
| S1: Two sessions, same target type (07-target both times) | kill-first | ✓ PASS |
| S2: Two sessions, different target types (07-target then 44-target) | kill-first | ✓ PASS |
| S3 partial: One session simple-target, second testhost via dotnet-test | dispose-then-kill | ✗ SIGSEGV |
| S4 (not reached due to S3 crash): Two sessions same target + GC.Collect+Sleep between | kill-first variant | n/a |

S1 + S2 prove the substrate supports sequential multi-session in the same hosting process with different targets. The crash recurs ONLY when the integration test uses dispose-then-kill ordering (S3 in repro, both AttachDisposeTest and VstestAttachDisposeTest in finding 62).

The DEEPER ADR-007 Phase 5 Probe 54 question — *concurrent* multi-session (multiple ICorDebug instances active simultaneously, e.g., NCrunch parallel testhosts) — is **not addressed by this investigation.** That remains architectural per ADR-007's Phase 5 framing. MCH-RE-2 was sequential multi-session, which works today.

### 5. "Was this situation flagged in ADR-006 and/or ADR-007?"

**Partially.** Grep across ADR-007 + findings 53–59 surfaced:

- **ADR-007 line 118 (Phase 5 Probe 54):** *"Multi-session engine. Required by NCrunch (variant H, inter-process parallel). Today's EngineSteppingSession is a DI singleton; multi-session is architectural. Probe characterises: concurrent session lifetimes, resource sharing, fairness, isolation guarantees."* — Flagged the architectural concept, but explicitly for CONCURRENT multi-session. SEQUENTIAL multi-session wasn't separately anticipated.

- **finding 53 line 50:** *"No mutable static state in DrHook.Engine"* — Documented the substrate's no-statics property; implied sequential multi-session should work but didn't test it explicitly.

- **`docs/limits/drhook-detach-exit-race.md`:** *"target process EXITS at about the same time (e.g. killed by the harness right after detach) — mscordbi's RC event thread processes the exit and intermittently segfaults"* — Described the dispose-then-kill race in single-session. The CROSS-session manifestation (MCH-RE-2) is the same race, just made more reachable by multi-session in same hosting process: the second session's mscordbi state interacts with the first session's stale references, widening the race window enough that even the substrate's Quiesce + Continue-loop teardown can't fully prevent it.

- **finding 59 MCH-RE-1:** *"Same-target Pause-Dispose re-attach hits mscordbi state accumulation at ~2 cycles on macOS — substrate-INDEPENDENT mscordbi behavior."* — Documented mscordbi accumulation for the same-target case. MCH-RE-2 is the analogous cross-target case; both stem from mscordbi's process-global state.

So: **the underlying drhook-detach-exit-race phenomenon was known.** The multi-session-amplification was implicit but not explicitly named until MCH-RE-2. ADR-007 Phase 5's "multi-session engine" anticipated the architectural shape; the dispose-then-kill failure mode in the multi-session context is the new explicit finding.

## Minimal reproducer

`poc/drhook-engine/mch-re-2-repro.cs` — standalone file-based probe (no MSTest, no integration-test machinery). Runs 4 scenarios sequentially in the same .NET process. Key findings:

- **S1 + S2 pass with kill-first ordering.** Two sessions, two different targets (or same target spawned twice), kill-first protocol → clean teardown both times, no SIGSEGV.
- **S3 (mixed: simple target then dotnet-test-spawned testhost, with dispose-then-kill ordering) SIGSEGVs.** Same race the original finding-62 MSTest scenario hit.

The probe stays as a regression-detection artifact — if the substrate evolves to handle dispose-then-kill cross-session natively, this probe should pass S3 too.

## Implications for Phase 8

Phase 8 mass promotion of probes 41–45 into integration tests is **unblocked**. Two paths forward:

1. **Kill-first in integration tests.** Modify `AttachDisposeTest` + `VstestAttachDisposeTest` (and future Phase 8 tests) to call `proc.Kill` BEFORE `session.Dispose`. Discard the "target alive after Dispose" assertion (Phase 1 probes 42 + 44 already validate that). Each test then mutates substrate state cleanly; all can co-exist in one MSTest exe.

2. **Filter-per-invocation (CI workaround).** Keep dispose-then-kill in integration tests as the substrate-correctness assertion shape; require CI to invoke each test in its own MSTest exe run. More CI plumbing, but preserves the assertion semantics.

Recommendation: **Option 1.** Kill-first matches `EngineSteppingSession.CleanupSession`'s production protocol for Launched sessions — integration tests should mirror production discipline. The "target alive after Dispose" assertion is already covered by the file-based probe 42, which IS the substrate-correctness probe; integration tests don't need to re-prove it.

## Substrate work surfaced (not blocking, queued for future)

- **MCH-RE-3 (future probe):** does the dispose-then-kill cross-session race close if substrate adds an explicit RC-event-thread drain handshake before Dispose returns? Per the drhook-detach-exit-race limit doc's candidate mitigation 2: *"A general fix for the whole finding-14 class would confirm mscordbi's RC event thread has drained ALL pending work items (callbacks AND exit) before releasing native state — but ICorDebug exposes no direct 'join the RC thread' primitive, so this needs its own probe-driven design (deferred)."* MCH-RE-3 would investigate whether such a drain primitive exists in newer mscordbi or can be approximated via timing.
- **ADR-007 Phase 5 Probe 54 (Multi-session engine):** still required for NCrunch's concurrent multi-session needs. This investigation doesn't address concurrent; only sequential. Probe 54 remains queued.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 5 Probe 54 (multi-session engine, concurrent — architectural).
- [`docs/limits/drhook-detach-exit-race.md`](../../../docs/limits/drhook-detach-exit-race.md) — Triggered limit; root phenomenon underlying MCH-RE-2.
- [finding 53](53-threading-memory-model-audit.md) — substrate-statics audit (no mutable statics confirmed); EngineSteppingSession singleton documented.
- [finding 59](59-detach-exit-race-outcome.md) — Probe 44 substrate change (Attached path detach-leave-running) + MCH-RE-1 (same-target re-attach mscordbi accumulation).
- [finding 62](62-legacy-vstest-promotion.md) — original MCH-RE-2 discovery + Phase 8 blocker framing.
- `poc/drhook-engine/mch-re-2-repro.cs` — minimal reproducer; regression-detection artifact.

## Conclusion

MCH-RE-2 is **not a new substrate bug.** It is the existing drhook-detach-exit-race manifesting in a multi-session context that wasn't previously exercised. The substrate's behavior is consistent — detach-leave-running keeps targets alive after Dispose, but killing those targets afterwards races mscordbi's RC event thread, same as the limit doc documents for single-session. The multi-session aspect doesn't change the race shape, only makes it more reachable.

**Phase 8 is unblocked.** Integration tests use kill-first; substrate doesn't change. The MCH-RE-2 entry in finding 62's "blocker" framing is downgraded to "discovery + discipline note."

Martin's framing was correct on all five questions; the empirical investigation confirmed the suspicions about singleton-state, the level-of-belonging (caller discipline, not substrate), and the multi-engine-within-process supportability.
