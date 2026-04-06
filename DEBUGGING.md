# Debugging with DrHook

DrHook is Sky Omega's runtime observation substrate. Use it when you need to understand what code is actually doing — not what you think it's doing.

## When to Use DrHook

**Use DrHook when:**
- A bug persists after your first fix attempt — you're guessing, not observing
- The behavior depends on runtime state (buffer positions, loop iterations, async continuations)
- You need to verify an assumption about control flow or data at a specific point
- Increasing a constant or adding a workaround feels like the right fix — it never is
- You're about to change code you haven't observed running

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
| `drhook_step_run` | Launch a .NET executable under debugger control |
| `drhook_step_test` | Launch a specific test method under debugger |
| `drhook_step_breakpoint` | Set a source breakpoint (file:line), optionally conditional |
| `drhook_step_break_function` | Set a function breakpoint (method name) |
| `drhook_step_break_exception` | Break on exception type |
| `drhook_step_continue` | Continue execution until next breakpoint |
| `drhook_step_next` | Step over (next line) |
| `drhook_step_into` | Step into method call |
| `drhook_step_out` | Step out of current method |
| `drhook_step_vars` | Inspect variables in current scope |
| `drhook_step_pause` | Pause execution |
| `drhook_step_stop` | Stop debugging session |
| `drhook_step_breakpoint_list` | List all breakpoints |
| `drhook_step_breakpoint_remove` | Remove a breakpoint |
| `drhook_step_breakpoint_clear` | Clear all breakpoints |
| `drhook_processes` | List .NET processes |
| `drhook_snapshot` | Capture thread/stack snapshot of running process |

Every step, continue, and pause response includes **process metrics** — working set, private bytes, thread count, GC heap size, and collection counts with deltas from the previous capture. No extra tool call needed.

## Known Limitations (netcoredbg on macOS/ARM64)

**`drhook_step_eval` removed.** netcoredbg's DAP `evaluate` request hangs indefinitely — the func-eval machinery deadlocks. `drhook_step_vars` (which uses the scopes/variables DAP path, not evaluate) works reliably and is the inspection surface.

**Conditional breakpoints hang.** netcoredbg evaluates breakpoint conditions using the same func-eval path. Do not pass a `condition` parameter to `drhook_step_breakpoint`.

### Conditional Stopping Workarounds

Two patterns achieve conditional stopping without func-eval:

**Pattern 1 — Breakpoint inside an `if`:**
The condition lives in the target code. Set an unconditional breakpoint on a line inside the `if` body.

```csharp
for (var i = 0; i < count; i++)
{
    if (i == targetValue)
        Console.WriteLine("hit");  // ← set breakpoint here
}
```

**Pattern 2 — `Debugger.Break()`:**
The target breaks itself. No breakpoint needed.

```csharp
using System.Diagnostics;

for (var i = 0; i < count; i++)
{
    if (i == targetValue)
        Debugger.Break();  // ← triggers stopped event in DAP
}
```

Both patterns are validated by integration tests. Use `drhook_step_vars` after stopping to inspect state.

## Launch Requirements

**Always pre-build targets.** `dotnet run --file` compiles before executing — the compilation delay causes the MCP call to hang while waiting for the breakpoint hit.

```bash
# Build first
dotnet build path/to/Project.csproj -c Debug

# Then launch with dotnet exec
drhook_step_run: program=dotnet, args=["exec", "path/to/bin/Debug/net10.0/Project.dll"]
```

## Workflow Example: Parser Buffer Bug

```
# 1. Write minimal repro as file-based app
tools/repro-parser-bug.cs

# 2. Build it to a DLL (do NOT use dotnet run --file)
dotnet build tools/repro-parser-bug.cs

# 3. Launch under DrHook
drhook_step_run: dotnet exec repro.dll

# 4. Set breakpoints at decision points
drhook_step_breakpoint: TurtleStreamParser.Buffer.cs:17  (Peek method)
drhook_step_breakpoint: TurtleStreamParser.Buffer.cs:240 (FillBufferSync)

# 5. Continue to breakpoint, inspect state
drhook_step_continue
drhook_step_vars  → see _bufferPosition, _bufferLength, _endOfStream

# 6. Step through the refill logic
drhook_step_next  → observe buffer shift
drhook_step_vars  → verify positions after shift
```

## Key Principle

DrHook closes the gap between "what the code says" and "what the code does." When those diverge, reading the code harder doesn't help — only observation does. This is the EEE methodology applied to debugging: move from Emergence (unknown unknowns) to Epistemics (observed knowns) before Engineering (fixes).
