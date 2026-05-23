# Debugging with DrHook

DrHook is Sky Omega's runtime observation substrate. Use it when you need to understand what code is actually doing — not what you think it's doing.

As of 1.8.2 the substrate is `DrHook.Engine` — a BCL + P/Invoke + source-gen COM implementation of `ICorDebug` interop, with `libdbgshim` bundled per-RID via NuGet. `netcoredbg` is no longer used anywhere; the previous DAP-over-stdio path is retired.

> **Production-suitability note (2026-05-23):** the substrate-independence bar is reached (ADR-006 Phase 3 complete). The remaining production-suitability work — teardown + concurrency hardening, test-runner debugging substrate, integration-test mechanism, cross-platform validation — is sequenced under [ADR-007](docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) (Proposed). Until ADR-007 closes, treat DrHook as substrate-grade for primary developer-debug scenarios on macOS/arm64; use with care under test runners (especially NCrunch) and on other platforms.

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
3. **Step through and record state.** Use `drhook_step_vars` at each breakpoint to capture the actual values. Compare what you see with what you expected.
4. **Identify the invariant violation.** The bug is where reality diverges from your mental model. Once you see the actual state, the fix is usually obvious.
5. **Fix and verify.** Change the code, re-run with the same breakpoints to confirm the fix.

## DrHook MCP Tools

| Tool | Purpose |
|------|---------|
| `drhook_step_run` | Attach to an already-running .NET process and run to an initial breakpoint |
| `drhook_step_launch` | Launch a .NET executable under debugger control (attach before main runs, via `dbgshim` `RegisterForRuntimeStartup`) |
| `drhook_step_breakpoint` | Set a source breakpoint (file:line) — conditions supported via the Roslyn-walker + ICorDebug func-eval substrate |
| `drhook_step_break_function` | Set a function breakpoint (method name) |
| `drhook_step_break_exception` | Break on exception type — supports subclass-aware filtering (`System.Exception` matches any subclass via `ICorDebugType.GetBase` chain walk) |
| `drhook_step_continue` | Continue execution until next breakpoint |
| `drhook_step_next` | Step over (next line) |
| `drhook_step_into` | Step into method call |
| `drhook_step_out` | Step out of current method |
| `drhook_step_vars` | Inspect variables in current scope — primitives + strings + objects (depth ≥ 2 field expansion) + arrays (SZARRAY, depth ≥ 2) |
| `drhook_step_pause` | Pause execution via `ICorDebugController.Stop` |
| `drhook_step_stop` | Stop debugging session |
| `drhook_step_breakpoint_list` | List all breakpoints |
| `drhook_step_breakpoint_remove` | Remove a breakpoint |
| `drhook_step_breakpoint_clear` | Clear all breakpoints |
| `drhook_processes` | List .NET processes (EventPipe-driven) |
| `drhook_snapshot` | Capture thread/stack snapshot of running process (EventPipe-driven) |

Every step, continue, and pause response includes **process metrics** — working set, private bytes, thread count, GC heap size, and collection counts with deltas from the previous capture. No extra tool call needed.

### Conditional breakpoints

Pass a Roslyn-parsed C# expression as the `condition` parameter. Supported operand classes:
- Primitive locals: `value == 3`, `s.Length > 3`
- Member access on values: `box.Size == 42`, `s.Length == 5` (getter func-eval'd on the runtime type)
- Exception members at exception stops: `ex.Code == 42`

The condition runs via ICorDebug `ICorDebugEval` func-eval at the stop. Faulting conditions surface as `StopReason.ConditionError` + a `LogRecord` with `IsFault: true` rather than silently false.

### Logpoints (non-stopping breakpoints)

A breakpoint configured with a `LogMessage` action emits a structured `LogRecord` to a `BoundedLogSink` and continues without halting. Interpolated `{expr}` fragments in the log message are evaluated via the same Roslyn walker. Hit-count gates (`Equals(N)` / `AtLeast(N)` / `Multiple(N)`) can sample the stream — `Equals(3)` against a fast-hitting breakpoint yields exactly one log line in the window.

### What's NOT yet available

- `drhook_step_test` — wired but engine path returns "not yet ported." ADR-007 Phase 7 removes the tool; ADR-007 Phase 3 + Phase 4 build the substrate-aligned replacement (child-process attach for `dotnet test` → testhost; no `VSTEST_HOST_DEBUG` trick).
- Multi-session — `EngineSteppingSession` is a singleton; only one debugging session at a time. ADR-007 Phase 5 builds multi-session if scope decisions require it (NCrunch, parallel testhost).
- Cross-platform validation — only macOS/arm64 has been exercised. Per-platform validation campaign is ADR-007 Phase 9.

## Workflow Example: Parser Buffer Bug

```
# 1. Write minimal repro as file-based app
tools/repro-parser-bug.cs

# 2. Build it to a DLL (do NOT use dotnet run --file, see Launch Requirements below)
dotnet build tools/repro-parser-bug.cs

# 3. Launch under DrHook (attach-before-main via DebugSession.Launch)
drhook_step_launch: program=dotnet, args=["exec", "tools/bin/Debug/net10.0/repro.dll"]

# Or attach to an already-running process:
drhook_processes  → find pid
drhook_step_run: pid=<pid>, sourceFile=..., line=...

# 4. Set breakpoints at decision points
drhook_step_breakpoint: TurtleStreamParser.Buffer.cs:17  (Peek method)
drhook_step_breakpoint: TurtleStreamParser.Buffer.cs:240 (FillBufferSync)

# 5. Continue to breakpoint, inspect state
drhook_step_continue
drhook_step_vars  → see _bufferPosition, _bufferLength, _endOfStream (primitives + reference fields)

# 6. Step through the refill logic
drhook_step_next  → observe buffer shift
drhook_step_vars  → verify positions after shift
```

## Launch Requirements

**Pre-build targets.** `dotnet run --file` compiles before executing — for `drhook_step_launch`, the compilation step delays the attach window and the launched-suspended state expected by `RegisterForRuntimeStartup`.

```bash
# Build first
dotnet build path/to/Project.csproj -c Debug

# Then launch:
drhook_step_launch: program=dotnet, args=["exec", "path/to/bin/Debug/net10.0/Project.dll"]
```

## PoC probes and findings

The substrate is backed by 40 PoC probes (`poc/drhook-engine/*-smoke.cs` + `*-target.cs`) numbered 02–40, each documenting one falsifiable epistemic act against macOS/arm64 CoreCLR. 52 finding docs (`poc/drhook-engine/findings/`) record the outcomes. The fixture archive (`poc/drhook-engine/fixtures/`) carries per-run probe evidence with timestamps and per-RID labels.

Run them with `dotnet <probe-smoke>.cs <target>` from `poc/drhook-engine/`. Probes 02–06 need a live .NET PID; probes 07–40 spawn their own target. `DBGSHIM_PATH` is no longer required on a stock machine that has built `DrHook.Engine` once — both the engine's `DbgShim.Resolve` and the probes' local `ResolveDbgShim` walk the per-RID NuGet cache automatically.

## Key Principle

DrHook closes the gap between "what the code says" and "what the code does." When those diverge, reading the code harder doesn't help — only observation does. This is the EEE methodology applied to debugging: move from Emergence (unknown unknowns) to Epistemics (observed knowns) before Engineering (fixes).
