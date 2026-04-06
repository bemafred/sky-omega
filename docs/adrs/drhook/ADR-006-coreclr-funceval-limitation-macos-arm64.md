# ADR-006 — CoreCLR FuncEval Limitation on macOS ARM64

## Status

**Status:** Accepted — 2026-04-06

## Context

### The Observed Failure

On macOS ARM64 (Apple Silicon — M1 through M5), netcoredbg's DAP `evaluate` request hangs indefinitely. The debugger's internal 15-second command timeout and 5-second eval timeout never fire. This blocks:

- Expression evaluation (`drhook_step_eval`)
- Conditional breakpoints (`setBreakpoints` with `condition`)
- Watch expressions (`evaluate` with `context: "watch"`)
- Process metrics via managed eval (`GC.GetTotalMemory`, `GC.CollectionCount`)

Variable inspection via the `scopes`/`variables` DAP path works correctly — the hang is isolated to the `evaluate` request, which routes through CoreCLR's `ICorDebugEval` func-eval machinery.

### Root Cause: CoreCLR, Not netcoredbg

Investigation of the netcoredbg source code confirms that the expression evaluation pipeline is **100% platform-agnostic**. There are no `#ifdef DARWIN`, `#ifdef ARM64`, or any platform-conditional code in the eval system (`evaluator.cpp`, `evalstackmachine.cpp`, `evalhelpers.cpp`, `evalwaiter.cpp`). The evaluation flow is:

```
Expression → EvalStackMachine → Evaluator → ICorDebugEval (CoreCLR) → EvalWaiter
```

The failure is in CoreCLR's `ICorDebugEval` implementation on non-Windows platforms. Three open issues in `dotnet/runtime` document this:

#### 1. Thread Suspension Relies on GC Polling Only — [dotnet/runtime#33561](https://github.com/dotnet/runtime/issues/33561)

**Status:** Open since March 2020. Milestoned "Future" — no implementation plan.

On Windows, func-eval uses `SuspendThread`/`ResumeThread` to hijack the debuggee thread and redirect execution to the evaluation thunk. On macOS and Linux, these OS primitives either don't exist or aren't compatible (`mach_thread_suspend`/`mach_thread_resume` on macOS has different semantics). The runtime can only suspend threads at GC poll points.

If the debuggee thread is stopped in native code, optimized code, a tight loop, or at a GC-unsafe point, **func-eval cannot begin**. This is the architectural root cause — it's a design limitation of CoreCLR's debugging infrastructure on Unix.

#### 2. ICorDebugEval::Abort Is Broken Since .NET 7 — [dotnet/runtime#82422](https://github.com/dotnet/runtime/issues/82422)

**Status:** Open since February 2023. Filed by the **netcoredbg team** (Samsung).

`ICorDebugEval::Abort()` and `ICorDebugEval2::RudeAbort()` return `S_OK` but do not actually abort the evaluation. The evaluated code continues running. If the code never terminates (deadlock, infinite loop), `EvalComplete`/`EvalException` callbacks never fire. This is a regression from .NET 6 — abort worked previously. Although titled "Linux amd64," the mechanism is platform-agnostic and affects macOS ARM64.

This explains why netcoredbg's timeout logic in `evalwaiter.cpp` never fires: the abort call "succeeds" but the evaluation silently continues, and the completion callback never arrives.

#### 3. Apple W^X / JIT Restrictions Affect Eval Thunks — [dotnet/runtime#108423](https://github.com/dotnet/runtime/issues/108423)

**Status:** Open since October 2024.

Func-eval needs to allocate executable memory for evaluation thunks. Apple's increasingly restrictive memory protection policies (`MAP_JIT`, `pthread_jit_write_protect_np`) complicate this on Apple Silicon. On iOS, `VirtualAlloc` with `PAGE_EXECUTE_READWRITE` returns `NULL` entirely ([dotnet/runtime#125959](https://github.com/dotnet/runtime/issues/125959)). On macOS, the situation is less severe but the JIT write protection toggle adds latency and failure modes.

