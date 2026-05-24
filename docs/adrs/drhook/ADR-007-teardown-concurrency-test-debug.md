# ADR-007: Teardown and concurrency hardening; substrate-aligned test-runner debugging; integration-test mechanism characterization

**Status:** Accepted — 2026-05-23

## Context

DrHook.Engine reached substrate-independence at 1.8.2 (Phase 3 close, [ADR-006](ADR-006-drhook-engine.md)) — `src/DrHook/` retired, netcoredbg gone, BCL-only ICorDebug interop with per-RID `libdbgshim` bundling. The 40 PoC probes (02–40) execute end-to-end on macOS/arm64: 39 PASS plus 1 documented PARTIAL (probe 05, resolved by probe 06). 47 unit tests pass.

What `DrHook.Engine` is **not** yet ready for: production use by AI coding agents (Claude Code, others) against real-world .NET workloads — specifically, test-runner-hosted debuggees, which are the dominant scenario for developer-facing AI agents. The ADR-006 Validation block explicitly names this gate: *"All DrHook MCP tools pass integration tests against the engine."* That gate is open, and a previous session attempted to close it via integration-test promotion of the existing probes — that attempt spiralled and left ambiguous artifacts (the `.local-dbgshim/` scavenge, `step_test` "polish item" stub, false provenance in [finding 11](../../../poc/drhook-engine/findings/11-dbgshim-baseline.md), stale tool descriptions) that today's session has corrected.

The diagnosis: the integration-test mechanism was itself an unsolved problem, and the previous attempt compounded it with substrate hardening + child-process attach + test-runner debugging + packaging work simultaneously. None of those was independently completable; their entanglement produced trial-and-error work strewn across the substrate. Per the lesson recorded in [`feedback_dont_compound_unknowns.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_dont_compound_unknowns.md), each phase of substrate work isolates exactly one unsolved thing. This ADR sequences the remaining production-suitability work under that discipline.

### Constraints (Martin, 2026-05-23 conversation)

1. **DrHook.Engine must handle thread concurrency properly** — not approximate, not defer.
2. **Attach and Launch both work; test debugging works without env-flag tricks.** The previous DrHook's `VSTEST_HOST_DEBUG=1 + testhost-PID-discovery` trick was framework-specific, didn't generalise, and is exactly the workaround shape this substrate refuses.
3. **No deferral; no fix-later.** Each engine change ships with its probe + finding; surprises within a phase become new probes before the phase resumes.
4. **No flags** for behaviour modes. Variants get distinct concrete classes or distinct substrate capabilities. (Per [`feedback_no_behavior_flags.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md).)
5. **`drhook_step_test` is removed**, not ported. It was introduced under trial-and-error / ungoverned conditions.
6. **Cross-platform is design discipline now, validation campaign later.** Probes are portable by construction; per-platform validation is its own time-budgeted phase (Phase 9).
7. **Substrate first, not speed. Quality trumps everything.** Vibe coding has no place in operating-system-level code, which is what a debugger substrate is.

### Stress dimensions test runners impose on the substrate

Test runners are not "an MCP-tool target." They are a substrate stress test along four mostly-orthogonal dimensions, each with multiple variants. Each variant × dimension combination requires the substrate to handle it correctly or refuse with a structured "not supported" — no silent failure, no half-coverage.

| Dimension | Variants |
|---|---|
| **Process model** | (A) single long-lived testhost (`dotnet test` default, vstest); (B) per-test process (NCrunch on Windows); (C) per-assembly process; (D) per-collection process (xUnit `[Collection]`); (E) in-process (xunit.console without separation) |
| **Execution parallelism** | (F) sequential; (G) intra-process parallel (xUnit default — N test threads in one process); (H) inter-process parallel (NCrunch with degree>1 — multiple testhost processes simultaneously); (I) hybrid |
| **Runner CLI** | (J) `dotnet test`/vstest; (K) `xunit.console`; (L) `nunit3-console`; (M) NCrunch's runner; (N) Rider / VS TestExplorer (proprietary) |
| **Resource contention + lifecycle pathology** | (O) shared DB/file/port; (P) in-memory fixture state; (Q) CI vs local timing variance; (R) test that hangs; (S) test that crashes; (T) test that spawns subprocesses; (U) test that calls `Environment.Exit`; (V) test that loops infinitely until cancelled |

