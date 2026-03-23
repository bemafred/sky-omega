# ADR-001 - Breakpoint Registry

## Status
Proposed (2026-03-23)

## Context

DAP (Debug Adapter Protocol) uses **full replacement semantics** for breakpoints. Each `setBreakpoints` request sends the complete list of breakpoints for a given source file — the debug engine replaces all existing breakpoints in that file with the new list. The same applies to `setFunctionBreakpoints` (replaces all function breakpoints globally) and `setExceptionBreakpoints` (replaces all exception filters).

This means that in the current DrHook implementation:

```
1. drhook_step_breakpoint(file="app.cs", line=10)  → DAP: setBreakpoints [line 10]
2. drhook_step_breakpoint(file="app.cs", line=20)  → DAP: setBreakpoints [line 20]
   ← line 10 breakpoint is silently removed
```

The same behavior applies to function breakpoints:

```
1. drhook_step_break_function("Fibonacci")     → DAP: setFunctionBreakpoints [Fibonacci]
2. drhook_step_break_function("SlowCountAsync") → DAP: setFunctionBreakpoints [SlowCountAsync]
   ← Fibonacci breakpoint is silently removed
```

The current code acknowledges this with warning notes in `SteppingSessionManager` (lines 309, 341) and tool descriptions in `DrHookTools.cs`, but does not solve it.

### Impact on AI Agent Workflow

An AI coding agent using DrHook will naturally want to:
- Set breakpoints in multiple locations before continuing
- Add a breakpoint mid-session without losing existing ones
- Remove a specific breakpoint while keeping others

The current set-and-replace behavior is unintuitive and destructive. The agent must remember what breakpoints exist and re-send the full list each time — work that belongs in the tool, not the agent.

## Decision

Add a `BreakpointRegistry` to `SteppingSessionManager` that tracks all active breakpoints and sends the complete set to DAP on every mutation.

### Data Model

```csharp
// Per-file source breakpoints
private readonly Dictionary<string, Dictionary<int, SourceBreakpoint>> _sourceBreakpoints = new();

// Global function breakpoints
private readonly Dictionary<string, FunctionBreakpoint> _functionBreakpoints = new();

// Active exception filters
private readonly HashSet<string> _exceptionFilters = new();

record SourceBreakpoint(int Line, string? Condition);
record FunctionBreakpoint(string Name, string? Condition);
```

### Mutation API

Each mutation modifies the registry and then sends the full state to DAP:

| Operation | Registry Change | DAP Request |
|-----------|----------------|-------------|
| Add source breakpoint | Add to `_sourceBreakpoints[file]` | `setBreakpoints` with all breakpoints for that file |
| Remove source breakpoint | Remove from `_sourceBreakpoints[file]` | `setBreakpoints` with remaining breakpoints for that file |
| Clear source breakpoints | Clear `_sourceBreakpoints[file]` | `setBreakpoints` with empty array |
| Add function breakpoint | Add to `_functionBreakpoints` | `setFunctionBreakpoints` with all function breakpoints |
| Remove function breakpoint | Remove from `_functionBreakpoints` | `setFunctionBreakpoints` with remaining |
| Set exception filter | Add to `_exceptionFilters` | `setExceptionBreakpoints` with all filters |
| Clear exception filter | Remove from `_exceptionFilters` | `setExceptionBreakpoints` with remaining |

### MCP Tool Changes

**Existing tools — behavior change (non-breaking):**

- `drhook_step_breakpoint` — adds to registry instead of replacing. Remove the "WARNING: set-and-replace" note.
- `drhook_step_break_function` — adds to registry instead of replacing.
- `drhook_step_break_exception` — adds to filter set instead of replacing.

**New tools:**

| Tool | Purpose |
|------|---------|
| `drhook_step_breakpoint_remove` | Remove a source breakpoint by file + line |
| `drhook_step_breakpoint_list` | List all active breakpoints (source, function, exception) |
| `drhook_step_breakpoint_clear` | Clear all breakpoints (or by category) |

