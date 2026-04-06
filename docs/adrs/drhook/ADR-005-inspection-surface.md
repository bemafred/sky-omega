# ADR-005 — Inspection Surface: Eval Fix, Watch Mode, Process Metrics, and Integration Testing

## Status

**Status:** Superseded — 2026-04-06

**Supersession note:** On 2026-04-06, file-based tracing in `DapClient.SendRequestAsync` proved that netcoredbg's DAP `evaluate` request hangs indefinitely on macOS/ARM64. The func-eval machinery deadlocks — netcoredbg's internal 15s command timeout and 5s eval timeout never fire. The `context` parameter is irrelevant (netcoredbg ignores it). Decisions 1-3 (eval context change, watch mode, process metrics via eval) are void. Decision 4 (integration tests) is implemented with reduced scope — no eval, watch, or metrics tests. Decision 5 (extend verify target) is kept. Decision 6 (amend ADR-002) is replaced with a different amendment noting the hang. Long-term replacement: DrHook.Engine (native .NET debugger engine, BCL-only).

## Context

### The Eval Failure

During a production debugging session (2026-04-05), Claude Code used `drhook_step_eval` to inspect `_buffer` while stepping through the Turtle stream parser processing a 978.8 GB file. Every `drhook_step_eval` call returned a DAP error. Claude Code fell back to `drhook_step_vars`, which worked — stepping and variable inspection were functional, only expression evaluation was broken.

The session was abandoned before the parser bug root cause was identified. DrHook's unreliable eval tool directly blocked diagnosis.

### Root Cause Analysis

`drhook_step_vars` and `drhook_step_eval` share the stack frame resolution path (`GetStackTraceAsync` → top frame ID) but diverge at the DAP request:

| Tool | DAP Path | How it works |
|------|----------|-------------|
| `step_vars` | `scopes` → `variables` | Walks the debugger's pre-built scope tree. Pure data read. |
| `step_eval` | `evaluate` with `context` param | Asks netcoredbg's expression evaluator to **parse and compile** the expression string. |

ADR-002 specified `context: "watch"` (line 84 of SteppingSessionManager.cs, line 19 of ADR-002). This matches DAP's watch panel semantics. However, netcoredbg's expression evaluator behaves differently across context values:

- **`"watch"`**: Restrictive. Intended for the IDE watch panel where expressions are re-evaluated silently after every stop. netcoredbg applies stricter evaluation rules in this context — some expressions that work in `"repl"` context fail here.
- **`"repl"`**: Permissive. Intended for the debug console where the user types expressions interactively. Supports broader expression forms including field access by bare name.

The blanket "DAP error on every call" pattern — including for a simple variable name like `_buffer` — is consistent with a context-level rejection, not an expression-specific failure. The `"watch"` context is the most likely cause.

**Contributing factor:** `_buffer` is likely a field (not a local variable). Evaluating an instance field by bare name requires implicit `this` resolution. The `"watch"` context may not perform this resolution where `"repl"` does.

### The Missing Inspection Mode

The eval failure exposed a deeper design gap. DAP defines three distinct inspection patterns, each with its own context value and intended usage:

| IDE Concept | DAP Mechanism | DAP Context | Behavior |
|---|---|---|---|
| Variables panel | `scopes` → `variables` | (none) | Walk pre-built scope tree. Pure data read. |
| Debug console | `evaluate` | `"repl"` | One-shot interactive expression. Permissive. |
| Watch panel | `evaluate` | `"watch"` | Persistent expressions re-evaluated after every stop. Restrictive — designed for silent, repeated evaluation. |

DrHook currently has tools for the first two but conflates them: `step_vars` implements the variables panel correctly, but `step_eval` uses `"watch"` context for what is functionally debug console behavior (one-shot, interactive, on-demand).