Two further substrate-internal dimensions affect what the substrate observes regardless of external shape:

- **Memory model + static state:** AssemblyLoadContext-rooted statics may map differently across runners; non-volatile shared state surfaces under parallel runners that single-thread testhosts never expose. The substrate either reads what ICorDebug stop-the-world guarantees, or has an explicit contract for when that guarantee doesn't hold.
- **Stack budget + platform defaults:** NCrunch instrumentation is heavy on the test thread's stack; Windows default thread stack (1 MB) is much smaller than Linux (8 MB) or macOS main (8 MB); macOS secondary threads are 512 KB by default; .NET threadpool varies. The substrate's own frame walking, type chain walking, member resolution, and func-eval must operate under bounded-stack assumptions, not the developer's local 8 MB default.

### Scope decisions (Martin, 2026-05-23)

- **NCrunch** — *designed-for now, validated in Phase 9.* Substrate (multi-session engine, process-tree observation) built to accommodate; per-test-process probe lands in Phase 9.
- **`dotnet test` parallel testhost** — *in scope now.*
- **`xunit.console`, `nunit3-console`** — *in scope now.*
- **Rider's own debugger** — *not a target.* Rider is the **reference oracle** for substrate-correctness on macOS during Phases 3, 4, 6 (per [`reference_rider_as_oracle.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/reference_rider_as_oracle.md)).
- **VS TestExplorer** — *deferred to Phase 9* (Windows-only, proprietary, requires reverse-engineered process model).
- **Cross-platform** — design discipline applies to all phases; per-platform validation is Phase 9 (separately time-budgeted).

## Decision

Sequence the work as a strict EEE chain of nine phases. Each phase has explicit completion criteria; no phase begins until the prior's findings are recorded. Probes 41+ continue the existing PoC numbering convention (file-based `NN-name-smoke.cs` + optional `NN-name-target.cs`; the proven legacy shape — *no new test infrastructure during substrate work*).

### Phase 1 — Teardown + concurrency hardening + memory-model audit + stack-budget audit

