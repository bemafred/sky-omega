using System.ComponentModel;
using ModelContextProtocol.Server;
using SkyOmega.DrHook.Diagnostics;
using SkyOmega.DrHook.Stepping;

namespace SkyOmega.DrHook.Mcp;

[McpServerToolType]
public sealed class DrHookTools
{
    private readonly SteppingSessionManager _session;

    public DrHookTools(SteppingSessionManager session)
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

    [McpServerTool(Name = "drhook_step_launch"), Description(
        "Launch a controlled stepping session against an already-running .NET process. " +
        "Uses netcoredbg (MIT, DAP over stdio). Sets an initial breakpoint and runs to it. " +
        "The process halts at the breakpoint — use drhook_step_next to advance. " +
        "Prefer drhook_step_run when you can launch the process yourself.")]
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
        "no breakpoint was hit. Returns the current source location.")]
    public async Task<string> StepPause(CancellationToken ct = default)
    {
        return await _session.PauseAsync(ct);
    }

    [McpServerTool(Name = "drhook_step_breakpoint"), Description(
        "Add a source breakpoint at a specific file and line. Optionally conditional. " +
        "Multiple breakpoints per file are supported — each call adds to the set. " +
        "Use drhook_step_continue to run to the breakpoint.")]
    public async Task<string> StepBreakpoint(
        [Description("Absolute path to the source file")] string sourceFile,
        [Description("Line number for the breakpoint")] int line,
        [Description("Optional condition expression (e.g. 'counter > 1000')")] string? condition = null,
        CancellationToken ct = default)
    {
        return await _session.SetBreakpointAsync(sourceFile, line, condition, ct);
    }

    [McpServerTool(Name = "drhook_step_break_function"), Description(
        "Add a function breakpoint by method name. Stops at method entry. " +
        "Optionally conditional. Multiple function breakpoints are supported — " +
        "each call adds to the set. Use drhook_step_continue to run to the breakpoint.")]
    public async Task<string> StepBreakFunction(
        [Description("Fully qualified or simple method name (e.g. 'Fibonacci' or 'MyNamespace.MyClass.Fibonacci')")] string functionName,
        [Description("Optional condition expression")] string? condition = null,
        CancellationToken ct = default)
    {
        return await _session.SetFunctionBreakpointAsync(functionName, condition, ct);
    }

    [McpServerTool(Name = "drhook_step_break_exception"), Description(
        "Add an exception breakpoint using DAP exception filters. " +
        "Stops execution when an exception matching the filter is thrown. " +
        "'all' breaks on every throw, 'user-unhandled' breaks only on exceptions not caught in user code. " +
        "Multiple filters can be active simultaneously.")]
    public async Task<string> StepBreakException(
        [Description("Exception filter: 'all' or 'user-unhandled'")] string filter,
        CancellationToken ct = default)
    {
        return await _session.SetExceptionBreakpointAsync(filter, ct);
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
        "List all active breakpoints — source, function, and exception. " +
        "Shows file, line, condition, and function name for each.")]
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

    [McpServerTool(Name = "drhook_step_vars"), Description("Inspect local variables at the current stepping position.")]
    public async Task<string> StepVars(
        [Description("Object inspection depth (default 1)")] int depth = 1,
        CancellationToken ct = default)
    {
        return await _session.InspectVariablesAsync(depth, ct);
    }

    [McpServerTool(Name = "drhook_step_eval"), Description(
        "Evaluate a C# expression in the context of the current stack frame. " +
        "Supports property access, indexing, method calls, arithmetic, and boolean logic. " +
        "More targeted than drhook_step_vars — use this when you know what you're looking for.")]
    public async Task<string> StepEval(
        [Description("C# expression to evaluate (e.g. 'myList.Count', 'x > 5', 'obj.ToString()')")] string expression,
        [Description("Expansion depth for complex results (default 1)")] int depth = 1,
        CancellationToken ct = default)
    {
        return await _session.EvaluateExpressionAsync(expression, depth, ct);
    }

    [McpServerTool(Name = "drhook_step_stop"), Description("End the active stepping session and detach from the process.")]
    public async Task<string> StepStop(CancellationToken ct = default)
    {
        return await _session.StopAsync(ct);
    }
}