The third pattern — persistent watches — is **missing entirely** and is arguably the most valuable for an AI agent. During a stepping session, Claude Code frequently needs to track 2-3 expressions across many steps. Currently this requires calling `step_eval` after every `step_next` — three tool calls instead of one. A watch mode would attach expressions to the session and include their values in every step result automatically.

### The Observation Gap: No Metrics During Stepping

DrHook has two observation layers that don't connect:

**EventPipe layer (`drhook_snapshot`):** Captures GC pressure, exceptions, contention, thread hotspots via `StackInspector`. Requires the process to be *running* for a sample window (default 2000ms). Cannot operate during stepping — the process is paused.

**Stepping layer (`drhook_step_*`):** Controls paused execution, inspects variables and expressions. Has no visibility into process-level health — memory consumption, GC activity, thread count.

During the 903 GB parser session, the failure occurred after millions of triples. Was it a memory wall? GC pressure? Allocation storm? The stepping layer couldn't answer these questions, and the EventPipe layer can't be used during stepping.

However, **OS-level process metrics are always available** — paused or running. `Process.GetProcessById(pid)` returns WorkingSet64, PrivateMemorySize64, and thread count regardless of process state. These are OS kernel counters, not runtime samples. Cost: one syscall. And once eval works (Decision 1), managed-layer metrics like `GC.GetTotalMemory(false)` and `GC.CollectionCount(N)` become available too.

Crucially, during `ContinueAsync` (run to breakpoint), the process runs freely. When it hits the breakpoint and stops, metrics captured at that point reflect everything that happened since the last stop. Combined with conditional breakpoints (e.g., `"tripleCount % 100000 == 0"`), this gives a profiling view: memory consumption sampled at regular intervals through a long-running operation, with zero tool call overhead.

### The Test Gap

The failure was not caught earlier because DrHook has **zero integration tests**. All 14 existing tests in `SteppingSessionManagerTests` verify only the "no active session" guard — that each method returns `{"error": "No active stepping session"}` when called without a DAP connection.

No test launches netcoredbg, connects to a target, or exercises any tool against a live debugging session. The existing `drhook-verify.cs` is a suitable non-interactive target for integration tests but has never been used as one.

## Decision

### 1. Change eval context from `"watch"` to `"repl"`

In `SteppingSessionManager.EvaluateExpressionAsync`, change:

```csharp
// Before (ADR-002 specification)
response = await _client.EvaluateAsync(topFrameId, expression, "watch", ct);

// After
response = await _client.EvaluateAsync(topFrameId, expression, "repl", ct);
```

**Rationale:** DrHook's eval is invoked on-demand by Claude Code during a stepping session — this is interactive debug console behavior, not passive watch panel behavior. The `"repl"` context matches the actual usage pattern.

**Risk:** `"repl"` context may allow side-effecting expressions that `"watch"` would reject. This is acceptable — the target process is already under the agent's control, and ADR-002 already acknowledged this (see "Security consideration" in Consequences).

### 2. Add persistent watch mode

Add three MCP tools and supporting session state:

#### New MCP tools

**`drhook_step_watch_add`** — Add a C# expression to the watch list.

```csharp
[McpServerTool(Name = "drhook_step_watch_add"), Description(
    "Add a C# expression to the persistent watch list. Watched expressions are " +
    "automatically evaluated after every step/continue and included in the result. " +
    "Use this to track values across multiple steps without repeated drhook_step_eval calls.")]
public async Task<string> StepWatchAdd(
    [Description("C# expression to watch (e.g. '_buffer.Length', 'i > 3')")] string expression,
    [Description("Display label (optional, defaults to the expression itself)")] string? label = null,
    CancellationToken ct = default)
```

**`drhook_step_watch_remove`** — Remove an expression from the watch list.

```csharp
[McpServerTool(Name = "drhook_step_watch_remove"), Description(
    "Remove an expression from the persistent watch list.")]
public async Task<string> StepWatchRemove(
    [Description("Expression or label to remove")] string expression,
    CancellationToken ct = default)
```

**`drhook_step_watch_list`** — List all active watches with current values.