The substrate prerequisite for everything else. A substrate with intermittent teardown bugs makes every subsequent test ambiguous (*"did the test fail or did teardown segfault?"*). Mercury serves as the stack-discipline reference (Mercury domain: parsing; DrHook domain: COM interop — discipline transfers, specific `Span<T>` patterns may not, lifetime analysis at native↔managed boundaries is materially harder; see [`feedback_no_helpers.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_helpers.md) etc.).

Sub-phases:

- [x] **1a. Threading + memory-model invariant document.** Field-by-field audit of `DrHook.Engine` shared state — `BlockingCollection<T>` hand-offs (CallbackPump), `Interlocked` usage (ManagedCallbackHost refcount), `lock` (BoundedLogSink), `ManualResetEventSlim` (DbgShim). For every field written by one thread and read by another, record the memory-model contract and the primitive that enforces it. Identify uncovered race windows. Output: `poc/drhook-engine/findings/53-threading-memory-model-audit.md`. *(Done 2026-05-23.)*
- [x] **1b. Teardown audit.** Walk `drhook-detach-exit-race` end-to-end; enumerate teardown paths (Dispose during running, during stopped, during eval, during pause, during child-process exit, during attach-mid-flight). Build a reproducer matrix. Output: `findings/54-teardown-audit.md`. *(Done 2026-05-23.)*
- [x] **1c. Stack-budget audit.** Mercury cross-reference: catalog the stack-discipline patterns Mercury uses; identify what transfers; identify DrHook-specific rules for COM-interop / callback-marshalling / stopped-state-memory-reads. Field-by-field review of any existing `Span<T>`/`stackalloc` in DrHook.Engine for lifetime correctness. Output: `findings/55-stack-budget-audit.md`. *(Done 2026-05-23.)*
- [x] **Anomaly-capture infrastructure.** `EngineAnomaly` typed record (thread of detection, operation, observed-vs-expected, kind-specific context); `BoundedAnomalySink` parallel to `BoundedLogSink`; `IDebugEventSink.OnAnomaly` default no-op; 9 engine-side capture sites wired (CallbackPump 4, DebugSession 5); MCP tool `drhook_drain_anomalies` surfaces the structured envelope. The substrate's loop-closing mechanism for unknown unknowns (per [`feedback_surprises_are_substrate_grade.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_surprises_are_substrate_grade.md)). *(Done 2026-05-24; 2 sites — `ManagedCallbackHost.HostOf`-null and `DbgShim.StartupCallbackThunk`-null — deferred pending static-fallback sink substrate.)*
- [x] **Engine threads get explicit stack-size declarations.** `new Thread(Pump, maxStackSize)` with documented budgets sized for Windows 1 MB and macOS 512 KB defaults, not the developer machine's headroom. *(Done 2026-05-24 as ENG-STK-2.)*
- [x] **Engineering fixes (ENG-CP-1, ENG-DS-1, ENG-DBG-D).** Atomic `Interlocked.Exchange` idempotence gates for `CallbackPump.Dispose` and `DebugSession.Dispose`/`Detach`; zero `_lib` after `NativeLibrary.Free` in `DbgShim.Dispose` to prevent double-`dlclose` on macOS. *(Done 2026-05-24.)*
- [x] **MaxInspectionDepth = 10 (ENG-STK-1).** `DebugSession.MaxInspectionDepth` constant + clamp in `GetLocals`/`GetArguments` + `DepthClamped` anomaly emission. Mercury-aligned with `SparqlParser.DefaultMaxDepth`. *(Done 2026-05-24.)*
- [x] **Probe 41 — Anomaly-injection validation (Phase 1 Validation gate).** Live target attach → set breakpoint → hit → call `GetLocals(depth=999)` → assert one `DepthClamped` anomaly surfaces with `requested=999`/`clamped=10` context via `DrainAnomaliesAsJson`. Validates the full capture-site → `BoundedAnomalySink` → `DrainAnomaliesAsJson` → MCP envelope path end-to-end before the substrate-correctness probes (42–45) rely on it. Output: `poc/drhook-engine/findings/56-anomaly-injection-outcome.md`. *(Done 2026-05-24, finding 56.)*
- [x] **Probe 42 — Dispose during the worker's `_resumeHandler(...)` call.** Force the race; characterise the failure mode; design the engine-side fix. *(Done 2026-05-24, finding 57 — 20/20 clean Dispose under continuous flood; no engine-side fix required, substrate's prior Quiesce + Interlocked gates + EngineAnomaly path hold.)*
- [x] **Probe 43 — Concurrent PauseRequest + STOPPING callback.** Verify the pump serialises correctly, or design the serialisation. *(Done 2026-05-24, finding 58 — 10/10 Pause requests surfaced under concurrent flood; single-consumer FIFO via BlockingCollection<T> serialises by construction.)*
- [x] **Probe 44 — Resolve `drhook-detach-exit-race` engine-side.** 10/10 under kill-coincident-with-Dispose. *Also characterise the rate envelope:* at what attach/detach frequency does the teardown path stay clean? Single-shot 10/10 is a starting bar, not a finishing one — NCrunch will be 50+ cycles/second. Must also design the **Attached-session path** (per finding 54): kill-first only mitigates Launched sessions; Attached needs detach-and-leave-running as the default OR explicit terminate-before-detach opt-in. *(Done 2026-05-24, finding 59 — substrate change: `_ownsTarget` field + `TryResumeForDetach` Continue-loop-until-S_FALSE; Phase B sentinel + Phase C 10/10 kill-coincident-Dispose at ~2 cycles/sec.)*
- [x] **Probe 45 — Worker-thread exception path.** If `_resumeHandler` throws, the worker dies silently and future `WaitForStop` hangs. Inject the throw, validate the recovery (worker survives + surfaces, or fails session cleanly with deterministic error). The `WorkerException` anomaly is already wired (EA-4); the probe validates the surfacing under live conditions. *(Done 2026-05-24, finding 60 — substrate's outer Pump catch fires exactly 1 WorkerException; WaitForStop returns null cleanly after timeout; Dispose completes; target alive.)*

Completion: all probes (41–45) pass; `drhook-detach-exit-race` **Resolved** (Attached-path substrate change + Launched-path kill-first via EngineSteppingSession); `EngineAnomaly` surfacing validated end-to-end through MCP by Probe 41. **Phase 1 — Teardown + concurrency hardening + memory-model audit + stack-budget audit — is COMPLETE on macOS-arm64.**

### Phase 2 — Probe how to probe properly (the meta-probe)

The previous spiral happened here. Its purpose is to characterise the failure mode of probe-to-integration-test promotion before any scaling. Output is a substrate decision recorded as a finding, plus a single experimental exemplar.

- [ ] **Probe 46** — take an already-passing legacy probe (e.g., probe 39 fields) and characterise what it takes to promote it into `tests/DrHook.Engine.IntegrationTests/` cleanly. Identify every implicit assumption legacy probes make about manual invocation (fixture file output, sequential single-target spawn, `Environment.CurrentDirectory` assumptions, console redirection, etc.).
- [ ] Output: `findings/61-promotion-meta-probe.md` recording: *"DrHook integration tests are shaped like X, prove Y, explicitly do not try to prove Z."* This is the substrate decision Phase 8 fills in against; without this finding, Phase 8 doesn't begin. (Findings 56–60 are taken by Probe 41 / Probe 42 / Probe 43 / Probe 44 / Probe 45 outcomes — see Phase 1.)

Completion: one finding + one exemplar test. **No mass promotion in this phase** — that's Phase 8.

### Phase 3 — Child-process attach substrate

The substrate capability test-runner debugging needs for variant J (`dotnet test` → testhost). One substrate capability among several; explicitly not *the* test-runner answer (NCrunch needs a different capability — Phase 5).

- [ ] **Probe 47.** ICorDebug semantics on child process spawn. Does `dbgshim` enumerate children of an already-debugged parent? Does CreateProcess callback fire on child spawn?
- [ ] **Probe 48.** Launch parent, observe child spawn, detach parent, Attach child. Validate the handoff.
- [ ] **Probe 49.** Same as 48 but parent kept alive (parent produces diagnostic output we want to keep observing).

Rider as oracle: for each probe, the same scenario in Rider gives the truth-reference. Discrepancies are findings.

### Phase 4 — Test-runner characterization

For each in-scope variant from the stress-dimensions table, characterise what substrate capability it requires. Output is **scope-decision finalisation** at the variant level, not probes-and-fixes.

- [ ] **Probe 50** — `dotnet test`/vstest process tree (variants A, F, G, J). What's the testhost spawn timing? What env does the runner set? Where does user-test code load? What does ICorDebug see when we attach to testhost?
- [ ] **Probe 51** — `xunit.console` (variant E, K). Single-process; what's different from variant A?
- [ ] **Probe 52** — `nunit3-console` (variant L).
- [ ] **Probe 53** — NCrunch process model characterisation (variants B, H, M) — *Windows; characterisation only, validation in Phase 9.* What's the process spawn rate? What's the test thread's stack like under NCrunch instrumentation? What env / pipe protocol does NCrunch use to coordinate? Sourced from NCrunch docs + observation, not validation.
- [ ] **Static-state characterisation per runner:** what view of static state does each runner give the substrate? AssemblyLoadContext boundaries, fixture reuse, parallel-test reuse of static state.
- [ ] **Stack-cost characterisation per runner:** baseline measurement of how much of the test thread's stack each runner consumes before the user's test runs.

**Expected scope-decision priors** (to be confirmed or revised by the probes, not pre-committed):
- Variants A, F, G, J (dotnet test, single/parallel testhost) — *expected supported*.
- Variants E, K (xunit.console) and L (nunit3-console) — *expected supported* once Phase 3 child-process attach lands; variant E (in-process) may surface as structured "not supported" if AssemblyLoadContext boundaries make stable observation unreliable.
- Variants B, H, M (NCrunch) — *designed-for* (Phase 5 substrate); validation deferred to Phase 9.
- Variant N (Rider / VS TestExplorer) — *structured "not supported"* (Rider is the oracle, not a target; VS TestExplorer deferred to Phase 9).
- Variants O–V (resource contention + lifecycle pathology) — *substrate-orthogonal*: characterised in Phase 6 per-variant tests, not Phase 4. Pathologies like U (`Environment.Exit`) and V (infinite loop) are debugger scenarios the substrate must observe-and-report, not runner scenarios.

Each probe validated against Rider on macOS where applicable. Output: one `findings/NN-*-outcome.md` per probe (numbered sequentially as the probes execute); substrate-scope decision finalised per variant.

### Phase 5 — Substrate capabilities Phase 4 commits to

Concrete probes for substrate capabilities Phase 4's scope decisions require. Each substrate capability is a substrate decision with its own probes, not a one-line feature.

**Accepted risk — NCrunch substrate designed pre-validation.** Probes 54 and 55 design substrate capabilities (multi-session engine, process-tree observation + pattern-matched attach) against NCrunch's documented model + observation, not against a running NCrunch instance — that validation is Phase 9. This is an explicit asymmetry against the doc's general "validate before commit" discipline: the alternative (defer Phase 5 until a Windows + NCrunch environment is available) would block the macOS substrate work for an unbounded period. The bet is that NCrunch's process-spawn-and-coordinate model is well enough characterised in its docs to design against. If Phase 9 reveals the real model differs materially, that's substrate rework, accepted as the cost of not blocking. Pre-Phase-9 de-risking via a one-off Windows session is *encouraged but not gated* — if a Windows machine becomes available during Phases 5–8, run probe 53's characterisation pass and feed findings back.

- [ ] **Probe 54 — Multi-session engine.** Required by NCrunch (variant H, inter-process parallel). Today's `EngineSteppingSession` is a DI singleton; multi-session is architectural. Probe characterises: concurrent session lifetimes, resource sharing, fairness, isolation guarantees.
- [ ] **Probe 55 — Process-tree observation + pattern-matched attach.** Required by NCrunch (variants B, M) — processes are spawned by NCrunch's coordinator, not from a Launch'd parent we control. We need: observe the system process tree, match new .NET processes against a pattern, attach.
- [ ] **Probe 56 — Attach-rate-envelope hardening.** If Phase 1 probe 44 characterised single-shot 10/10, this probe characterises 50/sec sustained under simulated NCrunch load.

### Phase 6 — Per-variant validation probes

One probe per in-scope variant from Phase 4. Each composes Phase 3 + Phase 5 substrate capabilities; none invent new substrate.

- [ ] **Probe 57.** xUnit under `dotnet test`, sequential — child-process attach (Phase 3) + breakpoint hit + locals inspection + clean detach.
- [ ] **Probe 58.** xUnit under `dotnet test`, parallel (intra-process) — same plus concurrent test threads.
- [ ] **Probe 59.** MSTest under `dotnet test`.
- [ ] **Probe 60.** NUnit under `dotnet test`.
- [ ] **Probe 61.** xUnit under `xunit.console` (the non-vstest path — proves we're not accidentally vstest-coupled).
- [ ] **Probe 62** (Phase 9 — Windows). xUnit under NCrunch, single test — multi-session (Phase 5 probe 54) + process-tree observation (probe 55).
- [ ] **Probe 63** (Phase 9 — Windows). xUnit under NCrunch, degree>1 — adds attach-rate envelope (probe 56).

Rider as oracle for probes 57–61.

### Phase 7 — MCP surface cleanup

- [ ] Remove `drhook_step_test` from `DrHookTools.cs` and `EngineSteppingSession.cs`. No replacement tool — test debugging uses existing `drhook_step_run` (Attach) and `drhook_step_launch` (Launch); the new substrate capabilities from Phases 3 + 5 are exposed under those.
- [ ] Update all stale `[Description]` attributes in `DrHookTools.cs` ("Uses netcoredbg (MIT, DAP over stdio)" → engine-aligned text).
- [ ] Fix stale section header `// ─── Stepping layer (DAP / netcoredbg) ───` and stale comments in `EngineSteppingSession.cs` (`src/DrHook/Stepping/` reference) and `DrHook.Mcp.csproj` (pre-staging note).
- [ ] Out-of-scope variants from Phase 4 scope decisions surface as structured *"not supported"* MCP responses (not silent failure, not best-effort).

### Phase 8 — Integration test suite using Phase 2's validated mechanism

The ADR-006 Validation gate closes here, not earlier. Phase 2's meta-probe is the prerequisite; without its finding, Phase 8 has no shape.

**Phase 8 effort sizing depends on Phase 2's finding.** Phase 2 characterises one exemplar promotion; if the exemplar reveals legacy probes are deeply coupled to manual-invocation assumptions (fixture file output, `Environment.CurrentDirectory`, console-output inspection, sequential single-target spawn), then Phase 8 expands to include test-harness substrate (in-process target hosting, structured output capture, parallel-safe fixture isolation) before promotion can scale. The Phase 2 finding records which case applies and what the substrate work entails; Phase 8's checklist may grow accordingly.

- [ ] Per-MCP-tool integration test. Each `[McpServerTool]` method gets a test driven end-to-end against an in-process target (the probe targets in `poc/drhook-engine/` are the model — promote them via Phase 2's mechanism).
- [ ] Probes 57–61 promoted from `poc/` to `tests/DrHook.Engine.IntegrationTests/`.
- [ ] CI on macOS/arm64. Failures block PRs.

### Phase 9 — Cross-platform validation campaign

Time-budgeted separately. Validates all prior phases on Linux + Windows; per-platform discoveries become new probes; NCrunch variants from Phase 6 (probes 62, 63) execute here for the first time.

- [ ] Probes 02–40 + 41–61 on Linux/x64.
- [ ] Probes 02–40 + 41–61 on Linux/arm64.
- [ ] Probes 02–40 + 41–61 + 62 + 63 on Windows/x64.
- [ ] Probes 02–40 + 41–61 + 62 + 63 on Windows/arm64.
- [ ] Per-platform findings documented; any new probes from discoveries integrated into the substrate.
- [ ] Phase 8 CI extended to all four platforms.

## Validation

This ADR moves **Proposed → Accepted** when the approach has been reviewed and amendments incorporated. Per CLAUDE.md's EEE-aligned status semantics, Accepted means *Epistemics complete — decision validated, approach approved, ready for engineering*. Phase 1's three audits (1a/1b/1c) are the **first engineering milestone within the Accepted phase**, not a status transition. Phase 4's scope reconfirmation against the audit findings is the second.

It moves **Accepted → Completed** when:
- Every checkbox above is `[x]`.
- `drhook_step_test` is gone (Phase 7).
- The ADR-006 Validation gate — *"All DrHook MCP tools pass integration tests against the engine"* — is closed (Phase 8).
- `drhook-detach-exit-race` is **Resolved** (not Mitigated) per Phase 1 probe 44.
- No environment-flag trick anywhere in the test-debug path.
- Probes 41–63 pass on macOS/arm64 in CI; Phase 9 has completed validation on Linux/x64+arm64, Windows/x64+arm64.
- The `EngineAnomaly` infrastructure exists, its capture mechanism is validated by a designed probe (intentional anomaly injection exercising the surfacing path), and the surfacing reaches the log sink + MCP response as designed. Organic in-the-wild surfacing is *expected* during Phase 9 and any such surprise is promoted to a probe + finding when it occurs — but the absence of an organic surprise is not a completion blocker.

## Discipline notes

- **No deferral within a phase.** A revealed unknown unknown becomes a probe; the phase doesn't proceed until the probe records a finding. Per [`feedback_dont_compound_unknowns.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_dont_compound_unknowns.md).
- **No fix-later.** Each engine change ships with its proof: probe + finding + commit linking both.
- **No flags.** No environment variables to gate behaviour modes. Per [`feedback_no_behavior_flags.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md).
- **No workarounds.** `VSTEST_HOST_DEBUG` was a workaround; defensive `volatile` would be a workaround; "wrap in try/catch and log Exception" would be a workaround. The substrate either does the thing correctly with an explicit contract, or refuses with a structured "not supported."
- **No trial-and-error.** Substrate-grade work is built right the first time. Per [`feedback_no_vibe_coding.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_vibe_coding.md). A debugger is operating-system-level code — vibe coding has no place there.
- **Substrate first, not speed.** If a probe reveals the substrate needs a deeper rework, that's the work.
- **Rider as oracle** for substrate-correctness on macOS during Phases 3, 4, 6. Disagreements between DrHook.Engine and Rider on the same scenario *are* the finding.
- **Surprises will happen.** The substrate includes `EngineAnomaly` capture as first-class infrastructure, not as error-handling glue. Per [`feedback_surprises_are_substrate_grade.md`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_surprises_are_substrate_grade.md).

## Consequences

- **DrHook becomes production-suitable** for AI coding agents working against real .NET test-runner workloads, on macOS/arm64 in CI and on Linux + Windows after Phase 9's campaign.
- **The `EngineAnomaly` infrastructure** becomes a substrate template for other Sky Omega components (Mercury, Minerva, future Lucy/James/Mira/Sky) — surprises-as-substrate-grade applied beyond DrHook.
- **Cross-platform + cross-dev-environment substrate is the strategic moat.** *Those who manage it own the future* (Martin, 2026-05-23). The discipline isn't an aspiration tax; it's why other AI coding agents will adopt this substrate and not the competing trial-and-error alternatives.
- **The probe corpus (02–70+)** becomes the substrate's epistemic record — file-based, single epistemic acts, finding-doc-per-outcome. Promoted into the integration-test suite only after Phase 2's meta-probe characterises the promotion mechanism.

## References

- [ADR-006](ADR-006-drhook-engine.md) — DrHook.Engine substrate, Phase 3 close at 1.8.2. This ADR closes its outstanding Validation gate.
- [`docs/limits/drhook-detach-exit-race.md`](../../limits/drhook-detach-exit-race.md) — Triggered limit resolved by Phase 1 probe 44.
- [`docs/limits/drhook-testability.md`](../../limits/drhook-testability.md) — testability-as-designed-in constraint Phase 2 + 8 honour.
- [`poc/drhook-engine/findings/11-dbgshim-baseline.md`](../../../poc/drhook-engine/findings/11-dbgshim-baseline.md) — corrected 2026-05-23; the false-provenance incident referenced in Context.
- [`poc/drhook-engine/findings/50-dbgshim-bundling.md`](../../../poc/drhook-engine/findings/50-dbgshim-bundling.md) — corrected 2026-05-23.
- `src/DrHook.Engine/CallbackPump.cs` — Phase 1a audit target.
- `src/DrHook.Mcp/EngineSteppingSession.cs:159` — current `step_test` stub message (removed in Phase 7).
- `src/DrHook.Mcp/DrHookTools.cs:77–92` — `drhook_step_test` to be removed in Phase 7.
- Mercury session graph: `https://sky-omega.dev/sessions/2026-05-23-drhook-adr007/` — observations, diagnoses, scope decisions, and the four-stress-dimensions catalogue recorded as the planning unfolded.
- Memory: [`feedback_dont_compound_unknowns`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_dont_compound_unknowns.md), [`feedback_surprises_are_substrate_grade`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_surprises_are_substrate_grade.md), [`reference_rider_as_oracle`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/reference_rider_as_oracle.md), [`feedback_no_scavenging_native_binaries`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_scavenging_native_binaries.md), [`feedback_no_vibe_coding`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_vibe_coding.md), [`feedback_no_behavior_flags`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md), [`feedback_eee_discipline`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_eee_discipline.md).
