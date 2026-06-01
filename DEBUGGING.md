# Debugging with DrHook

DrHook is Sky Omega's runtime observation substrate. Use it when you need to understand what code is actually doing — not what you think it's doing.

The substrate is `DrHook.Engine` — a BCL + P/Invoke + source-gen COM implementation of `ICorDebug` interop, with `libdbgshim` bundled per-RID via NuGet. `netcoredbg` was retired at 1.8.2; the previous DAP-over-stdio path no longer exists.

> **Surface-redesign note (2026-05-31):** the MCP tool names below are the **post-rename** surface shipped by [ADR-010](docs/adrs/drhook/ADR-010-mcp-tool-surface-redesign.md) (Accepted) Tier 1. Names now follow established IDE-debugger convention (`launch` starts a new process, `attach` connects to a running one) and agree with the substrate's own `DebugSession.Launch` / `.Attach` vocabulary. The previous `drhook_step_*` names are shown in the "Former name" column for one transition cycle. Capabilities are unchanged from before the rename.
>
> **Production-suitability status:** substrate-correctness on macOS-arm64 is CI-enforced via [ADR-008](docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) (Completed) + [ADR-007](docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phases 1, 2, 8 (Complete). 12/12 integration tests pass reliably. Cross-platform validation is ADR-007 Phase 9 (Open). Use with care under test runners on platforms other than macOS-arm64.

## When to Use DrHook

**Use DrHook when:**
- A bug persists after your first fix attempt — you're guessing, not observing.
- The behavior depends on runtime state (buffer positions, loop iterations, async continuations).
- You need to verify an assumption about control flow or data at a specific point.
- Increasing a constant or adding a workaround feels like the right fix — it never is.
- You're about to change code you haven't observed running.

**The rule:** If you've made two attempts at fixing something and it's still broken, stop coding and start observing. DrHook exists for exactly this moment.

## Methodology: Observe Before Fixing

1. **Reproduce with minimal input.** Create the smallest possible test case that triggers the bug. Use file-based apps (`.cs` scripts) for quick iteration.
2. **Set breakpoints at the decision points.** Not at the error — at the code paths that lead to the error. The bug is in the decision, not the exception.
3. **Step through and record state.** Use `drhook_locals` at each stop to capture actual values. Compare what you see with what you expected.
4. **Identify the invariant violation.** The bug is where reality diverges from your mental model. Once you see the actual state, the fix is usually obvious.
5. **Fix and verify.** Change the code, re-run with the same breakpoints to confirm the fix.

## DrHook MCP Tools

22 tools. Every state-changing operation and every inspection that reads target state takes a `hypothesis` parameter — state what you expect *before* you observe (Sky Omega epistemic discipline; ADR-010 Decision principle 5).

### Session lifecycle

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_launch` | **Launches a NEW process** under debugger control (Owned session). | `drhook_step_run` |
| `drhook_attach` | **Attaches to an already-running .NET process** by PID (Borrowed session — target survives the session). | `drhook_step_launch` |
| `drhook_stop` | **Ends the session** — the normal finish. Borrowed: detaches, target keeps running. Owned: target asked to exit gracefully — SIGTERM → SIGKILL if it doesn't exit within the ~2s window (ADR-008). | `drhook_step_stop` → `drhook_detach` |
| `drhook_detach` | Detaches and **leaves the target running** (deliberate). Borrowed: supported. Owned: not yet — pending finding **F-010-2** (the launched target is the debugger's child); returns an error pointing to `drhook_stop` / `drhook_kill`. | *(new — ADR-011 D1)* |
| `drhook_kill` | **Forcibly terminates** the target — anomaly escape hatch, *not* normal cleanup; every call is worth investigating. Owned: SIGTERM brief-grace → SIGKILL (`DebugSession.Abandon`, ADR-008). Borrowed: not yet — pending finding **F-010-1**. | *(new — ADR-011 D1)* |

`drhook_step_test` was **removed** in this rename (it only ever returned "not implemented") — see [What's NOT yet shipped](#whats-not-yet-shipped).

### Execution control

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_continue` | Resume execution; optionally wait for next stop | `drhook_step_continue` |
| `drhook_pause` | Pause running process (`ICorDebugController.Stop`) | `drhook_step_pause` |
| `drhook_step_over` | Step over (execute the current line without descending into calls) | `drhook_step_next` |
| `drhook_step_into` | Step into the method call on the current line | *(unchanged)* |
| `drhook_step_out` | Step out of the current method, back to the caller | *(unchanged)* |

### Breakpoints

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_break_source` | Set source breakpoint at file:line. Optional policy: `condition` (C# expression evaluated each hit — typed via Roslyn → System.Linq.Expressions → `LambdaExpression.Compile()` so int/double/bool operands keep their CLR types), `hitCount` (fire only on Nth matching hit), `logMessage` (template like `"counter={counter} times2={counter*2}"` — `{expr}` fragments compile the same way, including typed arithmetic, and render via `Convert.ToString(InvariantCulture)`), `suspend` (`"all"` to stop, `"none"` for a non-stopping logpoint that auto-resumes after each emit). | `drhook_step_breakpoint` |
| `drhook_break_function` | Set function breakpoint by method name. Same optional policy parameters as `drhook_break_source`; both `condition` and `logMessage` see method arguments + locals at entry. | `drhook_step_break_function` |
| `drhook_break_exception` | Set exception breakpoint by `typeName` (full CLR name, or `"*"` wildcard). Optional `phase` (`"any"`/`"first-chance"`/`"user-first-chance"`/`"catch-handler-found"`/`"unhandled"`), `condition`, `hitCount`, `logMessage`, `suspend`. SUBCLASS-AWARE — a filter on a base type matches every subclass, including types defined in the target's own module (verified for target-defined hierarchies by probe 57). Multiple filters compose with OR. | `drhook_step_break_exception` |
| `drhook_break_list` | List all breakpoints (source + function + exception) with full descriptors per entry — `id`, location, `hits` running count, and `policy` (when attached — `condition`/`hitCount`/`logMessage`/`suspend` as the agent supplied). | `drhook_step_breakpoint_list` |
| `drhook_break_remove` | Remove a breakpoint or exception filter by its substrate-assigned `id`. Use `drhook_break_list` to discover ids; dispatches to the right substrate path automatically. | `drhook_step_breakpoint_remove` |
| `drhook_break_clear` | Clear all breakpoints, or by category (`source` / `function` / `exception`) | `drhook_step_breakpoint_clear` |

### Inspection

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_locals` | Inspect locals + arguments at current stop. Depth parameter (≥ 2) expands object fields and arrays (SZARRAY). **Top frame only** — frame selection comes via ADR-010 Tier 2 (`drhook_frames` / `drhook_locals(frame=N)`). | `drhook_step_vars` |

### Observation (no session required)

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_processes` | List .NET processes (EventPipe-driven enumeration) | *(unchanged)* |
| `drhook_snapshot` | Passive thread/stack snapshot of running process (EventPipe). Requires `hypothesis` parameter. | *(unchanged)* |

### Substrate diagnostics

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_drain_anomalies` | Drain the engine's structured-anomaly buffer (substrate-correctness signals the engine detected but didn't raise as exceptions — late mscordbi callbacks, depth-clamped inspections, unexpected HRESULTs, worker-thread exceptions). Substrate-grade observation that no IDE debugger exposes. | *(unchanged)* |
| `drhook_drain_console` | Drain a LAUNCHED debuggee's captured stdout/stderr (isolated to a DrHook pipe per D2) — chunks tagged Stdout/Stderr, newest-last, with a dropped count. Pull while stepping a console app to see what it printed. | *(new — ADR-011 D3)* |
| `drhook_drain_log` | Drain logpoint output — rendered `logMessage` templates from `suspend='none'` breakpoints (+ condition faults, `isFault`). Closes the gap where logpoint output was dropped at the MCP layer. | *(new — ADR-011 D3)* |