```csharp
[McpServerTool(Name = "drhook_step_watch_list"), Description(
    "List all active watch expressions and their current values.")]
public async Task<string> StepWatchList(CancellationToken ct = default)
```

#### Session state

Add to `SteppingSessionManager`:

```csharp
// Watch registry — label → expression. Label defaults to expression if not provided.
private readonly Dictionary<string, string> _watches = new();
```

Clear `_watches` in `CleanupAsync` alongside the breakpoint registries.

#### Watch evaluation

Add a private method that evaluates all watches and returns a JSON array:

```csharp
private async Task<JsonArray> EvaluateWatchesAsync(int frameId, CancellationToken ct)
{
    var results = new JsonArray();
    foreach (var (label, expression) in _watches)
    {
        var node = new JsonObject { ["label"] = label, ["expression"] = expression };
        try
        {
            var response = await _client!.EvaluateAsync(frameId, expression, "watch", ct);
            node["result"] = response["result"]?.DeepClone();
            node["type"] = response["type"]?.DeepClone();
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("DAP error"))
        {
            node["error"] = ex.Message;
        }
        results.Add(node);
    }
    return results;
}
```

Note: watches use `"watch"` context — this is its correct DAP usage. Silent, repeated evaluation after each stop. If a watch expression fails, the error is captured per-expression rather than breaking the step result. This is how IDE watch panels work: invalid expressions show an error in the watch row, they don't halt stepping.

#### Integration into step results

Modify `StepNextAsync`, `StepIntoAsync`, `StepOutAsync`, `ContinueAsync` (when `waitForBreakpoint: true`), and `PauseAsync`: after building the result `JsonObject`, if `_watches.Count > 0`, evaluate watches and add them:

```csharp
if (_watches.Count > 0)
{
    var topFrameId = /* from the state already fetched */;
    result["watches"] = await EvaluateWatchesAsync(topFrameId, ct);
}
```

The step methods already call `GetCurrentStateAsync` which fetches the stack trace. To avoid a redundant `GetStackTraceAsync` call, extract the top frame ID from the state retrieval and pass it through. This is a minor refactor of `GetCurrentStateAsync` to also return the frame ID.

#### Context mapping — the complete picture after this ADR

| DrHook Tool | DAP Mechanism | DAP Context | Usage Pattern |
|---|---|---|---|
| `step_vars` | `scopes` → `variables` | — | "Show me everything in scope" |
| `step_eval` | `evaluate` | `"repl"` | "Evaluate this expression once, right now" |
| `step_watch_add/remove/list` | `evaluate` | `"watch"` | "Track these expressions across steps" |

Each DAP context now has its correct DrHook tool. The semantic confusion that caused the original bug is structurally resolved.

### 3. Add process metrics dashboard to step results

Include lightweight runtime metrics in every step/continue/pause response. No extra tool calls — metrics are captured as part of the step result, alongside watches.

#### Session state

Add to `SteppingSessionManager`:

```csharp
private int _targetPid;  // Set during RunAsync/LaunchAsync/RunTestAsync
private ProcessMetrics? _previousMetrics;  // For delta computation
```

#### Metrics capture

