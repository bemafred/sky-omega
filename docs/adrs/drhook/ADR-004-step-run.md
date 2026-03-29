# ADR-004: Process-Owning Stepping via DAP Launch

**Status:** Proposed
**Date:** 2026-03-29
**Context:** Emergence observation during ad-hoc Sky Omega MVP evaluation

## Problem

DrHook's current stepping flow requires a pre-existing .NET process:

1. User starts the target process externally
2. `drhook_step_launch` attaches to the PID, sets breakpoint, continues, waits for hit

This creates three problems:

1. **Race condition:** The process may execute past the breakpoint before DrHook attaches.
2. **MCP timeout:** `step_launch` blocks until the breakpoint is hit. If the target is sleeping or the breakpoint is in code reached later, the MCP call times out before the breakpoint fires.
3. **Claude Code limitation:** Claude Code cannot maintain background processes across Bash tool calls (each invocation is a fresh shell). File-based gates and signal protocols create chicken-and-egg timing problems.

These were discovered empirically on 2026-03-29 when attempting to use DrHook to debug VGR.Demo.Domain test failures. The observation layer (processes, snapshot) works correctly. The stepping layer cannot be used because the target process lifecycle is uncontrolled.

## Decision

Add a DAP `launch` capability alongside the existing `attach`, and expose it as a new MCP tool `drhook_step_run`.

### DAP Launch vs Attach

The Debug Adapter Protocol defines two modes:

| Mode | Who starts the process | Race condition | DrHook today |
|------|----------------------|----------------|--------------|
| `attach` | External (user, script) | Yes — process runs before debugger connects | Used |
| `launch` | Debug adapter (netcoredbg) | No — process starts under debugger control | **New** |

The `launch` request accepts:

```json
{
  "program": "/usr/local/share/dotnet/dotnet",
  "args": ["test", "--filter", "Innehåller", "--no-build", "/path/to/project"],
  "cwd": "/path/to/working/dir",
  "stopAtEntry": true
}
```

With `stopAtEntry: true`, the process is created but paused before any user code executes. Breakpoints can be set while the process is stopped. Then `configurationDone` + `continue` runs to the first breakpoint.

### New MCP Tool: `drhook_step_run`

```
drhook_step_run(
  program: string,       -- Executable path (e.g. "dotnet")
  args: string[],        -- Arguments (e.g. ["test", "--filter", "..."])
  sourceFile: string,    -- Source file for initial breakpoint
  line: int,             -- Line number for initial breakpoint
  hypothesis: string     -- What you expect to observe
)
```

The tool:
1. Launches netcoredbg
2. Sends DAP `launch` with `stopAtEntry: true`
3. Sets breakpoint at `sourceFile:line`
4. Sends `configurationDone`
5. Sends `continue` — process runs to breakpoint
6. Waits for `stopped` event
7. Returns state (location, variables summary)

After this, all existing stepping tools (`step_next`, `step_into`, `step_vars`, etc.) work as before.

## Implementation

### DapClient changes

Add one method:

```csharp
public async Task LaunchTargetAsync(string program, string[] args, string? cwd, bool stopAtEntry, CancellationToken ct)
{
    await SendRequestAsync("launch", new JsonObject
    {
        ["program"] = program,
        ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["cwd"] = cwd ?? Environment.CurrentDirectory,
        ["stopAtEntry"] = stopAtEntry,
    }, ct);
}
```

### SteppingSessionManager changes

Add `RunAsync` method parallel to existing `LaunchAsync`:

```csharp
public async Task<string> RunAsync(
    string program, string[] args, string? cwd,
    string sourceFile, int line, string hypothesis,
    CancellationToken ct)
```

This follows the same flow as `LaunchAsync` but calls `LaunchTargetAsync` instead of `AttachAsync`. The process lifecycle is owned by netcoredbg (and transitively by DrHook).

### DrHook.Mcp changes

Register one new tool: `drhook_step_run`.

## Unknowns to Validate

1. **`dotnet test` child processes:** `dotnet test` spawns `testhost.dll` as a child process. Does netcoredbg's `launch` follow into child processes, or only debug the parent `dotnet` process? If parent-only, we may need to launch the test host directly.

2. **`stopAtEntry` behavior:** Does it pause before `Main`, before the runtime initializes, or at the first managed instruction? This affects where initial breakpoints can be set.

3. **File-based apps:** `dotnet script.cs` compiles then executes. Does `launch` handle the compilation phase transparently?

4. **Process cleanup:** When DrHook disconnects, does netcoredbg terminate the launched process? The `disconnect` request has a `terminateDebuggee` parameter — current code sends `false`. For `launch` mode we likely want `true`.

## Success Criteria

- [x] `drhook_step_run` launches a simple .NET console app and stops at entry — **verified 2026-03-29** with `dotnet exec Mercury.Examples.dll demo`, stopped at `Program.Main` line 7
- [x] Breakpoints set after launch are hit — **verified** breakpoint at line 7 hit correctly
- [x] All existing step tools work after `step_run` (next, into, out, vars, continue) — **verified** step-next, step-vars both work, command="demo" observed
- [ ] `drhook_step_run` with `dotnet test --filter ...` reaches test code (validates child process behavior)
- [x] `step_stop` terminates the launched process cleanly — **verified**
- [x] MCP call completes within timeout (no blocking on long sleeps) — **verified**, response in seconds

**Note:** File-based apps (`dotnet script.cs`) timeout — compilation delay before DAP events flow. Use pre-built executables or `dotnet exec`.

## Falsification Criteria

- If netcoredbg's `launch` cannot debug `dotnet test` child processes, the tool is limited to standalone executables — still valuable but doesn't solve the test runner problem. Fallback: launch `testhost.dll` directly.
- If `stopAtEntry` doesn't reliably pause before user code, breakpoints may be missed — same race condition as `attach`. Fallback: use function breakpoints on known entry points.
