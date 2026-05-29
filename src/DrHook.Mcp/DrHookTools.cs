using System.ComponentModel;
using ModelContextProtocol.Server;
using SkyOmega.DrHook.Engine.Diagnostics;

namespace SkyOmega.DrHook.Mcp;

[McpServerToolType]
public sealed class DrHookTools
{
    private readonly EngineSteppingSession _session;

    public DrHookTools(EngineSteppingSession session)
    {
        _session = session;
    }

    // ─── Observation layer (EventPipe) ──────────────────────────────────────

    [McpServerTool(Name = "drhook_processes"), Description("List running .NET processes available for inspection.")]
    public async Task<string> Processes(CancellationToken ct)
    {
        var attacher = new ProcessAttacher();
        var processes = await attacher.ListDotNetProcessesAsync(ct);
        return processes.ToJson();
    }

    [McpServerTool(Name = "drhook_snapshot"), Description(
        "Capture a summarized observation of a running .NET process via EventPipe. " +
        "Passive — does not halt or modify execution. Returns structured summary " +
        "with thread states, hotspots, exceptions, GC pressure, and anomaly flags. " +
        "Requires a hypothesis: state what you expect BEFORE inspecting.")]
    public async Task<string> Snapshot(
        [Description("Target process ID")] int pid,
        [Description("What you expect to observe. Required — forces epistemic discipline.")] string hypothesis,
        [Description("Trace duration in milliseconds")] int durationMs = 2000,
        CancellationToken ct = default)
    {
        var inspector = new StackInspector();
        var snapshot = await inspector.CaptureAsync(pid, durationMs, hypothesis, ct);
        return snapshot.ToJson();
    }

    // ─── Stepping layer (DAP / netcoredbg) ──────────────────────────────────

    [McpServerTool(Name = "drhook_step_run"), Description(
        "Launch a .NET executable under debugger control, set a breakpoint, and run to it. " +
        "DrHook owns the process lifecycle — no race conditions, no external process management needed. " +
        "The process starts paused, breakpoints are set, then execution continues to the first hit. " +
        "Use this for console apps, prebuilt test wrappers, or any .NET executable. " +
        "Note: dotnet test spawns a child process that the debugger cannot follow — " +
        "wrap test code in a file-based app and use dotnet exec instead.")]
    public async Task<string> StepRun(
        [Description("Executable path (e.g. 'dotnet' or '/path/to/app')")] string program,
        [Description("Arguments as JSON array (e.g. [\"test\", \"--filter\", \"MyTest\"])")] string[] args,
        [Description("Source file path for the initial breakpoint")] string sourceFile,
        [Description("Line number for the initial breakpoint")] int line,
        [Description("What you expect to observe at this breakpoint")] string hypothesis,
        [Description("Working directory (optional, defaults to current)")] string? cwd = null,
        [Description("Environment variables as KEY=VALUE strings (e.g. [\"DOTNET_TieredCompilation=0\"])")] string[]? env = null,
        CancellationToken ct = default)
    {
        Dictionary<string, string>? envDict = null;
        if (env is { Length: > 0 })
        {
            envDict = new Dictionary<string, string>();
            foreach (var entry in env)
            {
                var eqIndex = entry.IndexOf('=');
                if (eqIndex > 0)
                    envDict[entry[..eqIndex]] = entry[(eqIndex + 1)..];
            }
        }

        return await _session.RunAsync(program, args, cwd, sourceFile, line, hypothesis, envDict, ct);
    }

