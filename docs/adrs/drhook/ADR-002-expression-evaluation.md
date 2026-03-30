# ADR-002 - Expression Evaluation

## Status
Accepted (2026-03-30) — implemented in all three phases

## Context

DrHook's current variable inspection (`drhook_step_vars`) traverses the scope tree: stack frame → scopes → variables → recursive expansion. This works for inspecting what's already bound, but cannot evaluate arbitrary expressions.

DAP provides an `evaluate` request that evaluates expressions in the context of a stopped frame:

```json
{
  "command": "evaluate",
  "arguments": {
    "expression": "people.Count > 2 && people[0].Name.StartsWith(\"A\")",
    "frameId": 123,
    "context": "watch"
  }
}
```

The response returns the evaluated result, its type, and a `variablesReference` for further expansion if the result is a complex object.

### What This Enables

| Use Case | Example | Current Capability |
|----------|---------|-------------------|
| Property access | `myList.Count` | Must traverse entire list object to find Count |
| Array indexing | `items[5]` | Must expand full array, navigate to index 5 |
| Method calls | `path.EndsWith(".cs")` | Not possible |
| Computed values | `x * 2 + offset` | Not possible |
| Boolean checks | `IsValid && Count > 0` | Not possible |
| Type casts | `(HttpClient)service` | Not possible |
| LINQ (limited) | `list.Where(x => x > 5).Count()` | Not possible |

The gap between "inspect bound variables" and "evaluate expressions" is significant for an AI agent that is forming and testing hypotheses about runtime state.

### netcoredbg Support

netcoredbg supports the `evaluate` request. Expression evaluation is performed by the debug engine using the .NET runtime's expression evaluator. This means C# expressions (including method calls, property access, LINQ) work — within the limitations of the debugger's expression evaluator.

**Known limitations of netcoredbg evaluation:**
- No `await` expressions (debugger can't resume async)
- No lambda creation (JIT limitation in debug context)
- Limited generic method invocation
- Some LINQ expressions may fail depending on complexity

## Decision

Add expression evaluation to DrHook as a new DAP method and MCP tool.

### DapClient Addition

```csharp
public async Task<JsonObject> EvaluateAsync(
    int frameId,
    string expression,
    string context,
    CancellationToken ct)
{
    return await SendRequestAsync("evaluate", new JsonObject
    {
        ["expression"] = expression,
        ["frameId"] = frameId,
        ["context"] = context
    }, ct);
}
```

### SteppingSessionManager Addition

```csharp
public async Task<string> EvaluateExpressionAsync(
    string expression,
    int depth,
    CancellationToken ct)
```

This method:
1. Gets the top stack frame (same as `InspectVariablesAsync`)
2. Calls `_client.EvaluateAsync(frameId, expression, "watch", ct)`
3. If the result has a `variablesReference > 0` and `depth > 1`, expands children using the existing `ExpandVariableAsync` method
4. Returns structured JSON with result value, type, and optional children

### MCP Tool

```csharp
[McpServerTool(Name = "drhook_step_eval"), Description(
    "Evaluate a C# expression in the context of the current stack frame. " +
    "Supports property access, indexing, method calls, arithmetic, and boolean logic. " +
    "More targeted than drhook_step_vars — use this when you know what you're looking for.")]
public async Task<string> StepEval(
    [Description("C# expression to evaluate (e.g. 'myList.Count', 'x > 5', 'obj.ToString()')")] string expression,
    [Description("Expansion depth for complex results (default 1)")] int depth = 1,
    CancellationToken ct = default)
```

<!-- QUESTION: Should the tool name be drhook_step_eval or drhook_step_evaluate?
     "eval" is shorter and follows the convention of terse debugger commands (gdb's
     "print", lldb's "expr"). "evaluate" is more explicit. Mercury uses full names
     (mercury_query, not mercury_q). I lean toward drhook_step_eval for debugger
     ergonomics — this tool will be called frequently during stepping sessions. -->

### Response Format

```json
{
  "expression": "people.Count",
  "result": "3",
  "type": "int",
  "step": 5,
  "assemblyVersion": "1.5.0"
}
```

For complex results with expansion:

```json
{
  "expression": "people[0]",
  "result": "Person { Name = Alice, Age = 30 }",
  "type": "Person",
  "step": 5,
  "assemblyVersion": "1.5.0",
  "children": [
    { "name": "Name", "value": "Alice", "type": "string" },
    { "name": "Age", "value": "30", "type": "int" }
  ]
}
```

### Error Handling

Expression evaluation can fail for many reasons (syntax error, null reference, type mismatch, debugger limitation). The DAP response includes an error message when evaluation fails:

```json
{
  "success": false,
  "message": "error CS0103: The name 'foo' does not exist in the current context"
}
```

`SteppingSessionManager` should return these errors as structured JSON (same `Error()` pattern used elsewhere) rather than throwing. Failed evaluation is informational, not exceptional — the agent learns from what doesn't work.

<!-- QUESTION: Should we support evaluating in a specific stack frame, not just the
     top frame? The current drhook_step_vars always uses the top frame. For expression
     evaluation, being able to evaluate in a caller frame could be valuable (e.g.,
     "what was the argument passed to this method?"). This would require an optional
     frameIndex parameter. The DapClient.EvaluateAsync already takes a frameId, so
     the plumbing is there — it's a question of MCP tool surface complexity. -->

<!-- QUESTION: Should we add a "watch list" concept — persistent expressions that are
     re-evaluated after each step? This would allow the agent to set up watches and
     then see how values change as it steps through code. This is closer to IDE
     behavior but adds state management complexity. Could be a future ADR if the
     basic evaluate proves useful. -->

## Implementation Plan

### Phase 1: DapClient
- Add `EvaluateAsync(frameId, expression, context, ct)` method
- Tests: `EvaluateAsync` throws when not connected (same pattern as existing tests)

### Phase 2: SteppingSessionManager
- Add `EvaluateExpressionAsync(expression, depth, ct)` method
- Reuse existing `ExpandVariableAsync` for result expansion
- Structured error returns for failed evaluations
- Tests: returns error JSON when no session active

### Phase 3: MCP Tool
- Add `drhook_step_eval` tool in `DrHookTools.cs`
- Parameters: `expression` (required), `depth` (optional, default 1)

## Consequences

### Positive
- Agents can test specific hypotheses about state without full variable enumeration
- More targeted than `drhook_step_vars` — reduces context window noise
- Enables computed checks ("is this invariant holding?") that variable inspection cannot express
- Reuses existing `ExpandVariableAsync` infrastructure

### Trade-offs
- Expression evaluation depends on netcoredbg's expression evaluator — some C# expressions will fail
- Security consideration: expressions can call methods with side effects (e.g., `File.Delete("x")`). This is inherent to debugger expression evaluation and not unique to DrHook. The target process is already under the agent's control.
- One more MCP tool in the surface (14 total, up from 13)

<!-- QUESTION: Should we document which expression categories work reliably with
     netcoredbg? Based on the PoC observations, property access, field access,
     and simple method calls work. Complex LINQ, async expressions, and generic
     methods may not. We could include a "known to work" / "may fail" table in the
     tool description or in documentation. -->

## References
- [DAP Specification — evaluate](https://microsoft.github.io/debug-adapter-protocol/specification#Requests_Evaluate)
- [netcoredbg expression evaluation](https://github.com/Samsung/netcoredbg/wiki/Evaluate)
- [ADR-004 (top-level)](../ADR-004-drhook-runtime-observation-substrate.md) — DrHook intent