```csharp
private record ProcessMetrics(
    double WorkingSetMB,
    double PrivateBytesMB,
    int ThreadCount,
    long? ManagedHeapBytes,   // null if eval unavailable
    int? GcGen0,
    int? GcGen1,
    int? GcGen2);

private async Task<JsonObject> CaptureMetricsAsync(int frameId, CancellationToken ct)
{
    // OS layer — always available, even when paused
    var proc = Process.GetProcessById(_targetPid);
    var workingSetMB = proc.WorkingSet64 / (1024.0 * 1024.0);
    var privateBytesMB = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
    var threadCount = proc.Threads.Count;

    // Managed layer — via eval, best-effort
    long? managedHeap = null;
    int? gen0 = null, gen1 = null, gen2 = null;
    if (_client is not null)
    {
        managedHeap = await TryEvalLong(frameId, "GC.GetTotalMemory(false)", ct);
        gen0 = await TryEvalInt(frameId, "GC.CollectionCount(0)", ct);
        gen1 = await TryEvalInt(frameId, "GC.CollectionCount(1)", ct);
        gen2 = await TryEvalInt(frameId, "GC.CollectionCount(2)", ct);
    }

    var current = new ProcessMetrics(workingSetMB, privateBytesMB, threadCount,
        managedHeap, gen0, gen1, gen2);

    var result = new JsonObject
    {
        ["workingSetMB"] = Math.Round(workingSetMB, 1),
        ["privateBytesMB"] = Math.Round(privateBytesMB, 1),
        ["threadCount"] = threadCount,
    };

    if (managedHeap is not null)
        result["managedHeapMB"] = Math.Round(managedHeap.Value / (1024.0 * 1024.0), 1);
    if (gen0 is not null) result["gcGen0"] = gen0.Value;
    if (gen1 is not null) result["gcGen1"] = gen1.Value;
    if (gen2 is not null) result["gcGen2"] = gen2.Value;

    // Delta from previous capture
    if (_previousMetrics is not null)
    {
        result["deltaWorkingSetMB"] = Math.Round(
            workingSetMB - _previousMetrics.WorkingSetMB, 1);
        if (managedHeap is not null && _previousMetrics.ManagedHeapBytes is not null)
            result["deltaManagedHeapMB"] = Math.Round(
                (managedHeap.Value - _previousMetrics.ManagedHeapBytes.Value) / (1024.0 * 1024.0), 1);
        if (gen2 is not null && _previousMetrics.GcGen2 is not null)
            result["deltaGcGen2"] = gen2.Value - _previousMetrics.GcGen2.Value;
    }

    _previousMetrics = current;
    return result;
}

private async Task<long?> TryEvalLong(int frameId, string expr, CancellationToken ct)
{
    try
    {
        var r = await _client!.EvaluateAsync(frameId, expr, "repl", ct);
        return long.TryParse(r["result"]?.GetValue<string>(), out var v) ? v : null;
    }
    catch { return null; }
}

private async Task<int?> TryEvalInt(int frameId, string expr, CancellationToken ct)
{
    try
    {
        var r = await _client!.EvaluateAsync(frameId, expr, "repl", ct);
        return int.TryParse(r["result"]?.GetValue<string>(), out var v) ? v : null;
    }
    catch { return null; }
}
```

#### Integration into step results

Same pattern as watches — after building the result JsonObject in each step method:

```csharp
var topFrameId = /* from stack trace already fetched */;
result["metrics"] = await CaptureMetricsAsync(topFrameId, ct);
```

The metrics block appears in **every** step, continue, and pause response. OS metrics are always present. Managed metrics appear when eval is functional and silently omit when it isn't — graceful degradation.

#### Result shape

```json
{
  "operation": "next",
  "step": 47,
  "currentState": { "file": "TurtleStreamParser.cs", "line": 312 },
  "watches": [ { "label": "_position", "result": "4821903", "type": "long" } ],
  "metrics": {
    "workingSetMB": 2847.3,
    "deltaWorkingSetMB": 142.7,
    "privateBytesMB": 3012.1,
    "managedHeapMB": 2341.8,
    "deltaManagedHeapMB": 138.2,
    "gcGen0": 142,
    "gcGen1": 31,
    "gcGen2": 3,
    "deltaGcGen2": 1,
    "threadCount": 14
  }
}
```

A Gen2 delta of +1 between two breakpoint hits tells a different story than steady Gen0 churn. Rising `deltaWorkingSetMB` with flat `deltaManagedHeapMB` points to native/unmanaged memory growth. These signals are diagnosis-ready without additional analysis.

#### The conditional-breakpoint profiling pattern

For long-running operations like the Wikidata pipeline, combine conditional breakpoints with the metrics dashboard:

