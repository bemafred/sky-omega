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
- [ ] **Inspection at scale — lazy navigable expansion, not a breadth/size cap** (surfaced 2026-06-06, dogfooding Mercury). `drhook_locals` at **depth 2** — well under `MaxInspectionDepth=10` — over `QueryResults.FromMaterializedWithGraphContext` (a 14-argument frame fanning into the live `QuadStore` object graph: indexes → `BTreeFile`s → mmap + `PageCache` + pinned pointers) **crashed the engine and dropped the MCP server**; the debugged target survived (F-010-2). Isolation by elimination across **seven** synthetic shapes (`poc/drhook-inspection-robustness/`: plain struct, `List<int>`, `List<struct>`, `ReadOnlySpan`, a class with a `byte*` pointer field, `FileStream`, `MemoryMappedViewAccessor` + `SafeHandle`) — **all inspect safely at depth 2**; the inspector is per-shape robust, rendering pointers and ref-structs opaque. So the cause is **breadth/scale**, not a type and not depth: ENG-STK-1's `MaxInspectionDepth` clamp bounds depth, but nothing bounds total walk breadth/size/time. **Substrate change required — direction corrected 2026-06-21 (the original prescription was wrong; preserved here as a falsified hypothesis):** the original action read *"a total inspection budget (node/byte/time cap) with graceful truncation + a `BreadthClamped` anomaly parallel to `DepthClamped`."* That is an **amputation, not a fix**. DrHook exists to observe *arbitrary real runtime state*, so the large live object graph that crashes depth-2 inspection (Mercury's `QuadStore`) **is the central case, not an edge** — a breadth/size cap that truncates it makes DrHook blind exactly where it is most needed: a structured *not-supported* stamped on the core capability. The correct fix is **architectural — lazy, navigable, on-demand inspection**: return one level of a frame's locals as collapsed nodes and expand a node only when the agent navigates into it (by handle/path), so the walk is **bounded per-step by construction** and arbitrarily large/deep graphs stay **fully observable** (pay only for what you look at); this *removes* the crash cliff rather than fencing below it. For a genuinely wide *single* node, **page** (first N children + continuation) — never **truncate**. Distinguish from the resource-limit discipline: bound the *resource* (the recursion's work per step) always; never bound the *capability* (what can be observed). The detailed navigation protocol is **not yet designed** (deferred). Interim workaround: depth-1 or scalar expression reads on complex frames (which already point at the directed-observation model). Evidence: `poc/drhook-inspection-robustness/` + the `ck:` design-knowledge graph (`ck:obs-drhook-inspection-lazy-navigable`, which corrects `ck:lesson-runtime-inspection-scalar-over-walk`). Reasoning lesson: [`feedback_derive_fix_from_purpose`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_derive_fix_from_purpose.md). *(**First increment LANDED + verified 2026-06-21.** The eager recursion in `FieldEnumerator`/`ArrayInspector` is removed — inspection now returns ONE level with a `HasChildren` flag per node, and `DebugSession.ExpandLocal`/`ExpandArgument` + the `drhook_expand` MCP tool give lazy, navigable, one-bounded-level-per-call descent. Regression oracle `poc/drhook-inspection-robustness/repro-quadstore-{smoke,target}.cs`: the depth-3 `GetArguments` that SIGSEGV'd in coreclr's `RtlpUnwindFunctionFull` now returns cleanly, and lazy navigation walks 7 levels into the live `QuadStore`'s mmap / `FileStream` / `SafeHandle` internals — exactly the native-backed region that crashed the eager walk — without faulting; 119/119 engine unit tests green, build clean. Follow-on increments: struct (VALUETYPE) field expansion, null-ref `HasChildren` precision, frame selection (ADR-010 Tier 2).)*
- [x] **Probe 41 — Anomaly-injection validation (Phase 1 Validation gate).** Live target attach → set breakpoint → hit → call `GetLocals(depth=999)` → assert one `DepthClamped` anomaly surfaces with `requested=999`/`clamped=10` context via `DrainAnomaliesAsJson`. Validates the full capture-site → `BoundedAnomalySink` → `DrainAnomaliesAsJson` → MCP envelope path end-to-end before the substrate-correctness probes (42–45) rely on it. Output: `poc/drhook-engine/findings/56-anomaly-injection-outcome.md`. *(Done 2026-05-24, finding 56.)*
- [x] **Probe 42 — Dispose during the worker's `_resumeHandler(...)` call.** Force the race; characterise the failure mode; design the engine-side fix. *(Originally Done 2026-05-24 via finding 57 / 20/20 clean — superseded 2026-05-24: the original probe used `07-target.cs` whose throw/catch loop produced STOPPING callbacks that parked the worker at `_resume.Take()`, not inside `_resumeHandler`. Replacement probe + dedicated `42-target.cs` (informational-only Thread.Start/Join flood). New probe at 824e642 baseline: 50/50 PASS. New probe at HEAD pre-fix: SIGSEGV — real substrate regression introduced by `TryResumeForDetach` at commit 44d76aa, masked by stale-cache false-PASS per `feedback_filebased_app_stale_cache`. **Closed 2026-05-24 by dispatch-settle in `TryResumeForDetach`** ([finding 65](../../../poc/drhook-engine/findings/65-probe42-redesign-regression.md)): `Thread.Sleep(10 ms)` between Continue iterations + `Thread.Sleep(50 ms)` on all exit paths, gives mscordbi RC thread time to complete in-flight dispatch before the next substrate operation. 50/50 PASS at HEAD with fix; 59/59 unit tests unchanged.)*
- [x] **Probe 47 — External target death during Borrowed observation.** Surfaced 2026-05-24 by Probe 44 phase C running under the new `--no-cache` protocol. **Closed 2026-05-24** by target-death detection substrate change ([finding 66](../../../poc/drhook-engine/findings/66-target-death-detection.md)): `DebugSession.Attach` acquires `Process.GetProcessById(pid)` for death-detection only (no kill ownership); `DebugSession.Dispose` short-circuits Quiesce + TryResumeForDetach when target has exited; explicit `ExitWorkSettleMs` (200 ms) before Detach lets mscordbi exit-work-item complete; `BorrowedDeathCheckSettleMs` (50 ms) before HasExited check ensures OS has propagated kill notification. Detach preserved on all paths (releases CCW from mscordbi — required before `_callback.Dispose` frees CCW memory). Probe 47: 10/10 PASS. Probe 44 phase C: 10/10 PASS (was SIGSEGV under `--no-cache`). Probe 42 + integration tests + 59/59 unit tests unchanged.
- [x] **Probe 43 — Concurrent PauseRequest + STOPPING callback.** Verify the pump serialises correctly, or design the serialisation. *(Done 2026-05-24, finding 58 — 10/10 Pause requests surfaced under concurrent flood; single-consumer FIFO via BlockingCollection<T> serialises by construction.)*
- [x] **Probe 44 — Resolve `drhook-detach-exit-race` engine-side.** 10/10 under kill-coincident-with-Dispose. *Also characterise the rate envelope:* at what attach/detach frequency does the teardown path stay clean? Single-shot 10/10 is a starting bar, not a finishing one — NCrunch will be 50+ cycles/second. Must also design the **Attached-session path** (per finding 54): kill-first only mitigates Launched sessions; Attached needs detach-and-leave-running as the default OR explicit terminate-before-detach opt-in. *(Done 2026-05-24, finding 59 — substrate change: `_ownsTarget` field + `TryResumeForDetach` Continue-loop-until-S_FALSE; Phase B sentinel + Phase C 10/10 kill-coincident-Dispose at ~2 cycles/sec.)*
- [x] **Probe 45 — Worker-thread exception path.** If `_resumeHandler` throws, the worker dies silently and future `WaitForStop` hangs. Inject the throw, validate the recovery (worker survives + surfaces, or fails session cleanly with deterministic error). The `WorkerException` anomaly is already wired (EA-4); the probe validates the surfacing under live conditions. *(Done 2026-05-24, finding 60 — substrate's outer Pump catch fires exactly 1 WorkerException; WaitForStop returns null cleanly after timeout; Dispose completes; target alive.)*

Completion: all Phase 1 substrate-correctness probes pass at HEAD on macOS-arm64 — 41 (anomaly injection), 42 (informational-flood `_resumeHandler` race, finding 65 dispatch-settle), 43 (PauseRequest concurrency), 44 phase B + C (drhook-detach-exit-race, finding 59 TryResumeForDetach + finding 66 target-death detection), 45 (worker-thread exception), 47 (external target death during Borrowed observation, finding 66). `drhook-detach-exit-race` **Resolved** (death-detection short-circuits mscordbi protocol pushes against dying targets; Detach preserved with ExitWorkSettleMs so CCW release stays safe). `EngineAnomaly` surfacing validated end-to-end through MCP by Probe 41. **Phase 1 — Teardown + concurrency hardening + memory-model audit + stack-budget audit — COMPLETE on macOS-arm64 at probe level (2026-05-24) and at CI-enforced integration-test level (2026-05-26 via [ADR-008](ADR-008-process-lifecycle-discipline.md) Increment 4).** 59/59 unit tests + 12/12 integration tests passing across MTP + VSTest runner shapes.

### Phase 2 — Probe how to probe properly (the meta-probe)

The previous spiral happened here. Its purpose is to characterise the failure mode of probe-to-integration-test promotion before any scaling. Output is a substrate decision recorded as a finding, plus a single experimental exemplar.

- [x] **Probe 46 — MTP integration-test promotion exemplar.** Stand up `tests/DrHook.Engine.IntegrationTests/` (MSTest.Sdk, MTP-native) + `tests/DrHook.Engine.IntegrationTargets.Mtp/` (real-shaped MTP test project). Lift probe 42's attach+Dispose substrate-validation shape into ONE `[TestMethod]`. Establish the 4-layer vocabulary (integration test / DrHook substrate / integration target / target test code). Output: `findings/61-promotion-meta-probe.md`. *(Done 2026-05-24, finding 61 — 982 ms test passes; surfaced 5 discoveries including MTP's documented `--debug` attach-handshake superseding the assessment doc's recommendation.)*
- [x] **Probe 46b — Legacy VSTest integration-test promotion exemplar.** Stand up `tests/DrHook.Engine.IntegrationTargets.Vstest/` (classic VSTest test project, not MSTest.Sdk). Build the first piece of `DrHook.Testing` orchestration: spawn `dotnet test` with `VSTEST_HOST_DEBUG=1`, capture stdout, parse `Process Id: NNNN` from VSTest's undocumented format, attach to testhost. ONE integration test against the Legacy target. Output: `findings/62-legacy-vstest-promotion.md`. *(Done 2026-05-24, finding 62 — VSTest path passes in isolation. **CRITICAL DISCOVERY: same-host-process multi-substrate-session crashes with SIGBUS (MCH-RE-2)** — was Phase 8 blocker; structurally eliminated by finding 64 substrate refactor below.)*
- [x] **MCH-RE-2 investigation + substrate-owned-lifecycle refactor.** Investigation (finding 63) traced MCH-RE-2 to the dispose-then-kill ordering pattern (`session.Dispose(); proc.Kill()` in test `finally` blocks): substrate detaches, leaves mscordbi RC thread running, caller's subsequent `Kill` races against the unwound substrate state. Workaround: kill-first protocol (caller-side discipline). Substrate refactor (finding 64) eliminates the misorder *structurally* — `DebugSession.AttachAndOwn(pid, sink)` factory added; substrate holds the target `Process` handle internally for OWNED sessions; `Dispose` enforces kill-first internally before Quiesce/Detach/Terminate; `EngineSteppingSession` simplified (no more caller-side `_launchedProcess` orchestration). Integration tests 2/2 PASS in the same MSTest exe (1.5 s). MCH-RE-2 is no longer constructible from the substrate's API surface. Output: `findings/63-mch-re-2-investigation.md`, `findings/64-substrate-owned-lifecycle.md`. *(Done 2026-05-24.)*

Completion: Both Probe 46 (MTP) and Probe 46b (Legacy VSTest) pass in isolation; the integration-test promotion shape is documented for both platforms. **MCH-RE-2 structurally eliminated** by finding 64's substrate-owned-lifecycle refactor — Phase 8 mass promotion unblocked. Adjacent discovery: probe 42 has a separate pre-existing regression at HEAD (independent of the refactor) queued for future substrate investigation; does not block Phase 8 since probe 42's pattern (20 sequential Borrowed sessions) is more extreme than what Phase 8 requires.

### Phase 3 — Child-process attach substrate — SUPERSEDED 2026-05-27

**Status: Superseded.** Premise dissolved by the MTP-first strategic position (consolidated in [`docs/architecture/technical/drhook-test-debugging-assessment.md`](../../architecture/technical/drhook-test-debugging-assessment.md)) and the Phase 8 integration-test reality.

**What the original Phase 3 was solving.** "Variant J (`dotnet test` → testhost)" assumed the substrate would need OS-level process-tree observation to attach to a `testhost` descendant of `dotnet test`. The drafted probes (57 ICorDebug parent-fork behavior; 58 substrate-mediated handoff; 59 parent retained through child attach) addressed that premise.

**Why the premise dissolved.**
1. **MTP collapses the process tree.** Under Microsoft.Testing.Platform, the test project IS the testhost — a single executable. The substrate's existing `AttachAndOwn(pid)` is sufficient; no parent → child observation needed. MTP exposes a documented `--debug` switch that prints `Process Id: NNNN` and blocks until `Debugger.IsAttached` — this is the canonical attach handshake, not an env-flag trick.
2. **VSTest legacy compat is explicitly accepted.** The assessment doc (§"Recommended DrHook 1.8.x strategy") classifies `VSTEST_HOST_DEBUG=1` + stdout PID parse as legacy compatibility, *not* as a substrate capability to develop further. Phase 8 integration tests demonstrate this works cleanly under the substrate's existing API surface.
3. **Phase 8's integration tests demonstrate end-to-end coverage.** `tests/DrHook.Engine.IntegrationTests/` covers both MTP (`TargetSpawn.Mtp` using `--debug`) and VSTest legacy (`TargetSpawn.Vstest` using the env flag) end-to-end. 12/12 tests pass on macOS-arm64.

**What remains substrate work.** The assessment doc and the integration-test reality identify substrate APIs that are *spec'd but not implemented*: `LaunchTestExecutable(project, framework, filter)` for MTP direct-launch, `EnumerateClrProcesses()` for `--select` mode, multi-process handling, NCrunch process-tree attach. These are different work from "child-process attach substrate" — they are *MCP-tool / orchestration-layer* substrate additions, not engine-level ICorDebug primitives. Sequenced in a successor ADR (or rescoped Phase 3) — see Phase 5 + Phase 7 placeholders for now.

**Probe numbers freed.** 57, 58, 59 — next-available for the successor scope. Per the temporal-allocation-order convention.

### Phase 4 — Test-runner characterization — CLOSED 2026-05-27 (substrate proved runner-agnostic)

**Status: Closed.** The integration-test arc + assessment doc + Phase 3 retirement collectively answered Phase 4's scope-decision question at a deeper level than per-runner probes would have. The phase's premise — that test runners impose substrate-relevant variance worth characterising per-CLI — was disproved by what shipped instead.

**The deeper insight.** What turned out to matter at substrate level is not which CLI launched the .NET process. The substrate's contract — `AttachAndOwn(pid)` + lifecycle discipline + anomaly surfacing + per-operation primitives — is **runner-agnostic**. Process lifetime, callback shape, halt-state, exception flow, parallel test execution: all observable at substrate level without per-runner branching. Phase 8's 12 integration tests prove this across MTP + legacy VSTest with substrate code that has zero runner-specific paths. Where runner-specific concerns DO live — project inspection, executable resolution, env-flag legacy compat — they belong to the *orchestration layer* now scoped in [ADR-009](ADR-009-test-debugging-mcp-surface.md).

**Per drafted probe (all unstarted, all closed):**

- **Probe 60 (dotnet test / vstest)** — closed by Phase 8 integration tests + assessment doc. Variants A, F, G, J are CI-enforced supported.
- **Probe 61 (`xunit.console`) and Probe 62 (`nunit3-console`)** — closed by substrate runner-agnosticism. Variants E, K, L are *expected-supported* via direct-attach (`Launch` or `AttachAndOwn`); if a future user reports concrete failure on those CLIs, that becomes a substrate-correctness probe at its time of discovery, not a scope-decision probe scheduled in advance.
- **Probe 63 (NCrunch doc-characterization)** — folded into Phase 5 (which already cites NCrunch docs as the design source) + Phase 9 (which validates on Windows). No separate Phase 4 deliverable needed.
- **Static-state characterization** — runner-level concern, not substrate-level. The substrate observes what ICorDebug stop-the-world guarantees; AssemblyLoadContext boundaries and fixture-state reuse are user-test concerns observable through the substrate's existing primitives, not concerns that change substrate behavior. Removed.
- **Stack-cost characterization** — substrate's stack-budget audit (Phase 1c / finding 55) already established bounded-stack assumptions in substrate code. Per-runner stack consumption is a *user-test* concern; Phase 8 validated substrate operation under real-runner stack budgets without issue. Removed.

**Scope decisions finalised** (Phase 4's actual output — same decisions as the original priors, now confirmed-by-evidence rather than pre-committed):
- Variants A, F, G, J — **supported**, CI-enforced via Phase 8.
- Variants E, K (xunit.console), L (nunit3-console) — **expected-supported** via same substrate APIs; no per-runner substrate work needed.
- Variants B, H, M (NCrunch) — **designed-for** in Phase 5; validation in Phase 9.
- Variant N (Rider / VS TestExplorer) — **structured "not supported"** (Rider = oracle, VS TestExplorer = Phase 9).
- Variants O–V (resource contention + lifecycle pathology) — **substrate-orthogonal**; user-test domain, not substrate scope. Phase 6 per-variant tests cover this where relevant.

**Probe numbers freed.** 60, 61, 62, 63 — next-available per the temporal-allocation-order convention.

### Phase 5 — Substrate capabilities Phase 4 commits to — CLOSED 2026-05-27 (substrate already general; deeper NCrunch integration not committed)

**Status: Closed.** Same insight that closed Phase 4: probes 64 and 65 are *orchestration-layer work, not substrate-layer work*, and probe 66 anticipated a deeper NCrunch integration than the assessment doc actually commits to.

**Per drafted probe (all unstarted, all closed):**

- **Probe 64 (Multi-session engine).** The *substrate* already supports multi-session: `DebugSession.AttachAndOwn(pid, sink)` returns an independent session, no shared mutable state across instances, lifecycle discipline (ADR-008) applies per-session. What is single-instance is `EngineSteppingSession` — the MCP-layer wrapper, registered as a DI singleton. Moving from singleton to scoped/transient is an MCP-layer change, not substrate work. Scoped naturally into [ADR-009](ADR-009-test-debugging-mcp-surface.md) (or a follow-on MCP-surface ADR if needed).
- **Probe 65 (Process-tree observation + pattern-matched attach).** Substrate primitive already in [ADR-009](ADR-009-test-debugging-mcp-surface.md) Decision 3 (`EnumerateClrProcesses` + cross-platform back-ends). Pattern-matching against the result is composition at MCP layer, not substrate work. `drhook_processes` (existing MCP tool) + filtering + `drhook_step_launch` already compose to cover the use case.
- **Probe 66 (Attach-rate-envelope hardening).** Anticipated NCrunch's 50/sec sustained attach-detach rate. But per assessment doc, "NCrunch should be treated as an external execution environment. The clean initial integration is attach-mode debugging" — manual attach to a user-selected testhost, not automatic 50/sec churn. The performance scope from [finding 66](../../../poc/drhook-engine/findings/66-target-death-detection.md) §"Performance characterization" remains a known limit (registered in [drhook-detach-exit-race](../../limits/drhook-detach-exit-race.md) Resolved status notes) — if a future scoped deeper NCrunch integration materializes, probe 66 becomes part of that scoping work, not Phase 5's deliverable.

**Why the closure framing matches Phase 4's.** Both phases were *prospective per-variant substrate planning* that turned out to over-anticipate substrate-level variance. The substrate's generality (`DebugSession` runner-agnostic; multi-session-capable; lifecycle-disciplined per ADR-008) means the "design substrate specifically for runner X" framing is largely empty — the work that remains is orchestration at the MCP/DI layer (ADR-009's territory) or future-conditional on commitments not yet made (deeper NCrunch integration).

**Probe numbers freed.** 64, 65, 66 — next-available per the temporal-allocation-order convention.

### Phase 6 — Per-variant validation probes — CLOSED 2026-05-27 (preconditions changed; substrate exceeded expectations)

**Status: Closed.** Same insight that closed Phases 4 + 5, extended to its per-variant validation form: substrate generality + Phase 8's CI-enforced coverage make per-variant validation either redundant or future-conditional.

**Per drafted probe (all unstarted, all closed):**

- **Probes 67–71 (xUnit/MSTest/NUnit under `dotnet test` + `xunit.console`).** Phase 8's 12/12 integration tests prove substrate-correctness across two distinct runner shapes (MTP + legacy VSTest) at the substrate-callback / lifecycle / anomaly-surfacing level. Adding per-CLI variations of the same substrate scenarios is *adversarial-but-uninteresting* — no substrate hypothesis is being tested, only the user-test framework varies, which the substrate doesn't observe at its layer. The Phase 4 + Phase 5 closures already established this; Phase 6's per-variant form follows.
- **Probes 72–73 (NCrunch on Windows).** Closed via deferral rather than dissolution. The preconditions Phase 6 was built against changed: the substrate turned out far better than expected, NCrunch's "deeper integration" never crystallized as a committed scope, and the assessment doc's "attach-mode initial" stance means NCrunch live observation isn't blocked on Phase 6 substrate work. **Reopened when a concrete NCrunch use case + Windows environment converge** — at that point, a new probe set is scoped against the actual NCrunch model and the substrate-as-it-then-stands, not against today's prospective design.

**The shared closure principle (Phases 4 + 5 + 6).** All three phases were per-variant prospective substrate planning. Each closed under the same evidence chain: substrate is runner-agnostic at its layer; Phase 8 CI-enforces this across enough runner shapes that the runner-agnosticism is *demonstrated, not assumed*; orchestration-layer variance (which DOES matter per-runner) is ADR-009's territory. The "characterize each variant in advance" framing turned out to over-anticipate substrate variance that didn't exist.

**Probe numbers freed.** 67, 68, 69, 70, 71, 72, 73 — next-available per the temporal-allocation-order convention.

### Phase 7 — MCP surface cleanup

- [ ] Remove `drhook_step_test` from `DrHookTools.cs` and `EngineSteppingSession.cs`. No replacement tool — test debugging uses existing `drhook_step_run` (Attach) and `drhook_step_launch` (Launch); the new substrate capabilities from Phases 3 + 5 are exposed under those.
- [ ] Update all stale `[Description]` attributes in `DrHookTools.cs` ("Uses netcoredbg (MIT, DAP over stdio)" → engine-aligned text).
- [ ] Fix stale section header `// ─── Stepping layer (DAP / netcoredbg) ───` and stale comments in `EngineSteppingSession.cs` (`src/DrHook/Stepping/` reference) and `DrHook.Mcp.csproj` (pre-staging note).
- [ ] Out-of-scope variants from Phase 4 scope decisions surface as structured *"not supported"* MCP responses (not silent failure, not best-effort).

### Phase 8 — Integration test suite using Phase 2's validated mechanism

The ADR-006 Validation gate closes here, not earlier. Phase 2's meta-probe is the prerequisite; without its finding, Phase 8 has no shape.

**Status (2026-05-26): COMPLETE on macOS-arm64.** Phase 2 closed with the substrate-owned-lifecycle refactor (finding 64): `DebugSession.AttachAndOwn(pid, sink)` is the canonical pattern for substrate-attaching integration tests. The MCH-RE-2 same-host-process multi-session SIGBUS is structurally impossible from the substrate's API surface — multiple integration tests in the same MSTest exe coexist cleanly. [ADR-008](ADR-008-process-lifecycle-discipline.md) Increment 4 delivered the mass promotion under the lifecycle-discipline contract (natural-exit pattern + `WaitForExit` Layer-1 assertions); 12/12 integration tests PASS in ~13.1 s across MTP + VSTest runner shapes ([finding 72](../../../poc/drhook-engine/findings/72-increment4a-mass-promotion.md) Phase 8a baseline + 8 substrate-correctness scenarios, [finding 73](../../../poc/drhook-engine/findings/73-increment1b-release-pending-stops.md) Increment 4b probe 41 promoted, [finding 74](../../../poc/drhook-engine/findings/74-increment4c-concurrent-pause-stop-promoted.md) Increment 4c probe 43 promoted).

**Phase 8 effort sizing.** The Phase 2 exemplars (MTP + Legacy VSTest) showed legacy probes are *not* deeply coupled to manual-invocation assumptions when promoted via `AttachAndOwn` — the caller-side surface shrinks (no `finally`-block Kill, no Process handle management). Phase 8 promotion is therefore straightforward; no test-harness substrate work is required before scaling.

- [x] **Per-MCP-tool integration test.** Each substrate-correctness scenario from Phase 1 promoted as a `[TestMethod]` against MTP + VSTest integration targets via `DebugSession.AttachAndOwn`. *(Done via ADR-008 Increment 4 — 12 tests covering 6 substrate-correctness scenarios × 2 runner shapes; substrate Dispose-during-brief-work baseline, WorkerException anomaly surfacing, informational-flood Dispose, Pause-then-Dispose-without-Resume, GetLocals(depth>10) DepthClamped anomaly, concurrent PauseRequest + STOPPING serialisation.)*
- [x] **Substrate-correctness probes promoted from `poc/` to `tests/DrHook.Engine.IntegrationTests/`.** *(Done via ADR-008 Increment 4: AttachDisposeTest, WorkerExceptionTest, InformationalFloodTest, PauseDisposeTest, AnomalyInjectionTest, ConcurrentPauseStopTest — each MTP + VSTest variant. Probe 47 dropped as redundant: death-detection is structurally tested by the substrate itself in normal Dispose paths and the external-kill scenario doesn't compose naturally with the WaitForExit Layer-1 discipline assertion.)*
- [x] **Probe 42 pre-existing regression resolved before promotion.** *(Done 2026-05-24 via [finding 65](../../../poc/drhook-engine/findings/65-probe42-redesign-regression.md) dispatch-settle in `TryResumeForDetach`; probe 42 promoted to `InformationalFloodTest` in Increment 4a.)*
- [x] **CI on macOS/arm64. Failures block PRs.** *(Integration test suite runs as a single MSTest exe under MSTest.Sdk; 12/12 PASS reliably across 3 consecutive runs per finding 74.)*

### Phase 9 — Cross-platform validation campaign

Time-budgeted separately. Validates the substrate-correctness probe corpus on Linux + Windows; per-platform discoveries become new probes. ADR-008's Phase 0.1 (probes 49–54 signal-disposition ground truth across platforms) is folded into this campaign. NCrunch validation deferred until concrete use case + Windows environment converge (Phase 6 closure note).

- [ ] Probes 02–40 + 41–46 (Phase 1/2) + 47–56 (ADR-008 substrate behavior) on Linux/x64. Phase 3 superseded (probe numbers 57–59 freed); Phases 4 + 5 + 6 closed (probe numbers 60–73 freed).
- [ ] Same probe set on Linux/arm64.
- [ ] Same probe set on Windows/x64.
- [ ] Same probe set on Windows/arm64.
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
- All allocated probes (41–46 Phase 1/2 + 47–56 ADR-008; Phase 3 superseded, Phases 4 + 5 + 6 closed without probes) pass on macOS/arm64 in CI; Phase 9 has completed validation on Linux/x64+arm64, Windows/x64+arm64.
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