Active work on interpreter-based func-eval ([dotnet/runtime#126576](https://github.com/dotnet/runtime/pull/126576)) is the .NET team's forward path for Apple platforms, but targets iOS first.

### Additional Contributing Factors

| Issue | Impact |
|-------|--------|
| [dotnet/runtime#126393](https://github.com/dotnet/runtime/issues/126393) | `DbgTransportSession` shutdown races on non-Windows (Open, April 2026) |
| [dotnet/runtime#126096](https://github.com/dotnet/runtime/issues/126096) | Runtime deadlock during debugging on .NET 10 (Open, March 2026) |
| [dotnet/runtime#125777](https://github.com/dotnet/runtime/issues/125777) | Missing `DEBUG_EXCEPTION_CATCH_HANDLER_FOUND` events during func-eval — .NET 10 regression |
| [dotnet/runtime#94114](https://github.com/dotnet/runtime/issues/94114) | Debugging doesn't work with macOS sandbox enabled |
| [dotnet/runtime#125484](https://github.com/dotnet/runtime/issues/125484) | SIGSEGV under debugger on macOS 26 — signal handlers reset by OS security |

### netcoredbg macOS ARM64 Status

From `README.md`: *"the MacOS arm64 build (M1) is community supported and may not work as expected."*

Samsung has no Apple Silicon hardware in CI. There are no official `osx-arm64` release binaries ([Samsung/netcoredbg#174](https://github.com/Samsung/netcoredbg/issues/174), still open). The build compiles and basic debugging (breakpoints, stepping, variable inspection) works, but anything touching `ICorDebugEval` — expression evaluation, conditional breakpoints, watch expressions — is at the mercy of CoreCLR's broken func-eval on Unix.

### What This Means for DrHook

DrHook's `drhook_step_eval` and conditional breakpoint features depend on func-eval. Since the failure is in CoreCLR (not netcoredbg or DrHook), it cannot be fixed at the DrHook or netcoredbg layer. The .NET team's own fix path (interpreter-based func-eval) has no timeline for macOS.

DrHook must work around the absence of func-eval entirely.

## Decision

**Accept that func-eval is unavailable on macOS ARM64 and design workarounds that use only the reliable DAP paths (`scopes`/`variables`, `setBreakpoints` without conditions, stepping).**

### Workaround 1: Conditional Breakpoints as Source-Level If-Statements

Instead of setting a DAP conditional breakpoint:

```json
{
  "command": "setBreakpoints",
  "arguments": {
    "breakpoints": [{ "line": 42, "condition": "count > 1000" }]
  }
}
```

Instrument the source code with an explicit if-statement and place an unconditional breakpoint inside the body:

```csharp
// Line 42: the code being investigated
ProcessTriple(triple);

// Instrumentation — temporary diagnostic if-statement
if (count > 1000)
{
    _ = count; // DIAG: breakpoint target
}
```

The agent sets an unconditional breakpoint on the assignment line inside the if-body. The runtime evaluates the condition as normal managed code — no func-eval required. The breakpoint only triggers when the condition is true.

**Advantages over DAP conditional breakpoints:**

- Works reliably on all platforms regardless of func-eval status
- Arbitrary condition complexity — lambdas, LINQ, async, method calls all work (they're just C# code)
- The condition is visible in the source diff, making it reviewable
- No 5-second eval timeout risk on complex conditions

**Guidelines for the agent:**

- Add instrumentation with a clear comment marker (`// DIAG:`)
- Use `_ = expr;` as the breakpoint target — a discard assignment that the compiler won't optimize away in Debug builds
- Remove instrumentation after the diagnostic session
- Prefer placing the if-block immediately after the line of interest, not inside hot inner loops (move it to an outer scope if the condition doesn't depend on the inner variable)

### Workaround 2: Debugger.Break() for Programmatic Breakpoints

Use `System.Diagnostics.Debugger.Break()` for breakpoints that should fire when a specific runtime condition is met:

```csharp
if (count > 1000 && buffer.Length > maxExpected)
{
    System.Diagnostics.Debugger.Break(); // Triggers debugger stop
}
```

`Debugger.Break()` is a managed call that triggers a debug break event in the CLR — it does not use func-eval. The debugger stops at this line exactly as if a breakpoint were set there.

**When to prefer `Debugger.Break()` over an if-statement with a breakpoint:**

- When the condition is complex and benefits from being self-documenting code
- When the diagnostic should survive across multiple debug sessions without re-setting breakpoints
- When the break should fire regardless of whether a debugger is attached (it's a no-op without a debugger unless `Debugger.Launch()` is used)

**Additional `System.Diagnostics` tools:**

| API | Purpose |
|-----|---------|
| `Debugger.Break()` | Programmatic breakpoint — stops the debugger at this line |
| `Debugger.IsAttached` | Guard diagnostic code so it only runs under a debugger |
| `Debugger.Log(level, category, message)` | Send messages to the debugger's output window |
| `Debug.Assert(condition)` | Break on assertion failure (Debug builds only) |
| `Debug.WriteLineIf(condition, message)` | Conditional diagnostic output |

### Workaround 3: Variable Inspection Instead of Expression Evaluation

For watch-like needs, assign the expression result to a local variable and inspect it via `drhook_step_vars`:

```csharp
// Instead of evaluating "people.Count > 2 && people[0].Name.StartsWith("A")"
// in a watch expression, compute it in code:
var diagnostic_check = people.Count > 2 && people[0].Name.StartsWith("A"); // DIAG:
```

The agent can then read `diagnostic_check` through the `scopes`/`variables` path, which works reliably.

### Impact on DrHook MCP Tools

| Tool | Status | Notes |
|------|--------|-------|
| `drhook_step_vars` | Works | Uses `scopes`/`variables` — no func-eval |
| `drhook_step_eval` | Removed (ADR-002 amendment) | Blocked by func-eval hang |
| `drhook_step_watch_*` | Not implemented (ADR-005 superseded) | Would require func-eval |
| `drhook_step_breakpoint` | Works (unconditional only) | Conditional breakpoints require func-eval |
| `drhook_step_next/into/out` | Works | Stepping does not use func-eval |
| `drhook_step_run/stop` | Works | Session lifecycle is unaffected |
| `drhook_snapshot` | Works | Uses EventPipe, not func-eval |

### Long-Term Path

DrHook.Engine — a native .NET debugger engine replacing netcoredbg, owned by Sky Omega. This would allow DrHook to implement expression evaluation without depending on CoreCLR's `ICorDebugEval`, potentially using Roslyn scripting or a custom interpreter operating on the inspected process's memory via the DAC (Data Access Component).

Until then, the workarounds documented here are the supported path. They are not inferior hacks — source-level conditions are more expressive than func-eval conditions and have zero runtime overhead beyond the condition check itself.

## Consequences

### Positive

- DrHook has a clear, documented path for conditional debugging on macOS ARM64
- Source-level conditions are more powerful than DAP conditions (full C# expressiveness)
- No dependency on CoreCLR func-eval fixes with no timeline
- Workarounds are portable — they work identically on all platforms
- `Debugger.Break()` is a well-known .NET pattern, not a DrHook-specific workaround

### Trade-offs

- Requires source modification for conditional breakpoints — the agent must add and later remove instrumentation code
- Variable watches require assigning to locals — adds temporary variables to the source
- The agent must be instructed to use these patterns instead of attempting DAP-level conditions

### What Changes for the Agent

The agent (Claude Code using DrHook MCP tools) should:

1. Never attempt `evaluate` DAP requests on macOS ARM64
2. For conditional stops: add if-statements with `Debugger.Break()` or unconditional breakpoints inside the if-body
3. For value inspection: use `drhook_step_vars` exclusively; if a computed expression is needed, assign it to a diagnostic local
4. Clean up all `// DIAG:` instrumentation after the debugging session

## References

- [ADR-002 — Expression Evaluation](ADR-002-expression-evaluation.md) — original eval specification and hang amendment
- [ADR-005 — Inspection Surface](ADR-005-inspection-surface.md) — superseded; watch mode and metrics depended on func-eval
- [dotnet/runtime#33561](https://github.com/dotnet/runtime/issues/33561) — thread suspension via activation injection (architectural root cause)
- [dotnet/runtime#82422](https://github.com/dotnet/runtime/issues/82422) — ICorDebugEval::Abort broken since .NET 7 (filed by Samsung/netcoredbg)
- [dotnet/runtime#108423](https://github.com/dotnet/runtime/issues/108423) — Apple JIT API investigation
- [dotnet/runtime#125959](https://github.com/dotnet/runtime/issues/125959) — func-eval fails on iOS (executable memory allocation)
- [dotnet/runtime#126576](https://github.com/dotnet/runtime/pull/126576) — interpreter-based func-eval (in progress, iOS-first)
- [Samsung/netcoredbg#174](https://github.com/Samsung/netcoredbg/issues/174) — no official osx-arm64 binaries
- [Samsung/netcoredbg README](https://github.com/Samsung/netcoredbg) — "community supported" macOS ARM64 status
- [ICorDebugEval Interface — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugeval-interface)