1. Set breakpoint with condition `"tripleCount % 100000 == 0"`
2. Add watch: `"tripleCount"`
3. `ContinueAsync` — process runs freely
4. At each 100K-triple stop: metrics show memory trend, watches show progress

This gives a lightweight profiling view across millions of iterations with zero manual polling. The agent sees the memory trajectory and can identify the exact interval where the leak or wall appears.

#### Cleanup

Clear `_previousMetrics` and `_targetPid` in `CleanupAsync`.

### 4. Add integration tests using `drhook-verify.cs`

Create `SteppingIntegrationTests.cs` in `DrHook.Tests/Stepping/` that builds and runs `drhook-verify.cs` under debugger control and exercises each tool category.

#### Target: `drhook-verify.cs`

The existing verification target is ideal — deterministic, non-interactive, with known values:

```csharp
static void DoWork()
{
    var sum = 0;
    for (var i = 1; i <= 5; i++)
    {
        sum += i;  // ← breakpoint target: known values at each iteration
        Console.WriteLine($"  i={i}, sum={sum}");
    }
    Console.WriteLine($"Final sum: {sum}");
}
```

#### Required test coverage

Each test launches a fresh session via `SteppingSessionManager.RunAsync` with a breakpoint inside `DoWork`, exercises the tool under test, then stops the session. Tests are independent — no shared session state.

**Group 1 — Session lifecycle:**

| Test | Exercises | Verifies |
|------|-----------|----------|
| `Run_StopsAtBreakpoint` | `RunAsync` | Returns `status: "launched"`, `currentState` has correct file/line |
| `Run_RejectsDoubleSession` | `RunAsync` × 2 | Second call returns error JSON |
| `Stop_ReturnsSummary` | `RunAsync` → `StopAsync` | Returns `status: "stopped"`, `totalSteps`, `sessionHypothesis` |

**Group 2 — Stepping:**

| Test | Exercises | Verifies |
|------|-----------|----------|
| `StepNext_AdvancesLine` | `StepNextAsync` | Line number increments, `step` count increments |
| `StepInto_DescendsIntoMethod` | `StepIntoAsync` on `DoWork()` call | Function name changes to `DoWork` |
| `StepOut_ReturnsToCallerFrame` | `StepIntoAsync` → `StepOutAsync` | Returns to caller frame |
| `Continue_RunsToBreakpoint` | Set second breakpoint → `ContinueAsync` | Stops at second breakpoint |
| `Pause_InterruptsExecution` | `ContinueAsync(waitForBreakpoint: false)` → `PauseAsync` | Returns valid state |

**Group 3 — Variable inspection:**

| Test | Exercises | Verifies |
|------|-----------|----------|
| `Vars_ReturnsLocals` | `InspectVariablesAsync` | JSON contains `sum`, `i` with correct types |
| `Vars_DepthExpansion` | `InspectVariablesAsync(depth: 2)` on a loop iteration | Values match expected state |

**Group 4 — Expression evaluation (the broken path):**

| Test | Exercises | Verifies |
|------|-----------|----------|
| `Eval_SimpleVariable` | `EvaluateExpressionAsync("sum", 1)` | Returns `result` with integer value, no error |
| `Eval_PropertyAccess` | `EvaluateExpressionAsync("i.ToString()", 1)` | Returns string result |
| `Eval_Arithmetic` | `EvaluateExpressionAsync("sum + 10", 1)` | Returns computed value |
| `Eval_BooleanExpression` | `EvaluateExpressionAsync("i > 2", 1)` | Returns `true` or `false` |
| `Eval_InvalidExpression` | `EvaluateExpressionAsync("nonexistent", 1)` | Returns structured error JSON (not exception) |
| `Eval_DepthExpansion` | Eval a complex object with `depth: 2` | Returns `children` array |

**Group 5 — Watch mode:**