<!-- QUESTION: Should breakpoint removal be separate tools, or should the existing
     drhook_step_breakpoint gain an "action" parameter ("add"/"remove"/"list")?
     Separate tools follow the current pattern (one tool per operation) and are more
     discoverable. A unified tool with an action parameter is more compact but less
     obvious to an LLM agent. -->

### DapClient Changes

`DapClient.SetBreakpointAsync` needs an overload accepting multiple breakpoints:

```csharp
public async Task<JsonObject> SetBreakpointsAsync(
    string sourceFile,
    IReadOnlyList<SourceBreakpoint> breakpoints,
    CancellationToken ct)
```

Similarly, `SetFunctionBreakpointsAsync` needs a list overload:

```csharp
public async Task<JsonObject> SetFunctionBreakpointsAsync(
    IReadOnlyList<FunctionBreakpoint> breakpoints,
    CancellationToken ct)
```

The existing single-breakpoint overloads can be removed once the registry is in place — they are inherently set-and-replace and should not be used directly.

### Session Lifecycle

The registry is cleared in `CleanupAsync()` alongside the other session state (`_stepCount`, `_sessionHypothesis`, etc.). Breakpoints do not survive across sessions — this is intentional, since each `LaunchAsync` attaches to a potentially different process.

<!-- QUESTION: Should breakpoints set during step-launch (the initial breakpoint)
     be added to the registry? Currently, step-launch sets one breakpoint via DAP
     directly as part of the attach sequence. If it's in the registry, subsequent
     drhook_step_breakpoint calls won't accidentally remove it. I believe yes — the
     initial breakpoint should seed the registry. -->

## Implementation Plan

### Phase 1: Registry Infrastructure
- Add `SourceBreakpoint`, `FunctionBreakpoint` records to `SteppingSessionManager`
- Add registry dictionaries
- Add internal `SyncBreakpointsAsync(file)` and `SyncFunctionBreakpointsAsync()` methods
- Seed registry from `LaunchAsync` initial breakpoint

### Phase 2: Modify Existing Tools
- `SetBreakpointAsync` → add to registry + sync
- `SetFunctionBreakpointAsync` → add to registry + sync
- `SetExceptionBreakpointAsync` → add to filter set + sync
- Remove warning notes from tool descriptions

### Phase 3: New Tools
- `drhook_step_breakpoint_remove`
- `drhook_step_breakpoint_list`
- `drhook_step_breakpoint_clear`

### Phase 4: Tests
- Registry add/remove/list
- Multi-breakpoint per file
- Clear behavior
- Session cleanup clears registry

## Consequences

### Positive
- AI agents can set multiple breakpoints naturally without worrying about replacement
- Breakpoint state is queryable (`drhook_step_breakpoint_list`)
- Reduces cognitive load on the agent — the tool manages DAP complexity

### Trade-offs
- Registry is in-memory only — not persisted across sessions (intentional)
- Registry and DAP can diverge if DAP silently fails to verify a breakpoint (mitigated by checking `verified` field in response)
- Three new MCP tools increase the tool surface

<!-- QUESTION: Should we track the DAP `verified` flag per breakpoint? A breakpoint
     might be set but not verified (e.g., source file not loaded, optimized code).
     The registry could distinguish between "requested" and "verified" breakpoints,
     and drhook_step_breakpoint_list could show the verification status. This adds
     value for the agent but complicates the registry. -->

<!-- QUESTION: Should breakpoint hit counts be tracked? DAP's `hitCondition` field
     allows "break after N hits" but is separate from conditional breakpoints.
     We could add an optional hitCount parameter to drhook_step_breakpoint. This is
     orthogonal to the registry itself but would be natural to add at the same time. -->

## References
- [DAP Specification — setBreakpoints](https://microsoft.github.io/debug-adapter-protocol/specification#Requests_SetBreakpoints)
- [DAP Specification — setFunctionBreakpoints](https://microsoft.github.io/debug-adapter-protocol/specification#Requests_SetFunctionBreakpoints)
- [DAP Specification — setExceptionBreakpoints](https://microsoft.github.io/debug-adapter-protocol/specification#Requests_SetExceptionBreakpoints)
- [ADR-004 (top-level)](../ADR-004-drhook-runtime-observation-substrate.md) — DrHook intent