    [McpServerTool(Name = "drhook_step_test"), Description(
        "Debug a .NET test method. Launches 'dotnet test' with VSTEST_HOST_DEBUG=1, " +
        "waits for testhost to pause and report its PID, then attaches the debugger to the " +
        "testhost process and runs to the breakpoint. This is how VS Code debugs tests — " +
        "launch the parent normally, attach to the child.")]
    public async Task<string> StepTest(
        [Description("Path to the test project (e.g. 'tests/MyTests/MyTests.csproj')")] string project,
        [Description("Source file path for the breakpoint")] string sourceFile,
        [Description("Line number for the breakpoint")] int line,
        [Description("What you expect to observe at this breakpoint")] string hypothesis,
        [Description("Test filter expression (e.g. 'MyTestMethod' or 'FullyQualifiedName~MyClass')")] string? filter = null,
        [Description("Working directory (optional, defaults to current)")] string? cwd = null,
        CancellationToken ct = default)
    {
        return await _session.RunTestAsync(project, filter, cwd, sourceFile, line, hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_launch"), Description(
        "Launch a controlled stepping session against an already-running .NET process. " +
        "Uses netcoredbg (MIT, DAP over stdio). Sets an initial breakpoint and runs to it. " +
        "The process halts at the breakpoint — use drhook_step_next to advance. " +
        "Prefer drhook_step_run or drhook_step_test when possible.")]
    public async Task<string> StepLaunch(
        [Description("Target process ID to attach to")] int pid,
        [Description("Source file path for the initial breakpoint")] string sourceFile,
        [Description("Line number for the initial breakpoint")] int line,
        [Description("What you expect to observe at this breakpoint")] string hypothesis,
        CancellationToken ct = default)
    {
        return await _session.LaunchAsync(pid, sourceFile, line, hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_next"), Description(
        "Step one line in the active stepping session. Returns current source location, " +
        "local variables, and their values. This is the core inspection tool — " +
        "Claude Code narrates execution line by line in the terminal.")]
    public async Task<string> StepNext(
        [Description("What you expect on the next line (optional but valuable)")] string? hypothesis = null,
        CancellationToken ct = default)
    {
        return await _session.StepNextAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_into"), Description(
        "Step INTO the method call on the current line. Descends into the callee. " +
        "Use this to follow execution into a method — e.g. entering a recursive call " +
        "or library method to see what happens inside. Contrast with drhook_step_next " +
        "which steps OVER calls.")]
    public async Task<string> StepInto(
        [Description("What you expect inside the method (optional but valuable)")] string? hypothesis = null,
        CancellationToken ct = default)
    {
        return await _session.StepIntoAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_out"), Description(
        "Step OUT of the current method, returning to the caller frame. " +
        "Use this to escape deep call stacks — e.g. after stepping into a recursive " +
        "call, step-out returns to the frame that made the call.")]
    public async Task<string> StepOut(
        [Description("What you expect at the return site (optional but valuable)")] string? hypothesis = null,
        CancellationToken ct = default)
    {
        return await _session.StepOutAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_continue"), Description(
        "Resume execution. If waitForBreakpoint is true (default), blocks until a " +
        "breakpoint is hit and returns the stopped state. If false, returns immediately " +
        "with status 'running' — use drhook_step_pause to interrupt later.")]
    public async Task<string> StepContinue(
        [Description("What you expect at the next breakpoint (optional but valuable)")] string? hypothesis = null,
        [Description("If true (default), wait for a breakpoint hit. If false, return immediately for use with step-pause.")] bool waitForBreakpoint = true,
        CancellationToken ct = default)
    {
        return await _session.ContinueAsync(hypothesis, waitForBreakpoint, ct);
    }

    [McpServerTool(Name = "drhook_step_pause"), Description(
        "Pause a running process immediately. Use after drhook_step_continue when " +
        "you need to interrupt execution — e.g. to inspect a tight loop or when " +
        "no breakpoint was hit. Returns the current source location and metrics.")]
    public async Task<string> StepPause(
        [Description("What you expect to find when execution is interrupted (optional but valuable)")] string? hypothesis = null,
        CancellationToken ct = default)
    {
        return await _session.PauseAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_breakpoint"), Description(
        "Add a source breakpoint at a specific file and line. " +
        "Multiple breakpoints per file are supported — each call adds to the set. " +
        "Use drhook_step_continue to run to the breakpoint.")]
    public async Task<string> StepBreakpoint(
        [Description("Absolute path to the source file")] string sourceFile,
        [Description("Line number for the breakpoint")] int line,
        CancellationToken ct = default)
    {
        return await _session.SetBreakpointAsync(sourceFile, line, null, ct);
    }

    [McpServerTool(Name = "drhook_step_break_function"), Description(
        "Add a function breakpoint by method name. Stops at method entry. " +
        "Multiple function breakpoints are supported — each call adds to the set. " +
        "Use drhook_step_continue to run to the breakpoint.")]
    public async Task<string> StepBreakFunction(
        [Description("Fully qualified or simple method name (e.g. 'Fibonacci' or 'MyNamespace.MyClass.Fibonacci')")] string functionName,
        CancellationToken ct = default)
    {
        return await _session.SetFunctionBreakpointAsync(functionName, null, ct);
    }

    [McpServerTool(Name = "drhook_step_break_exception"), Description(
        "Set an exception breakpoint that stops execution when an exception of the specified type is " +
        "thrown. Matching is SUBCLASS-AWARE: a filter on a base type (e.g. 'System.IOException' or " +
        "'MyApp.DomainException') matches every subclass — including types defined in the target's own " +
        "module. Multiple filters compose with OR semantics; arm one per type you want covered. " +
        "Common idioms: typeName='*' phase='first-chance' = break on every throw (DAP 'all'); " +
        "typeName='*' phase='unhandled' = break only on unhandled exceptions (DAP 'user-unhandled'). " +
        "The optional condition is a C# expression evaluated at each matching exception; pair with " +
        "suspend='none' for a non-stopping exception logpoint (when LogMessage support lands).")]
    public async Task<string> StepBreakException(
        [Description("Fully-qualified CLR type name (e.g. 'System.NullReferenceException', 'MyApp.DomainException'), or '*' for any type. Matching is subclass-aware via the substrate's cross-module ICorDebugType.GetBase walk — a filter on a base catches every derived throw, regardless of which module defines the derived type.")] string typeName,
        [Description("Exception phase: 'any' (default — match any phase), 'first-chance' (fired at the throw site), 'user-first-chance' (search reached first user code), 'catch-handler-found' (a handler has been resolved), or 'unhandled' (no handler found).")] string? phase = null,
        [Description("Optional C# condition gating the breakpoint. Only stops when the expression evaluates true. Has access to the in-flight exception via 'ex' (e.g. 'ex.Code == 42'). Compiled via the substrate's CSharpCondition walker.")] string? condition = null,
        [Description("Optional hit-count gate. The breakpoint only fires on the Nth matching exception (HitCountMode.Equals). Useful with condition for sampling.")] int? hitCount = null,
        [Description("'all' (default — surface the stop) or 'none' (don't stop; intended for logpoint-style emission once LogMessage support lands).")] string? suspend = null,
        CancellationToken ct = default)
    {
        return await _session.SetExceptionBreakpointAsync(typeName, phase, condition, hitCount, suspend, ct);
    }

    [McpServerTool(Name = "drhook_step_breakpoint_remove"), Description(
        "Remove a specific breakpoint. Specify source file + line to remove a source breakpoint, " +
        "or functionName to remove a function breakpoint, or filter to remove an exception filter.")]
    public async Task<string> StepBreakpointRemove(
        [Description("Source file path (for source breakpoints)")] string? sourceFile = null,
        [Description("Line number (for source breakpoints)")] int? line = null,
        [Description("Function name (for function breakpoints)")] string? functionName = null,
        [Description("Exception filter to remove ('all' or 'user-unhandled')")] string? filter = null,
        CancellationToken ct = default)
    {
        if (sourceFile is not null && line is not null)
            return await _session.RemoveBreakpointAsync(sourceFile, line.Value, ct);
        if (functionName is not null)
            return await _session.RemoveFunctionBreakpointAsync(functionName, ct);
        if (filter is not null)
            return await _session.RemoveExceptionBreakpointAsync(filter, ct);

        return "{\"error\": \"Specify sourceFile+line, functionName, or filter to remove.\"}";
    }

    [McpServerTool(Name = "drhook_step_breakpoint_list"), Description(
        "List all active breakpoints with full descriptors — source, function, and exception. " +
        "Each entry includes: id (use with drhook_step_breakpoint_remove), location (file:line / Type.Method / typeName+phase), " +
        "hits (running count of times the breakpoint's policy evaluator has been entered), and " +
        "policy (when one is attached — condition / hitCount / logMessage / suspend, as the agent originally supplied). " +
        "Use this to discover IDs and to verify what's armed before stepping.")]
    public string StepBreakpointList()
    {
        return _session.ListBreakpoints();
    }

    [McpServerTool(Name = "drhook_step_breakpoint_clear"), Description(
        "Clear all breakpoints, or clear by category: 'source', 'function', or 'exception'. " +
        "Omit category to clear everything.")]
    public async Task<string> StepBreakpointClear(
        [Description("Optional category: 'source', 'function', or 'exception'. Omit to clear all.")] string? category = null,
        CancellationToken ct = default)
    {
        return await _session.ClearBreakpointsAsync(category, ct);
    }

    [McpServerTool(Name = "drhook_step_vars"), Description(
        "Inspect local variables at the current stepping position. " +
        "Returns variable names, values, types, and process metrics.")]
    public async Task<string> StepVars(
        [Description("What you expect the variables to show (optional but valuable)")] string? hypothesis = null,
        [Description("Object inspection depth (default 1)")] int depth = 1,
        CancellationToken ct = default)
    {
        return await _session.InspectVariablesAsync(depth, hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_stop"), Description("End the active stepping session and detach from the process.")]
    public async Task<string> StepStop(CancellationToken ct = default)
    {
        return await _session.StopAsync(ct);
    }

    // ─── Substrate diagnostics ──────────────────────────────────────────────

    [McpServerTool(Name = "drhook_drain_anomalies"), Description(
        "Drain the substrate-anomaly buffer (ADR-007 Phase 1). Returns structured evidence " +
        "of substrate-correctness invariants the engine detected but did not raise as exceptions " +
        "— e.g. late mscordbi callbacks, depth-clamped inspections, unexpected HRESULTs from " +
        "Quiesce/Detach/Terminate, worker-thread exceptions. Each anomaly carries Kind, Thread, " +
        "Operation, Observed, Expected, plus kind-specific Context. The buffer is bounded " +
        "(capacity 256, newest-last); dropped count reports records lost to capacity since " +
        "previous drain. Anomalies are NOT errors — they are the substrate's signal that " +
        "something unexpected happened, surfaced so AI consumers can decide whether to escalate.")]
    public string DrainAnomalies()
    {
        return _session.DrainAnomaliesAsJson();
    }
}