| Test | Exercises | Verifies |
|------|-----------|----------|
| `WatchAdd_ReturnsCurrentValue` | `WatchAddAsync("sum")` | Returns JSON with `result` and `type` for current value |
| `WatchRemove_RemovesExpression` | Add → Remove → List | Watch no longer in list |
| `WatchList_ShowsAllWatches` | Add `"sum"` and `"i"` → List | Both watches returned with values |
| `WatchInStepNext_IncludesValues` | Add `"sum"` → `StepNextAsync` | Step result contains `watches` array with `sum` value |
| `WatchInStepInto_IncludesValues` | Add watch → `StepIntoAsync` | Step result contains `watches` array |
| `WatchError_DoesNotBreakStep` | Add `"nonexistent"` → `StepNextAsync` | Step succeeds, watch entry has `error` field |
| `WatchClearedOnStop` | Add watch → `StopAsync` → new session | Watch list empty in new session |

**Group 6 — Process metrics:**

| Test | Exercises | Verifies |
|------|-----------|----------|
| `Metrics_PresentInStepNext` | `StepNextAsync` | Response contains `metrics` object with `workingSetMB`, `privateBytesMB`, `threadCount` |
| `Metrics_DeltaAfterSecondStep` | Two `StepNextAsync` calls | Second response has `deltaWorkingSetMB` field |
| `Metrics_ManagedHeapPresent` | `StepNextAsync` (with eval working) | `managedHeapMB` and `gcGen0/1/2` present |
| `Metrics_GracefulDegradation` | Eval a target where GC expressions fail | OS metrics present, managed metrics absent, no error |
| `Metrics_PresentInContinue` | `ContinueAsync` to breakpoint | Response contains `metrics` with accumulated deltas |

**Group 7 — Breakpoints:**

| Test | Exercises | Verifies |
|------|-----------|----------|
| `Breakpoint_AddAndList` | `SetBreakpointAsync` → `ListBreakpoints` | Breakpoint appears in list |
| `Breakpoint_Remove` | Add → Remove → List | Breakpoint removed from list |
| `Breakpoint_Clear` | Add multiple → `ClearBreakpointsAsync` | `totalCount: 0` |
| `FunctionBreakpoint_StopsAtEntry` | `SetFunctionBreakpointAsync("DoWork")` → Continue | Stops at `DoWork` entry |
| `ExceptionBreakpoint_CatchesThrow` | Needs a target that throws — extend `drhook-verify.cs` or use separate target |
| `ConditionalBreakpoint` | `SetBreakpointAsync(line, "i == 3")` → Continue | Stops only when `i == 3` |

#### Build prerequisite

Integration tests must build `drhook-verify.cs` before running. Options:

1. **`dotnet run --file` (preferred if .NET 10+):** No build step — `drhook_step_run` launches `dotnet run --file drhook-verify.cs` directly.
2. **Pre-build in test setup:** Create a small console project wrapping `drhook-verify.cs`, build it in test fixture setup, run the DLL with `dotnet exec`.

Use option 1 if the runtime supports it; fall back to option 2 otherwise.

#### Test trait

Mark all integration tests with `[Trait("Category", "Integration")]` so they can be excluded from fast CI runs (they launch real processes and depend on netcoredbg being installed).

### 5. Extend `drhook-verify.cs` for full tool coverage

The current verify target covers locals and a loop. Add scenarios for:

```csharp
// Exception scenario (for exception breakpoint testing)
static void ThrowAndCatch()
{
    try { throw new InvalidOperationException("verify-exception"); }
    catch (InvalidOperationException) { /* caught */ }
}

// Object with fields (for eval field access and depth expansion)
static void ObjectInspection()
{
    var person = new Person("Alice", 30);
    var greeting = person.Name;  // ← breakpoint: eval "person.Name", "person.Age > 18"
}

record Person(string Name, int Age);
```

Keep it minimal — each scenario should be callable from a predictable code path so tests can set breakpoints at known line numbers.

### 6. Amend ADR-002

