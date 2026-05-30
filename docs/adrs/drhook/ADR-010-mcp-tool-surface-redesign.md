# ADR-010: DrHook MCP tool surface — semantic naming and established-debugger alignment

**Status:** Proposed — 2026-05-27

**Supersedes:** [ADR-009 (DrHook)](ADR-009-test-debugging-mcp-surface.md) — that ADR's test-debugging-specific scope is subsumed by the broader surface redesign captured here. Test-debugging is one input shape to a unified `drhook_launch` tool; it does not require its own MCP-tool family.

## Epistemic note — read first

This ADR is structured for review, not for ship. The author (Claude) has limited training depth in .NET debugger internals — specifically ICorDebug primitive semantics, Portable PDB internals, IDE-specific debugger UX patterns, and the precise capability surface that VS/VS Code/Rider expose to the user vs. through the DAP. Earlier drafts in the conversation that produced this ADR contained hallucinated decisions that required user correction — notably the per-runner MCP tool decomposition proposed in (now-superseded) ADR-009 and assumptions about substrate capabilities not grounded in code reads. The two prior documents in [`docs/architecture/technical/`](../../architecture/technical/) (`drhook-test-debugging.md`, `drhook-test-debugging-assessment.md`) were not the source of those hallucinations — they are the foundational design rationale that previously corrected such hallucinations and remain the reference for substrate-strategy decisions. This ADR explicitly avoids the failure mode by:

1. **Grounding every factual claim about the current state in a code read** (cited inline with `file:line`).
2. **Grounding every claim about IDE capabilities in cited research** (URLs in References).
3. **Marking proposed-but-uncertain claims with `[?]`** so reviewer attention is correctly focused.
4. **Surfacing an explicit Open Questions section** — these MUST be answered before Proposed → Accepted.

The redesign positions in §Decision are *proposals subject to evidence*, not asserted truths.

## Context

### Current MCP tool surface (factual — 2026-05-27)

`src/DrHook.Mcp/DrHookTools.cs` exposes **20 MCP tools** (counted from `[McpServerTool(...)]` attributes). Organized by category:

**Observation (no session required):**
- `drhook_processes` — list running .NET processes (cited: `DrHookTools.cs:19`).
- `drhook_snapshot` — passive EventPipe trace of a target PID; requires `hypothesis` parameter (cited: `DrHookTools.cs:27`).

**Session lifecycle:**
- `drhook_step_run` — launch a .NET executable under debugger control (cited: `DrHookTools.cs:45`). Substrate path: `DebugSession.Launch` (Owned).
- `drhook_step_test` — designed for `dotnet test` + `VSTEST_HOST_DEBUG=1` flow but currently surfaces a structured "not implemented" error to MCP callers (cited: `EngineSteppingSession.cs:200-206`). Aspirational, not functional.
- `drhook_step_launch` — attach to an already-running PID (cited: `DrHookTools.cs:94`). Substrate path: `DebugSession.Attach` (Borrowed). Description still says "Uses netcoredbg (MIT, DAP over stdio)" — **stale** (netcoredbg retired at 1.8.2 per ADR-006).
- `drhook_step_stop` — end session, detach (cited: `DrHookTools.cs:250`).

**Execution control:** `drhook_step_next` (over), `drhook_step_into`, `drhook_step_out`, `drhook_step_continue`, `drhook_step_pause` (cited: `DrHookTools.cs:109-164`).

**Breakpoints:** `drhook_step_breakpoint` (source), `drhook_step_break_function`, `drhook_step_break_exception` (filter: `"all"` or `"user-unhandled"`), `drhook_step_breakpoint_remove`, `drhook_step_breakpoint_list`, `drhook_step_breakpoint_clear` (cited: `DrHookTools.cs:166-237`).

**Inspection:** `drhook_step_vars` — return locals + arguments at current stop (cited: `DrHookTools.cs:239`; implementation: `EngineSteppingSession.cs:493`).

**Substrate diagnostics:** `drhook_drain_anomalies` — drain the engine's anomaly buffer (cited: `DrHookTools.cs:258`).

**Section header `// ─── Stepping layer (DAP / netcoredbg) ───` at `DrHookTools.cs:43` is also stale** (DAP / netcoredbg retired at 1.8.2).

### Substrate surface (what the engine actually exposes)

`src/DrHook.Engine/DebugSession.cs` public surface (verified by grep — line numbers cited inline):

**Lifecycle:** `Attach`, `AttachAndOwn`, `Launch` (factory methods); `Dispose`, `Detach`, `Abandon`, `RequestExit` (lifecycle per ADR-008).

**Control:** `Resume()` (`:338`), `Pause()` (`:455`), `WaitForStop(timeout)` (`:309`), `WaitForConditionalStop(Func<IEvalContext, bool>, timeout)` (`:463`), `WaitForPolicyStop` (`:490`), `WaitForExceptionPolicyStop` (`:523`).

**Stepping:** `StepInto`, `StepOver`, `StepOut` (`:586-594`).

**Introspection:** `EnumerateModules` (`:599`), `GetStackFrames` (`:605` — returns `IReadOnlyList<string>`, formatted as `"Method @ file:line"`), `GetArguments(depth)` (`:635`), `GetLocals(depth)` (`:656` — **TOP frame only**), `GetCurrentExceptionTypeName` (`:681`).

**Func-eval (experimental, narrow scope):** `TryEvalStaticCall` (parameterless static method, `:687`), `TryEvalStaticCallInt` (single int-arg static method, `:728`). Documented `EXPERIMENT (func-eval)`; **not general-purpose expression evaluation**.

**Breakpoints:** `SetBreakpoint(module, type, method)` (`:945`), `SetBreakpointAtLine(module, file, line)` (`:973`), `ListBreakpoints` (`:999`), `RemoveBreakpoint(id)` (`:1009`), `ClearBreakpoints` (`:1030`).