**Every step, continue, and pause response includes process metrics** — working set, private bytes, thread count, GC heap size, collection counts with deltas from the previous capture. No extra tool call needed.

## What's NOT yet shipped

Substrate work is required before these surfaces become functional:

- **Watch expressions.** Substrate has narrow func-eval for parameterless and single-int-arg static methods (`DebugSession.TryEvalStaticCall`, `TryEvalStaticCallInt`). General agent-driven Roslyn-based expression evaluation against locals/arguments/`this` outside breakpoint policies is not surfaced as its own MCP tool. ADR-010 Tier 3 (`drhook_watch`, finding F-010-3 — small, since Increment 7 shipped the typed `CSharpCondition` translator).
- **Call stack frame switching.** `GetStackFrames` returns frames as formatted strings; locals are read from the top frame only. Frame-selection state and rich frame records are ADR-010 Tier 2 (`drhook_frames`, verify) / Tier 3 (substrate).
- **Set next statement.** ICorDebug `SetIP` is not exposed at the substrate level. ADR-010 Tier 3.
- **Data breakpoints.** Not in the substrate today; ICorDebug support level is an Open Question per ADR-010 §Open. ADR-010 Tier 3.
- **Run to cursor.** Composable from existing primitives (`SetBreakpointAtLine` + `Resume` + remove-on-hit); not yet packaged as a tool. ADR-010 Tier 2.
- **Owned detach-and-leave-running** — `drhook_detach` on an Owned (`drhook_launch`) target is pending finding **F-010-2** (the launched target is currently the debugger's child); returns an error meanwhile. Use `drhook_stop` (graceful end) or `drhook_kill` (force).
- **Borrowed force-kill** — `drhook_kill` on a Borrowed (`drhook_attach`) target is pending finding **F-010-1** (the substrate doesn't own an attached target's lifecycle); returns an error meanwhile. Use `drhook_detach`.
- **Test-project launch.** `drhook_step_test` was removed in ADR-010 Tier 1 (it only returned "not implemented"). Replacement: ADR-010 Tier 3 lets `drhook_launch` accept a `.csproj` target and dispatches MTP / VSTest internally. Until then, attach to the testhost child with `drhook_attach`.
- **Multi-session.** `EngineSteppingSession` is a DI singleton; only one debug session per MCP server. Substrate's `DebugSession` is per-session; the singleton is the MCP-layer constraint. ADR-010 §Open Question 9.
- **Cross-platform.** Only macOS/arm64 is exercised. ADR-007 Phase 9 (Open).

## Workflow Example: Parser Buffer Bug

```
# 1. Write minimal repro as file-based app
tools/repro-parser-bug.cs

# 2. Build it to a DLL (do NOT use `dotnet run --file` — see Launch Requirements)
dotnet build tools/repro-parser-bug.cs

# 3a. Launch a NEW process under debugger control (Owned session):
drhook_launch: program=dotnet, args=["exec", "tools/bin/Debug/net10.0/repro.dll"],
               sourceFile="...", line=..., hypothesis="..."

# 3b. OR attach to an already-running process (Borrowed session):
drhook_processes              # find pid
drhook_attach: pid=<pid>, sourceFile="...", line=..., hypothesis="..."

# 4. Add more breakpoints at decision points (with optional policy)
drhook_break_source: file="src/Mercury/Turtle/Buffer.cs", line=17   # Peek
drhook_break_source: file="src/Mercury/Turtle/Buffer.cs", line=240, # FillBufferSync, only when buffer is near empty
                     condition="_bufferLength - _bufferPosition < 8"
drhook_break_source: file="src/Mercury/Turtle/Buffer.cs", line=240, # FillBufferSync, sample every 5th hit
                     hitCount=5

# 5. Run to breakpoint; inspect state
drhook_continue
drhook_locals                 # locals + arguments at the stop

# 6. Step through the refill logic
drhook_step_over              # execute current line without descending
drhook_locals                 # verify positions after shift
drhook_step_into              # follow into a method call
drhook_step_out               # return to caller

# 7. End session
drhook_stop                   # normal end — Owned: target gracefully terminated; Borrowed: target survives
```

## Launch Requirements

**Pre-build targets before launching.** `dotnet run --file` compiles before executing — when used with `drhook_launch`, the compilation step delays the attach window and the launched-suspended state expected by `RegisterForRuntimeStartup`.

```bash
# Build first
dotnet build path/to/Project.csproj -c Debug

# Then launch:
drhook_launch: program=dotnet, args=["exec", "path/to/bin/Debug/net10.0/Project.dll"], ...
```

## PoC probes and findings

The substrate is backed by a probe corpus in `poc/drhook-engine/` — `NN-name-smoke.cs` driver scripts plus optional `NN-name-target.cs` targets, each documenting one falsifiable epistemic act. Numbered ranges:

- **02–40**: pre-substrate-independence probes; baseline ICorDebug interop validation. (PASS at HEAD on macOS/arm64.)
- **41–46**: ADR-007 Phase 1 + Phase 2 substrate-correctness probes (anomaly injection, dispose-during-resume race, pause/stopping race, detach-exit race, worker exception, MTP / legacy-VSTest promotion meta-probes). Complete.
- **47**: external target death (ADR-007 Phase 1 extension; ADR-008 Layer-2 guard).
- **48 / 48b / 48c**: multi-session investigation (led to ADR-008 framing).
- **49–54**: ADR-008 Phase 0 process-lifecycle ground truth probes (signal disposition, ignoring targets, tight CPU loop, default disposition, process tree).
- **55**: substrate two-stage escalation (ADR-008 Increment 1).
- **56**: break-stopped Dispose (ADR-008 Increment 1b).
- **57**: target-defined exception hierarchy (ADR-010 Increment 6).
- **58**: logpoint template typed-arithmetic validation (ADR-010 Increment 7).

Probes call `DrHook.Engine.DebugSession` directly (not the MCP tool layer), so the ADR-010 Tier 1 rename does not affect them.

Run probes with `dotnet run --no-cache <probe-smoke.cs> <target>` from `poc/drhook-engine/`. `--no-cache` matters — file-based-app re-runs use a cached build by default, which can mask engine source edits ([feedback_filebased_app_stale_cache](.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_filebased_app_stale_cache.md)). Probes 02–06 need a live .NET PID; probes 07+ spawn their own target. `DBGSHIM_PATH` is not required on a machine that has built `DrHook.Engine` once — both the engine's `DbgShim.Resolve` and the probes' local `ResolveDbgShim` walk the per-RID NuGet cache automatically.

## ADR cross-references

- [ADR-006](docs/adrs/drhook/ADR-006-drhook-engine.md) — DrHook.Engine substrate; substrate-independence at 1.8.2. **Accepted.**
- [ADR-007](docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) — substrate-correctness arc. **Accepted.** Phase 1 Complete, Phase 2 Complete, Phase 3 Superseded (by ADR-009 → ADR-010), Phases 4-6 Closed (substrate proved runner-agnostic), Phase 7 (MCP surface cleanup) subsumed by ADR-010 Tier 1, Phase 8 Complete (12/12 CI), Phase 9 Open (cross-platform campaign).
- [ADR-008](docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) — natural-exit-by-default, explicit `Abandon` for forced termination; SIGTERM-then-SIGKILL escalation. **Completed.**
- [ADR-010](docs/adrs/drhook/ADR-010-mcp-tool-surface-redesign.md) — MCP tool surface redesign. **Accepted.** Tier 1 (this rename pass) shipped; Tier 2 (substrate-verification tools) and Tier 3 (substrate-addition tools) land in successor increments.

## Key Principle

DrHook closes the gap between "what the code says" and "what the code does." When those diverge, reading the code harder doesn't help — only observation does. This is the EEE methodology applied to debugging: move from Emergence (unknown unknowns) to Epistemics (observed knowns) before Engineering (fixes).