Add an amendment section to ADR-002 noting:
1. `"watch"` context was found to cause blanket evaluation failures in production and has been replaced by `"repl"` per this ADR.
2. The open question about a "watch list" concept (ADR-002 line 154-158) is now answered: yes, implemented via `step_watch_add/remove/list` in this ADR.

## Consequences

### Positive

- Eval tool becomes functional — unblocks DrHook for production debugging
- Watch mode reduces tool call volume during stepping sessions (1 call instead of N+1 per step when tracking N expressions)
- Process metrics dashboard gives memory/GC visibility at every stop point — the missing diagnostic for the 903 GB parser session
- Conditional breakpoint + metrics = lightweight profiler for long-running operations (no separate profiling tool needed)
- Each DAP context (`"repl"`, `"watch"`, scope walk) maps to exactly one DrHook tool — the semantic confusion that caused the original bug is structurally impossible
- Integration tests catch regressions before they reach production sessions
- `drhook-verify.cs` becomes a living specification of DrHook's capabilities
- Every tool path is exercised against a real DAP session, not just guard clauses

### Trade-offs

- Integration tests are slower than unit tests (process launch, netcoredbg startup) — mitigated by `[Trait("Category", "Integration")]` for selective runs
- Tests depend on netcoredbg being installed — same as DrHook itself; this is an existing dependency, not a new one
- `"repl"` context for `step_eval` is more permissive — side-effecting expressions won't be blocked. Acceptable per ADR-002's existing security analysis.
- Three new MCP tools for watches + metrics in every response (17 tools, up from 14) — justified by resolving a real semantic ambiguity and closing the observation gap
- Managed-layer metrics add 4 eval calls per step internally — acceptable latency for the diagnostic value. OS-layer metrics (one syscall) are essentially free. Managed metrics degrade gracefully to absent when eval fails.

### Risk: context change doesn't fix it

If changing to `"repl"` does not resolve the eval failure, the integration test suite will surface the actual failure mode immediately. The tests are valuable regardless of whether the context was the root cause — they close the test gap that allowed the tool to ship broken.

If `"repl"` also fails, next diagnostic steps:
1. Check netcoredbg version — evaluate support varies across versions
2. Test with simple locals (not fields) to isolate `this` resolution
3. Capture raw DAP error message from netcoredbg (currently discarded in the inner catch)

## Implementation Order

1. Write the integration test for `Eval_SimpleVariable` first — this is the minimal reproduction of the production failure
2. Run it with `"watch"` context to confirm it fails (red)
3. Change to `"repl"`, run again to confirm it passes (green)
4. Implement watch state in `SteppingSessionManager` (`_watches` dictionary, `EvaluateWatchesAsync`, cleanup)
5. Add `WatchAddAsync`, `WatchRemoveAsync`, `WatchListAsync` to `SteppingSessionManager`
6. Wire watches into step result paths (`StepNextAsync`, `StepIntoAsync`, `StepOutAsync`, `ContinueAsync`, `PauseAsync`)
7. Add MCP tools: `drhook_step_watch_add`, `drhook_step_watch_remove`, `drhook_step_watch_list`
8. Implement `CaptureMetricsAsync` with OS-layer metrics (`Process.GetProcessById`) and managed-layer metrics (GC eval expressions)
9. Wire metrics into step result paths (same insertion points as watches)
10. Implement remaining integration tests (all groups including metrics)
11. Extend `drhook-verify.cs` with exception and object scenarios
12. Amend ADR-002

## References

- [ADR-002 — Expression Evaluation](ADR-002-expression-evaluation.md) — original specification (watch context)
- [DAP Specification — evaluate request](https://microsoft.github.io/debug-adapter-protocol/specification#Requests_Evaluate) — context values
- [netcoredbg evaluate wiki](https://github.com/Samsung/netcoredbg/wiki/Evaluate)
- Production failure: 2026-04-05, Turtle parser session on 903 GB file