**Exception filters:** `ArmExceptionFilter(typeName, phase)` (`:1050`; `phase` ∈ `{None, FirstChance, Unhandled}`), `ListExceptionFilters`, `RemoveExceptionFilter`, `ClearExceptionFilters`.

**Not exposed (substrate-addition required):**
- General expression evaluation (Roslyn-driven). `EngineSteppingSession.SetBreakpointAsync` rejects non-empty `condition` parameter with explicit error: *"Conditional breakpoints are a polish item — the Roslyn walker lives in the probes today and hasn't been extracted into DrHook.Engine.Expressions yet"* (`EngineSteppingSession.cs:326-328`).
- Set next statement (ICorDebug `SetIP`).
- Data breakpoints.
- Hit-count semantics on breakpoints.
- Logpoint mode (log + continue, don't stop).
- Frame selection (locals are hardcoded to top frame per `DebugSession.cs:670-674`).
- Project inspection (MTP vs VSTest detection, executable resolution).

### IDE capability comparison (from research — citations in References)

Compressed from agent research (full table in research deliverable). Key gaps in DrHook's MCP surface vs. established IDE debuggers on macOS:

| Capability | VS | VS Code | Rider | DrHook today |
|---|:--:|:--:|:--:|:--:|
| Launch with breakpoint | F | F | F | F (`step_run`) |
| Attach to PID | F | F | F | F (`step_launch`) |
| Source / function / exception breakpoints | F | F | F | F |
| Step over/into/out + continue/pause | F | F | F | F |
| Locals / arguments | F | F | F | F (`step_vars` — top frame only) |
| **Conditional breakpoints** | F | F | F | **N** (substrate signature accepts; runtime rejects) |
| **Hit-count breakpoints** | F | F | F | **N** |
| **Logpoint / tracepoint** | F | F | F | **N** |
| **Run to cursor** | F | F | F | **N** |
| **Set next statement** | F | P | F | **N** |
| **Watch expressions** | F | F | F | **N** (only narrow static-method eval) |
| **Call stack with frame switch** | F | F | F | **P** (frames listed as strings; no switch) |
| **Async call stack** | F | P | F | **? — needs verification** |
| **Data breakpoints** | F | ? | P | **N** |
| **Edit and Continue / Hot Reload** | F | F | P | **N** |
| **Anomaly stream (substrate-correctness signals)** | N | N | N | **F** (`drain_anomalies`) — **unique to DrHook** |
| **Multi-process / child-attach** | P | N | P | **P** (substrate is per-session; MCP layer is singleton) |

Legend: F = full, P = partial, N = none, ? = unclear from official docs.

DrHook's structural advantages (no IDE matches): substrate-anomaly streaming, BCL-only deployment (no per-tier licensing), runtime substrate independence per ADR-006. Table-stakes gaps where DrHook is currently behind: conditional / hit-count / logpoint breakpoints, run-to-cursor, set-next-statement, watch expressions, full call stack with frame switching.

### Problems in the current surface (concrete enumeration)

1. **Naming inversion: `step_run` launches; `step_launch` attaches.** The verbs map opposite to IDE convention — VS/VS Code/Rider use *launch* for "start a new process under the debugger" and *attach* for "connect to an existing process." Current naming will confuse any agent (human or AI) bringing the standard mental model. **Confirmed: this is a real correctness problem in the self-describing surface, not a stylistic preference.**

2. **`step_breakpoint*` vs `step_break_*` prefix inconsistency.** Source breakpoints use `_breakpoint`; function and exception use `_break_`. Management tools use `_breakpoint_` (list/remove/clear). Inconsistent vocabulary in the same family.

3. **Stale descriptions reference retired technology.** `drhook_step_launch` description: *"Uses netcoredbg (MIT, DAP over stdio)"* (`DrHookTools.cs:96`). The substrate retired netcoredbg at 1.8.2 per ADR-006.

4. **`drhook_step_run` description contains a workaround note that contradicts shipped substrate capability.** *"Note: dotnet test spawns a child process that the debugger cannot follow — wrap test code in a file-based app and use dotnet exec instead"* (`DrHookTools.cs:50-51`). Phase 8 integration tests demonstrate the substrate CAN follow testhost via attach-after-stdout-PID-parse (the legacy VSTest path). The MCP tool's self-description is inconsistent with the substrate's actual capability.

5. **`drhook_step_test` is broken-by-design at MCP layer.** Returns structured "not implemented" error to any caller. Its description (`DrHookTools.cs:77-81`) presents the tool as functional. Self-description is materially incorrect.

6. **`drhook_step_breakpoint` and `step_break_function` advertise conditions** *(per their substrate-method signature, EngineSteppingSession.cs:323/345)* but actively reject any condition with explicit error (lines 326-328 / 348-349). MCP tool descriptions don't surface this gap. Agent-facing: tool *appears* to support conditional breakpoints; runtime says otherwise.

7. **`drhook_step_breakpoint_remove` polymorphic dispatch.** Accepts an exclusive-or of `{file+line | functionName | filter}`. JSON Schema (and MCP self-description) doesn't natively express this XOR constraint — agents may pass multiple, only one path is taken, others are silently ignored.

8. **Output shapes use `step` and `operation` for similar concepts.** `step_run` output uses `status: "launched"`; `step_continue` output uses `operation: "continue"`; stepping ops use `operation: "next" | "stepIn" | "stepOut"`. No consistent discriminator.

9. **`hypothesis` parameter is sometimes required (`drhook_snapshot`), sometimes optional (most step ops), sometimes unmentioned in description.** Inconsistent epistemic-discipline application.

10. **20 tools today; some are sub-cases of others.** E.g., `step_breakpoint` (source) and `step_break_function` (function) share enough that they could be one tool with a `kind` discriminator — but that contradicts the "MCP should be flat and self-describing" principle. *Need to decide: prefer multiple narrow tools, or fewer tools with richer parameter shapes?*

## Decision (proposed)

### Naming principles

1. **Verbs match IDE convention** (per the capability table in §Context): `launch`, `attach`, `stop`, `continue`, `pause`, `step_over`, `step_into`, `step_out`, `run_to_cursor`, `set_next_statement`.
2. **Substrate lifecycle is encoded by verb choice.** `launch` ⇒ Owned (target lifecycle managed by session, terminated on `stop` per ADR-008). `attach` ⇒ Borrowed (target survives session-stop). Per [ADR-008](ADR-008-process-lifecycle-discipline.md).
3. **Runner kind is never exposed at the API surface.** Project inspection happens internally inside `drhook_launch`; the dispatched kind is reported in the *output* `kind` field for diagnostic transparency only. Per [`feedback_substrate_dissolves_per_variant_planning`](../../../.claude/projects/-Users-bemafred/.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_substrate_dissolves_per_variant_planning.md): API-layer runner-agnosticism is a strict superset of substrate-layer runner-agnosticism.
4. **Breakpoints are first-class entities with stable IDs.** Creation returns ID; modification by ID; removal by ID. No polymorphic dispatch on disjoint parameters.
5. **`hypothesis` is universal, optional, consistent.** Every action that observes or changes program state accepts `hypothesis` as an optional parameter. Sky Omega epistemic discipline. Substrate-mandatory only at the substrate-discipline-critical observation points (snapshot, today).
6. **Tool descriptions are exhaustively self-describing.** Description tells the agent *what it does*, *when to use it (vs. siblings)*, *what it returns*, *known limitations*, *substrate guarantees and known gaps*. No out-of-band knowledge required.
7. **Prefix consistency: `drhook_<verb>` or `drhook_<category>_<verb>`.** Flat is preferred; sub-categorize only when necessary (`drhook_break_<variant>`).
8. **Naming asymmetry between existing-but-stale and new tools is acceptable during transition.** MCP self-description means agents read the current state; backwards-compat is not a substrate concern.

### Tool catalog (proposed — 23 tools)

Status legend per tool:
- **(rename-only)** — current substrate capability; tool gets new name + clearer description.
- **(restructure)** — current substrate capability; parameter shape changes (e.g., by-ID remove).
- **(verify substrate)** — substrate may or may not support; need code verification before increment can ship.
- **(substrate addition)** — substrate work required; tool is aspirational until substrate ships.

#### Session lifecycle (3 tools)

| Tool | Replaces | Status |
|---|---|---|
| `drhook_launch` | `step_run` + (logically) `step_test` | rename-only for `.exe` / `dotnet+dll` launch; **substrate addition** for `.csproj` project-mode (inspector + executable-resolver + dispatcher) |
| `drhook_attach` | `step_launch` | rename-only |
| `drhook_stop` | `step_stop` | rename-only |

#### Execution control (7 tools)

| Tool | Replaces | Status |
|---|---|---|
| `drhook_continue` | `step_continue` | rename-only |
| `drhook_pause` | `step_pause` | rename-only |
| `drhook_step_over` | `step_next` | rename-only |
| `drhook_step_into` | `step_into` | rename-only |
| `drhook_step_out` | `step_out` | rename-only |
| `drhook_run_to_cursor` | — | **(new)** composable from existing primitives (transient breakpoint + continue + remove on hit); substrate work bounded |
| `drhook_set_next_statement` | — | **(substrate addition)** ICorDebug `SetIP` not currently exposed on `DebugSession` |

#### Breakpoints (7 tools)

| Tool | Replaces | Status |
|---|---|---|
| `drhook_break_source` | `step_breakpoint` | rename-only for `(file, line)`; **(substrate addition)** for `condition` (Roslyn walker extraction per `EngineSteppingSession.cs:328`), `hitCount`, `logMessage` |
| `drhook_break_function` | `step_break_function` | rename-only for `method`; same substrate additions for `condition`, `hitCount` |
| `drhook_break_exception` | `step_break_exception` | rename-only |
| `drhook_break_data` | — | **(substrate addition)** — ICorDebug field-write breakpoint; not in current substrate |
| `drhook_break_list` | `step_breakpoint_list` | rename-only |
| `drhook_break_remove` | `step_breakpoint_remove` | restructure — by-ID only, not polymorphic |
| `drhook_break_clear` | `step_breakpoint_clear` | rename-only |

#### Inspection (3 tools)

| Tool | Replaces | Status |
|---|---|---|
| `drhook_locals` | `step_vars` | rename-only for top-frame; **(substrate addition)** if `frame` parameter targets non-top frame |
| `drhook_watch` | — | **(substrate addition)** — general Roslyn-based expression evaluation; substrate has only narrow static-method func-eval today (`DebugSession.cs:687/728`) |
| `drhook_frames` | — | **(verify substrate)** — `DebugSession.GetStackFrames` returns strings; rich frame data exists internally per ADR-007 Phase 1c. Whether substrate can expose `{index, method, file, line, module}` records + frame-selection state is to verify |

#### Observation (sessionless) (2 tools)

| Tool | Replaces | Status |
|---|---|---|
| `drhook_processes` | `drhook_processes` | no change |
| `drhook_snapshot` | `drhook_snapshot` | no change |

#### Substrate diagnostics (1 tool)

| Tool | Replaces | Status |
|---|---|---|
| `drhook_drain_anomalies` | `drhook_drain_anomalies` | no change |

**Total: 23 tools** (vs. 20 today; net additions are `run_to_cursor`, `set_next_statement`, `break_data`, `watch`, `frames` — minus `step_test` consolidated into `launch`).

### Tiered increments

The redesign ships in **three tiers**, ordered by risk and substrate dependency.

#### Tier 1 — Rename-only, low risk, immediate value

Tools whose substrate capability exists today; only the MCP surface needs work. Ships as a single PR.

- `drhook_launch` (rename of `step_run`; **NOT** including project-mode yet — that's Tier 3)
- `drhook_attach` (rename of `step_launch`; also fix stale netcoredbg description)
- `drhook_stop` (rename of `step_stop`)
- `drhook_continue`, `drhook_pause`, `drhook_step_over`, `drhook_step_into`, `drhook_step_out` (renames)
- `drhook_break_source`, `drhook_break_function`, `drhook_break_exception`, `drhook_break_list`, `drhook_break_remove` (by-ID restructure), `drhook_break_clear` (renames)
- `drhook_locals` (rename of `step_vars`)
- `drhook_processes`, `drhook_snapshot`, `drhook_drain_anomalies` (unchanged, descriptions audited)
- **Remove `drhook_step_test`** (the not-implemented stub) — its replacement (`drhook_launch` accepting `.csproj`) lands in Tier 3.
- **Fix all stale descriptions** (`netcoredbg`, `DAP / netcoredbg` section header, `step_run`'s testhost-attach-impossible note).
- **Surface known limitations in tool descriptions.** Source/function breakpoint descriptions should say: "Conditional breakpoints are not yet supported; substrate work pending."

**Tier 1 scope: 18 tools renamed/audited; 2 tools removed (`step_test`); MCP-surface only, no substrate changes.** Estimated effort: half a day. Validation: existing 12/12 integration tests still pass after rename; manual MCP-tool invocation against the renamed surface.

#### Tier 2 — Substrate-verification tools

Tools that *appear* implementable from existing substrate primitives but require verification before adding.

- **`drhook_run_to_cursor`** — composable from `SetBreakpointAtLine` + `Resume` + `WaitForStop` + `RemoveBreakpoint`. Mostly orchestration; needs ID-tracking for the transient breakpoint. **Open Question:** does the substrate handle a breakpoint that's hit, then removed mid-session, then a fresh continue, cleanly?
- **`drhook_frames`** — `GetStackFrames` returns strings; the agent research result identifies async call stack reconstruction as a Rider/VS strength. **Open Question:** what does substrate's `WalkManagedFrames` (cited at `DebugSession.cs:608`) expose internally? Can rich frame records be returned without async-stack reconstruction (which is harder)?
- **`drhook_locals` with `frame` parameter** — substrate's `GetLocals` is hardwired to the top frame (`DebugSession.cs:670-674`). Frame-selection requires substrate to accept a frame index and read locals via the selected frame's IL context. **Open Question:** is this a small substrate addition or a deeper change?

Tier 2 ships incrementally as each Open Question is resolved by a probe or by reading more of the substrate.

#### Tier 3 — Substrate-addition tools

Tools that require new substrate capability before any MCP surface can be added.

- **`drhook_launch` accepting `.csproj`** — requires project inspector (MTP / VSTest / future-variant detection), executable resolver, dispatcher. This is the substantive scope of the original ADR-009.
- **`drhook_break_source` / `drhook_break_function` with `condition`** — substrate work **DONE in Increment 2** (extracted as `DrHook.Engine.Expressions/CSharpCondition`; `BreakpointPolicySpec.CompileWith` + `DebugSession.Compile` are the canonical substrate surfaces). MCP wire-up pending — moved into Increment 6 below.
- **`drhook_break_source` with `hitCount`** — substrate **DONE in Increment 2** (`HitCountGate` + `BreakpointPolicy.HitCount`). MCP wire-up pending — Increment 6.
- **`drhook_break_source` with `logMessage`** — substrate-internal logpoint mode **DONE in Increment 2** (`BreakpointPolicy.LogMessage` evaluated, with `LogRecord` emission). The `{expr}` interpolation template compiler is the remaining substrate work; `BreakpointPolicySpec.CompileWith` currently throws `NotImplementedException` when `LogMessage` is set. Pending as a follow-up to Increment 1.
- **`drhook_break_function` with `hitCount`** — same as `break_source`; substrate done.
- **`drhook_break_data`** — ICorDebug field-write breakpoint. Substrate has not surfaced this; ICorDebug primitive support is `[?]` and needs subject-matter verification (Open Question).
- **`drhook_set_next_statement`** — ICorDebug `SetIP` on the current frame. Substrate does not currently expose; primitive availability `[?]`.
- **`drhook_watch`** — general Roslyn expression evaluation against locals + arguments + `this`. Substrate has only narrow static-method func-eval (`DebugSession.cs:687/728`); a general eval surface is substantial new work.

Each Tier 3 tool ships when its substrate dependency lands. Sequencing is not prescribed by this ADR; per-tool work would be motivated by demand and validated by a probe + finding.

### Increment 6 — Exception breakpoint MCP surface + breakpoint introspection alignment

**Status:** Completed — 2026-05-29 (Proposed → Completed same day). All six deliverables shipped on `main`:

| # | Deliverable | Commit |
|---|---|---|
| 1 | `drhook_step_break_exception` explicit parameters (`typeName` + `phase?` + `condition?` + `hitCount?` + `suspend?`) | `77d11b8` |
| 2 | `drhook_step_breakpoint_list` full descriptors per entry (`id`, location, `hits`, `policy?`) — `DebugSession.GetBreakpointHits` + `GetExceptionFilterHits` added to substrate | `6170ec1` |
| 3 | `drhook_step_breakpoint_remove` by-ID canonical (collapsed three kind-specific methods into one `RemoveByIdAsync`) | `b86d991` |
| 4 | `drhook_step_breakpoint` + `drhook_step_break_function` accept `condition` / `hitCount` / `suspend` parameters; stale "Roslyn walker lives in the probes" rejection error removed | `8720335` |
| 5 | Probe 57 — target-defined exception hierarchy validation (`MyApp.OrderValidationException : MyApp.DomainException`; filter on base, throw derived, substrate's `MatchesChain` admits via cross-module `ICorDebugType.GetBase` walk) | `59be9ce` |
| 6 | Tool description audit: `step_break_exception` cross-references to list/remove; DEBUGGING.md tool tables + "What's NOT yet shipped" section updated; workflow example expanded with policy parameters | `82471a0` |

Pre-deliverable substrate verification — code-read of `Interop/ExceptionInspector.cs:60-92` confirmed the chain walk is runtime-driven via `ICorDebugType.GetBase` with no BCL-specific path; target-defined types fall out of the same mechanism that handles BCL types. Probe 57 then validated this empirically against a target-defined two-level hierarchy.

95/95 `DrHook.Engine` unit tests pass through every deliverable; probes 22, 23, 25, 28, 29, 30 all still pass (the migrated set), and probe 57 PASSED on macOS-arm64 with substrate's cross-module subclass walk admitting `MyApp.DomainException` to match a `MyApp.OrderValidationException` throw.

The "deliberate out-of-scope" items remain out: enable/disable toggle independent of Arm/Remove (substrate uses Arm/Remove primitives; functionally equivalent); `{expr}` template compiler for `LogMessage` (now scoped formally as Increment 7 below).

### Increment 7 — LogMessage template compiler

**Status:** Proposed — 2026-05-30.

The single deliberate exclusion from Increment 6 — and the only remaining "what's NOT yet shipped" gap that's substrate-level rather than orchestration-level. The substrate's full logpoint mechanism is shipped end-to-end (probes 28/29 validate the `BreakpointPolicy.LogMessage` delegate flow + `Suspend.None` auto-resume + `IDebugEventSink.OnLog` emission); what's missing is the string-to-delegate compilation for templates like `"hit count = {count}, value = {value}"`. Today `BreakpointPolicySpec.CompileWith` throws `NotImplementedException` if `LogMessage` is non-null; the four MCP set tools omit `logMessage` accordingly. Increment 7 closes that hole.

#### Context — what already exists

- **Substrate delegate-form**: `BreakpointPolicy.LogMessage: Func<IEvalContext, string>?`. Called once per qualifying breakpoint hit; result emitted as a `LogRecord` via `IDebugEventSink.OnLog`. Probes 28/29 validate the flow with hand-built renderers.
- **Substrate compiler subset** (`Expressions/CSharpCondition.cs:Eval`): literals, identifiers (local lookup), member access (via `IMemberResolver.TryEvalMemberCall`), parenthesized, logical NOT, binary `&& || == != < > <= >=`. **Arithmetic (`+ - * /`) is NOT in the walker** — Increment 7 stays within the existing subset; extending arithmetic is a separate substrate addition.
- **Test seam**: `BreakpointPolicySpec.CompileWith(IMemberResolver)` is internal; tests can supply a fake resolver.

#### Deliverables

1. **`CSharpCondition.CompileTemplate(string template, IMemberResolver memberResolver) → Func<IEvalContext, string>`** — substrate method that parses a template into literal segments interleaved with `{expr}` fragments and returns a renderer:
   - Scan the template for `{` / `}` boundaries; escape sequences `{{` and `}}` produce literal braces (VS Code DAP convention).
   - Each `{expr}` fragment is parsed with `SyntaxFactory.ParseExpression` and walked via the existing `Eval` (refactored to be reusable from both `Compile` and `CompileTemplate`).
   - At render time: each fragment's evaluation result is stringified with `Convert.ToString(..., CultureInfo.InvariantCulture)`; literal segments emit verbatim; final string is the concatenation.
   - Mismatched braces, empty `{}`, or expressions in unsupported syntax throw at compile time with the offending fragment quoted in the message.

2. **`BreakpointPolicySpec.CompileWith` no longer throws on `LogMessage`** — calls `CSharpCondition.CompileTemplate(LogMessage, memberResolver)` and stores the resulting `Func<IEvalContext, string>` in `BreakpointPolicy.LogMessage`.

3. **`drhook_step_breakpoint`, `drhook_step_break_function`, `drhook_step_break_exception` — `logMessage: string?` parameter**. Flows through `BreakpointPolicySpec` → `DebugSession.Compile` → `SetBreakpointAtLine` / `SetBreakpoint` / `ArmExceptionFilter`. The `suspend="none"` parameter (already exposed) becomes meaningful when paired with `logMessage` — the substrate's existing logpoint flow renders + emits + auto-resumes per ADR-010 Increment 2 + Increment 6.

4. **`drhook_step_breakpoint_list` `policy.logMessage` field** — `RenderPolicy` already emits it when present in the Spec; will start appearing in list output once tools pass it through.

5. **Probe 58 — template compiler end-to-end.** Target with a known local (e.g. `int counter` incrementing 0..N); breakpoint with template `"counter={counter} squared={counter * 2}"` — actually no, multiplication isn't supported; use `"counter={counter} parity={counter == 0}"` instead, exercising literal text + identifier interpolation + comparison-fragment interpolation. Probe asserts N well-formed `LogRecord` entries surface via the sink, content matching the iteration values. Probe number 58 (next available after 57).

6. **Unit tests — `CSharpConditionTemplateTests`.** Pure-logic tests against a fake `IEvalContext` + `IMemberResolver`: empty template, literal-only, single fragment, mixed text+fragments, multiple fragments, escaped `{{`/`}}`, member-access fragments via fake resolver, malformed (unbalanced brace) throws with clear error, expression with unsupported syntax throws with offending fragment quoted.

7. **MCP description audit** — tool descriptions for `step_breakpoint` / `step_break_function` / `step_break_exception` gain a `logMessage` parameter row + an explanation of `{expr}` syntax with the walker's supported-subset note. `drhook_step_breakpoint_list` description mentions `logMessage` may appear in the policy summary.

8. **DEBUGGING.md** — "What's NOT yet shipped" `Logpoint LogMessage interpolation` entry pruned post-shipping. Workflow example expanded with a logpoint usage.

#### Out of scope (deliberate)

- **Arithmetic in expression fragments** (`{count + 1}`, `{value * 2}`). Walker doesn't support these today. Extending `ApplyBinary` to admit `AddExpression` / `SubtractExpression` / `MultiplyExpression` / `DivideExpression` and producing `long` results is a separate substrate addition — orthogonal to template compilation. Probe 29's hand-built renderer (`$"v={v} doubled={2*v}"`) stays delegate-form until that lands.
- **String-interpolation format specifiers** (`{value:X}`, `{value:N2}`). Out of scope — fragments evaluate to whatever the walker produces; stringification uses invariant culture default. Format-specifier syntax is a follow-on.
- **Multi-line / verbatim templates**. Single-line interpolation only. Newlines in templates are passed through verbatim if the agent supplies them.

#### Validation

- All existing 95 unit tests continue to pass.
- New `CSharpConditionTemplateTests` (estimated ~10 tests covering the cases above) pass.
- Probe 58 passes on macOS-arm64 with N well-formed logpoint emissions matching iteration values.
- A round-trip through `drhook_step_breakpoint(logMessage: "v={v}", suspend: "none")` → continue → time-bounded `drhook_step_breakpoint_list` → drain anomalies confirms substrate emits the rendered messages via `OnLog` as the substrate already does for the delegate form.

#### Why this increment is well-bounded

The substrate compiler subset already exists. Logpoint mechanics from policy gates through `OnLog` emission to auto-resume are all shipped. The change is one parsing pass + a refactor of `Eval` to be reusable from both `Compile` (bool predicate) and `CompileTemplate` (string renderer) + plumbing on the four MCP set tools. No new substrate type, no `DebugSession` API addition beyond removing one `NotImplementedException` throw, no `CallbackPump` changes.

Increment 2 (closed 2026-05-29 by commits `8cce862` … `2d16923` + `824cc0b`) shipped the substrate work that makes per-breakpoint and per-exception-filter policies first-class: `BreakpointPolicy` (delegate) / `BreakpointPolicySpec` (string), `DebugSession.Compile`, `SetBreakpointAtLine(...policy)`, `SetBreakpoint(...policy)`, `ArmExceptionFilter(...policy)`, and caller-thread evaluation in `WaitForStop` for both breakpoint and exception-filter locations. The substrate also already supports subclass-aware exception matching via `ExceptionInspector.CurrentExceptionTypeChain` (runtime-driven `ICorDebugType.GetBase` walk; cross-module-safe per cordebug.idl — verified by `Interop/ExceptionInspector.cs:60-92`, probe 37, finding 47).

The **MCP-tool surface has not kept up** with this substrate granularity:

- `drhook_step_break_exception(filter: string)` advertises only `"all"` and `"user-unhandled"` in its description. Its code path silently accepts any other string as a literal type name (`EngineSteppingSession.cs:373-378`), but no agent reading the description would discover this. Phase, condition, hit count, and logpoint mode are entirely absent.
- `drhook_step_breakpoint_list` returns the agent's original `filter` string for exception entries (e.g. `"all"`), losing the resolved `typeName` / `phase`. Source and function entries return `{id, file, line}` / `{id, function}` — the attached `BreakpointPolicy` (condition, hit count, log message, suspend, current hit count) is invisible to the agent.
- `drhook_step_breakpoint_remove` description ties the removal key to the lossy filter alias rather than the canonical breakpoint ID.

Increment 6 closes this MCP-surface gap, with a new probe explicitly validating target-defined exception hierarchies (not just BCL types).

#### Deliverables

1. **`drhook_step_break_exception` — explicit parameter surface.** Replace the single `filter: string` parameter with:
   - `typeName: string` (required) — fully-qualified CLR type name (`"System.NullReferenceException"`, `"MyApp.DomainException"`, etc.) or `"*"` wildcard. Document the subclass-aware match semantics with an explicit example covering a target-defined hierarchy.
   - `phase: string?` (optional, default `"any"`) — one of `"any" | "first-chance" | "user-unhandled" | "catch-handler-found" | "unhandled"`.
   - `condition: string?` (optional) — compiled via `DebugSession.Compile(new BreakpointPolicySpec(...))`.
   - `hitCount: int?` (optional).
   - `suspend: string?` (optional, default `"all"`) — `"all" | "none"`.
   - Returns: `{id, typeName, phase, hits: 0, policy?, prompt}`.
   - Convenience presets `"all"` / `"user-unhandled"` stay valid as MCP-layer aliases the description recommends, but the explicit-parameter form is the canonical surface.

2. **`drhook_step_breakpoint_list` — full descriptors.** Each entry exposes the substrate's view in full:
   - Source: `{id, file, line, policy?, hits}`.
   - Function: `{id, function, policy?, hits}`.
   - Exception: `{id, typeName, phase, policy?, hits}`.
   - `policy?` is `{condition?, hitCount?, logMessage?, suspend}` when present.

3. **`drhook_step_breakpoint_remove` — by-ID canonical.** Accept `id: int` as the primary identifier. The polymorphic `(file+line | functionName | filter)` form deprecates in favor of "list to discover IDs, remove by ID."

4. **`drhook_step_breakpoint` (source) and `drhook_step_break_function` — accept policy parameters.** Add `condition: string?`, `hitCount: int?`, `logMessage: string?`, `suspend: string?` parameters. Same string-spec → `BreakpointPolicySpec.CompileWith` → policy flow as the exception variant. `logMessage` gated until the template compiler lands (substrate currently throws `NotImplementedException` for it).

5. **Probe 57 — target-defined exception hierarchy.** New probe in `poc/drhook-engine/`. Target defines `MyApp.DomainException : System.Exception` and a derived `MyApp.OrderValidationException : MyApp.DomainException`. Probe arms an exception filter on `"MyApp.DomainException"`, throws an `OrderValidationException` in the target, asserts the filter matches via the substrate's subclass-chain walk across the target's own module. Validates the cross-module `GetBase` path explicitly for target-defined types, not just BCL types. Probe number 57 is free per the ADR-007 renumbering.

6. **Tool descriptions updated to match substrate behavior** — no more `'all' or 'user-unhandled'` lossy framing. Subclass-aware semantics documented with examples. Resolved `typeName` / `phase` shown in tool output.

#### Out of scope (deliberate exclusions)

- **Enable/disable toggle independent of Arm/Remove.** VS/VS Code/Rider expose an enable checkbox; substrate has Arm/Remove primitives. Functionally equivalent at the agent surface (Arm to enable, Remove to disable). If a stateful "armed-but-disabled" representation is needed later, that's a separate increment.
- **`{expr}` template compiler for `LogMessage`.** Pending as a follow-up to Increment 1 (the `CompileTemplate` variant of `CSharpCondition`). Once it lands, `BreakpointPolicySpec.CompileWith` stops throwing on `LogMessage`, and the MCP `logMessage` parameter on `step_breakpoint` / `step_break_function` / `step_break_exception` becomes functional.

#### Validation

- Tool descriptions match substrate behavior; no lossy abstractions.
- Probe 57 passes on macOS-arm64 (target-defined hierarchy filter matches).
- Existing 95/95 unit tests and probes 22, 23, 25, 28, 29, 30 still pass.
- A round-trip from `drhook_step_breakpoint_list` shows everything an agent needs to reason about the breakpoint set (type, phase, policy, hits) without out-of-band knowledge.

#### Why this increment is well-bounded

The substrate work is **done** as of Increment 2. This increment is purely MCP-layer alignment (parameter shapes, tool descriptions, output JSON) plus one verification probe for the target-defined-hierarchy case the existing probes don't cover. No `DebugSession` API changes; no `CallbackPump` changes; no new compilation surfaces.

## Open questions — answer before Proposed → Accepted

1. **Naming style: `drhook_step_over` vs. `drhook_step_next`?** VS uses "Step Over" terminology; Rider uses "Step Over"; VS Code DAP uses "Step Over". Current MCP tool is `step_next`. Standardize to `step_over` (matches industry). **Confirm.**
2. **`drhook_stop` vs. `drhook_detach` vs. `drhook_end_session`?** "Stop" matches VS Code's "Stop Debugging" terminology. For Owned sessions the target is terminated; for Borrowed it survives. "Stop" can read as ambiguous (stops the *target*? or the *session*?). **Reviewer call.**
3. **Does the substrate expose async-stack-reconstruction primitives?** Rider's blog cites "Async call stack debugger improvements" (2017). VS/VS Code expose async stacks via DAP. **`[?]` — needs subject-matter verification on what `Frames.WalkManagedFrames` actually provides and whether async continuation reconstruction is available.**
4. **Is `IEvalContext` usable for an MVP `drhook_watch`?** `WaitForConditionalStop(Func<IEvalContext, bool>, ...)` exists (`DebugSession.cs:463`) — suggests substrate already has some eval context primitive. Could a Roslyn-based MCP tool use `IEvalContext` for arbitrary expression evaluation, or is it limited to the conditional-stop scenario? **Verify.**
5. **`drhook_break_data` ICorDebug support.** The agent-research result classifies data breakpoints as "VS: explicit support; Rider: limited; VS Code: not documented." Is the ICorDebug API support actually present, or is this a runtime-version-gated capability? **`[?]` — subject-matter verification required.**
6. **`hypothesis` discipline — universal or selective?** Current substrate makes `hypothesis` *required* for `drhook_snapshot` (the strongest discipline). Step operations make it *optional*. Should the redesigned API tighten this — require `hypothesis` on every state-changing operation? Or relax (rare-required everywhere)? **Reviewer call.**
7. **Tool-count target.** 23 tools (proposed) vs. some IDE-style consolidation. Smaller tool count → simpler agent surface but each tool grows in parameter complexity. Larger tool count → more self-describing per tool but more enumeration overhead. **Trade-off — reviewer call.**
8. **`drhook_break_source(logMessage)` as a separate `drhook_logpoint` tool?** VS Code DAP treats logpoints as a breakpoint variant with `logMessage`. VS calls them "Tracepoints" — a distinct breakpoint kind in UI. **Style: same tool with conditional param, or separate tool?**
9. **Multi-process / multi-session.** The substrate is per-session (each `DebugSession.AttachAndOwn` returns an independent session); the MCP layer's `EngineSteppingSession` is a DI singleton. Should `drhook_launch` / `drhook_attach` return a session ID, allowing multiple concurrent sessions per MCP server? **Out of this ADR's scope but flagged for a successor ADR.**
10. **`drhook_step_test` removal timing.** Today it returns "not implemented yet" structured error. Tier 1 removes the tool. **Confirm timing: before or after Tier 3's `drhook_launch` accepts `.csproj`?** Pre-substrate-replacement is honest (no false advertising); post-substrate-replacement is safer (no capability gap). **Reviewer call.**

## Validation

### Proposed → Accepted requires:

- All Open Questions answered by reviewer (Martin) — either resolved as decisions or explicitly deferred with rationale.
- Tier 1 scope confirmed as the first deliverable.
- Naming-principle list reviewed and confirmed (especially principles 1, 3, 4).
- Decision to supersede ADR-009 confirmed; ADR-009 marked Superseded in its file and in the DrHook ADR index.

### Accepted → Completed requires:

- Tier 1 shipped: 18 tools renamed/restructured, stale descriptions fixed, `step_test` removed, existing 12/12 integration tests still passing.
- Tier 2 + Tier 3 tools each ship in successor increments, with substrate verification or addition recorded as findings.
- DrHook.Mcp `README.md` updated to reflect actual tool count and current capability surface (today's README claims "19 tools spanning EventPipe observation, DAP stepping, breakpoint management, expression evaluation, and test debugging" — count is wrong, "DAP" is stale, "test debugging" is aspirational).
- Tool descriptions surface known substrate-gap limitations honestly (e.g., "Conditional breakpoints not yet supported — substrate work pending").

## Consequences

### For agents (human and AI)

Tool names match the established debugger vocabulary. Mental model from any IDE transfers directly. Self-description is exhaustive; no out-of-band documentation required. **`drhook_step_test` removal will appear as a regression to any caller depending on it today — but caller's actual behavior is "structured error response," so there's no functional capability loss.**

### For substrate

Tier 1 has zero substrate impact. Tier 2/3 surface real substrate gaps (conditional breakpoints, hit counts, logpoints, watch expressions, set-next-statement, data breakpoints, frame selection, project inspection) — but the ADR makes these gaps explicit rather than concealed by misleading tool descriptions.

### For ADR chain

- ADR-009 is superseded by this ADR (the test-debugging-specific scope is a special case of `drhook_launch`'s project mode, Tier 3).
- ADR-007 Phase 7 (MCP surface cleanup) becomes a sub-deliverable of this ADR's Tier 1.
- Future substrate ADRs (Roslyn walker extraction, ICorDebug `SetIP` exposure, etc.) can reference this ADR as the consumer rationale.

### For the strategic position

Tool naming and self-description quality determines how AI coding agents learn the substrate. A clean, IDE-aligned vocabulary lowers the substrate's adoption cost for any agent that already knows VS/Rider/VS Code conventions. The substrate's structural advantages — anomaly streaming, BCL-only, runtime substrate independence — surface more clearly when the rest of the API doesn't carry historical naming debt.

## References

### Code reads (factual basis)

- `src/DrHook.Mcp/DrHookTools.cs` — current MCP tool surface (20 tools).
- `src/DrHook.Mcp/EngineSteppingSession.cs` — MCP-to-substrate adapter; reveals which capabilities are wired and which are stubbed.
- `src/DrHook.Engine/DebugSession.cs` — substrate public surface.

### Prior ADRs

- [ADR-006](ADR-006-drhook-engine.md) — DrHook.Engine substrate; netcoredbg retirement at 1.8.2.
- [ADR-007](ADR-007-teardown-concurrency-test-debug.md) — Phase 1 substrate-correctness arc; Phase 7 (MCP surface cleanup) becomes sub-deliverable of this ADR's Tier 1.
- [ADR-008](ADR-008-process-lifecycle-discipline.md) — lifecycle discipline; informs `drhook_stop` semantics for Owned vs Borrowed sessions.
- [ADR-009](ADR-009-test-debugging-mcp-surface.md) — superseded by this ADR.

### Memory references

- [`feedback_substrate_dissolves_per_variant_planning`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_substrate_dissolves_per_variant_planning.md) — the runner-agnostic principle applied at API-surface level here.
- [`project_drhook_adr008_closure`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_drhook_adr008_closure.md) — DrHook substrate state as of ADR-008 closure; foundation for this redesign.

### IDE capability research (agent-result citations)

**Visual Studio:**
- [Debugger feature tour](https://learn.microsoft.com/en-us/visualstudio/debugger/debugger-feature-tour)
- [Using breakpoints](https://learn.microsoft.com/en-us/visualstudio/debugger/using-breakpoints)
- [Parallel Stacks window](https://learn.microsoft.com/en-us/visualstudio/debugger/using-the-parallel-stacks-window)
- [Decompile .NET code while debugging](https://learn.microsoft.com/en-us/visualstudio/debugger/decompilation)
- [Debug multi-process applications](https://learn.microsoft.com/en-us/visualstudio/debugger/debug-multiple-processes)
- [Child Process Debugging Power Tool](https://marketplace.visualstudio.com/items?itemName=vsdbgplat.MicrosoftChildProcessDebuggingPowerTool)
- [Hot Reload](https://learn.microsoft.com/en-us/visualstudio/debugger/hot-reload)

**VS Code (C# Dev Kit / vsdbg):**
- [C# Debugging](https://code.visualstudio.com/docs/csharp/debugging)
- [C# Dev Kit FAQ](https://code.visualstudio.com/docs/csharp/cs-dev-kit-faq)
- [Generic debugging in VS Code](https://code.visualstudio.com/docs/debugtest/debugging)
- [C# Dev Kit Updates: Hot Reload](https://devblogs.microsoft.com/dotnet/csharp-on-visual-studio-code-just-got-better-with-enhancements-to-csharp-dev-kit/)

**JetBrains Rider:**
- [Debugging Code](https://www.jetbrains.com/help/rider/Debugging_Code.html)
- [Debug multi-thread and async applications](https://www.jetbrains.com/help/rider/Debugging_Multithreaded_Applications.html)
- [Parallel Stacks](https://www.jetbrains.com/help/rider/Parallel_Stacks.html)
- [Hot Reload](https://www.jetbrains.com/help/rider/Hot_Reload.html)
- [Async call stack improvements (blog)](https://blog.jetbrains.com/dotnet/2017/12/13/async-call-stack-debugger-improvements-rider-2017-3/)

### Cross-cutting Sky Omega context

- [`docs/architecture/technical/drhook-test-debugging-assessment.md`](../../architecture/technical/drhook-test-debugging-assessment.md) — consolidated DrHook test-debugging design rationale (MTP-first strategy, BCL-only sovereignty argument, process-topology analysis). Foundational reference for substrate-strategy decisions; informs this ADR's substrate-runner-agnosticism principle. On one specific point — the "explicit modes / `--runner` flag" CLI recommendation — this ADR takes a different position (Decision principle 3: runner kind never exposed at API surface), per the meta-insight that substrate generality dissolves per-variant API decomposition. The rest of the document remains the current substrate-strategy reference.
- [`docs/architecture/technical/drhook-test-debugging.md`](../../architecture/technical/drhook-test-debugging.md) — earlier first-pass analysis; explicitly superseded by the consolidated assessment per its own header. Retained as part of the design-rationale epistemic record.
