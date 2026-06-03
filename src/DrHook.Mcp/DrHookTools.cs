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

    // ─── Stepping layer (DrHook.Engine / ICorDebug) ─────────────────────────

    [McpServerTool(Name = "drhook_launch"), Description(
        "Launch a NEW .NET executable under debugger control, set a breakpoint, and run to it. " +
        "DrHook owns the process lifecycle (Owned session) — no race conditions, no external process management needed. " +
        "The process starts paused, breakpoints are set, then execution continues to the first hit. " +
        "Use this for console apps, prebuilt test wrappers, or any .NET executable. " +
        "To debug a test, attach to the testhost child with drhook_attach; project-aware launch directly from a " +
        ".csproj (dispatching MTP / VSTest internally) is planned — ADR-010 Tier 3.")]
    public async Task<string> Launch(
        [Description("Executable path (e.g. 'dotnet' or '/path/to/app')")] string program,
        [Description("Arguments as JSON array (e.g. [\"exec\", \"/path/to/app.dll\"])")] string[] args,
        [Description("Source file path for the initial breakpoint")] string sourceFile,
        [Description("Line number for the initial breakpoint")] int line,
        [Description("What you expect to observe at this breakpoint")] string hypothesis,
        [Description("Working directory (optional, defaults to current)")] string? cwd = null,
        [Description("Environment variables as KEY=VALUE strings, merged onto the inherited environment (e.g. [\"DOTNET_TieredCompilation=0\"]). Launch/Owned only — an attached target's environment is fixed at its own start.")] string[]? env = null,
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

        return await _session.LaunchAsync(program, args, cwd, sourceFile, line, hypothesis, envDict, ct);
    }

    [McpServerTool(Name = "drhook_attach"), Description(
        "Attach to an ALREADY-RUNNING .NET process by PID (Borrowed session — the target survives when the session ends). " +
        "Sets an initial breakpoint and runs to it. The process halts at the breakpoint — use drhook_step_over to advance. " +
        "Prefer drhook_launch when you control process startup. Use drhook_processes to discover PIDs.")]
    public async Task<string> Attach(
        [Description("Target process ID to attach to")] int pid,
        [Description("Source file path for the initial breakpoint")] string sourceFile,
        [Description("Line number for the initial breakpoint")] int line,
        [Description("What you expect to observe at this breakpoint")] string hypothesis,
        CancellationToken ct = default)
    {
        return await _session.AttachAsync(pid, sourceFile, line, hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_over"), Description(
        "Step over the current line — execute it without descending into any method calls on it. " +
        "Returns current source location, local variables, and their values. This is the core inspection tool — " +
        "Claude Code narrates execution line by line in the terminal. Contrast with drhook_step_into, which " +
        "descends INTO the call on the current line.")]
    public async Task<string> StepOver(
        [Description("What you expect on the next line (optional but valuable)")] string? hypothesis = null,
        CancellationToken ct = default)
    {
        return await _session.StepOverAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_step_into"), Description(
        "Step INTO the method call on the current line. Descends into the callee. " +
        "Use this to follow execution into a method — e.g. entering a recursive call " +
        "or library method to see what happens inside. Contrast with drhook_step_over " +
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

    [McpServerTool(Name = "drhook_continue"), Description(
        "Resume execution. If waitForBreakpoint is true (default), blocks until a " +
        "breakpoint is hit and returns the stopped state. If false, returns immediately " +
        "with status 'running' — use drhook_pause to interrupt later.")]
    public async Task<string> Continue(
        [Description("What you expect at the next breakpoint (optional but valuable)")] string? hypothesis = null,
        [Description("If true (default), wait for a breakpoint hit. If false, return immediately for use with drhook_pause.")] bool waitForBreakpoint = true,
        CancellationToken ct = default)
    {
        return await _session.ContinueAsync(hypothesis, waitForBreakpoint, ct);
    }

    [McpServerTool(Name = "drhook_pause"), Description(
        "Pause a running process immediately. Use after drhook_continue (waitForBreakpoint=false) when " +
        "you need to interrupt execution — e.g. to inspect a tight loop or when " +
        "no breakpoint was hit. Returns the current source location and metrics.")]
    public async Task<string> Pause(
        [Description("What you expect to find when execution is interrupted (optional but valuable)")] string? hypothesis = null,
        CancellationToken ct = default)
    {
        return await _session.PauseAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_break_source"), Description(
        "Add a source breakpoint at a specific file and line, optionally gated by a policy. " +
        "Multiple breakpoints per file are supported — each call adds to the set. " +
        "Optional policy parameters: 'condition' is a C# expression evaluated each hit (the breakpoint " +
        "only surfaces when it evaluates true); 'hitCount' fires only on the Nth matching hit; " +
        "'logMessage' is a template like 'v={value}' rendered per hit and emitted as a structured " +
        "LogRecord (pair with suspend='none' for a non-stopping logpoint); 'suspend' set to 'none' " +
        "fires the policy's actions without stopping. Use drhook_continue to run to the breakpoint.")]
    public async Task<string> BreakSource(
        [Description("Absolute path to the source file")] string sourceFile,
        [Description("Line number for the breakpoint")] int line,
        [Description("Optional C# condition (e.g. 'value == 3', 's.Length > 0'). Has access to locals + arguments of the current frame. Compiled via the substrate's CSharpCondition walker.")] string? condition = null,
        [Description("Optional hit-count gate. The breakpoint only fires on the Nth matching hit (HitCountMode.Equals).")] int? hitCount = null,
        [Description("Optional log-message template with literal text and {expr} interpolation fragments (e.g. 'v={value} size={box.Size}'). Each fragment uses the same expression subset as condition — literals, identifiers, member access, parens, NOT, comparison binops, and typed arithmetic (+ - * / %); format specifiers are NOT yet supported. Use {{ and }} for literal braces. Rendered per hit and emitted via IDebugEventSink.OnLog.")] string? logMessage = null,
        [Description("'all' (default — surface the stop) or 'none' (don't stop; intended for logpoint mode when paired with logMessage).")] string? suspend = null,
        CancellationToken ct = default)
    {
        return await _session.SetBreakpointAsync(sourceFile, line, condition, hitCount, logMessage, suspend, ct);
    }

    [McpServerTool(Name = "drhook_break_function"), Description(
        "Add a function breakpoint by method name, optionally gated by a policy. Stops at method entry. " +
        "Multiple function breakpoints are supported — each call adds to the set. " +
        "Optional policy parameters mirror drhook_break_source: 'condition' (C# expression with " +
        "access to method arguments + locals at entry), 'hitCount' (fire on Nth matching call), " +
        "'logMessage' (template with {expr} interpolation rendered per hit), 'suspend' ('all' to stop, " +
        "'none' for logpoint mode). Use drhook_continue to run to the breakpoint.")]
    public async Task<string> BreakFunction(
        [Description("Fully qualified or simple method name (e.g. 'Fibonacci' or 'MyNamespace.MyClass.Fibonacci')")] string functionName,
        [Description("Optional C# condition (e.g. 'n > 0'). Has access to method arguments + locals at entry. Compiled via the substrate's CSharpCondition walker.")] string? condition = null,
        [Description("Optional hit-count gate. The breakpoint only fires on the Nth matching call (HitCountMode.Equals).")] int? hitCount = null,
        [Description("Optional log-message template with {expr} interpolation (e.g. 'entered with n={n}'). Same expression subset as condition; rendered per hit and emitted via IDebugEventSink.OnLog.")] string? logMessage = null,
        [Description("'all' (default — surface the stop) or 'none' (don't stop; intended for logpoint mode when paired with logMessage).")] string? suspend = null,
        CancellationToken ct = default)
    {
        return await _session.SetFunctionBreakpointAsync(functionName, condition, hitCount, logMessage, suspend, ct);
    }

    [McpServerTool(Name = "drhook_break_exception"), Description(
        "Set an exception breakpoint that stops execution when an exception of the specified type is " +
        "thrown. Matching is SUBCLASS-AWARE: a filter on a base type (e.g. 'System.IOException' or " +
        "'MyApp.DomainException') matches every subclass — including types defined in the target's own " +
        "module. Multiple filters compose with OR semantics; arm one per type you want covered. " +
        "Common idioms: typeName='*' phase='first-chance' = break on every throw (DAP 'all'); " +
        "typeName='*' phase='unhandled' = break only on unhandled exceptions (DAP 'user-unhandled'). " +
        "The optional condition is a C# expression evaluated at each matching exception; pair with " +
        "suspend='none' and logMessage for a non-stopping exception logpoint. " +
        "Returns the substrate-assigned id; pass it to drhook_break_remove to disarm, or " +
        "see drhook_break_list to inspect what's currently armed.")]
    public async Task<string> BreakException(
        [Description("Fully-qualified CLR type name (e.g. 'System.NullReferenceException', 'MyApp.DomainException'), or '*' for any type. Matching is subclass-aware via the substrate's cross-module ICorDebugType.GetBase walk — a filter on a base catches every derived throw, regardless of which module defines the derived type.")] string typeName,
        [Description("Exception phase: 'any' (default — match any phase), 'first-chance' (fired at the throw site), 'user-first-chance' (search reached first user code), 'catch-handler-found' (a handler has been resolved), or 'unhandled' (no handler found).")] string? phase = null,
        [Description("Optional C# condition gating the breakpoint. Only stops when the expression evaluates true. Has access to the in-flight exception via 'ex' (e.g. 'ex.Code == 42'). Compiled via the substrate's CSharpCondition walker.")] string? condition = null,
        [Description("Optional hit-count gate. The breakpoint only fires on the Nth matching exception (HitCountMode.Equals). Useful with condition for sampling.")] int? hitCount = null,
        [Description("Optional log-message template with {expr} interpolation (e.g. 'caught {ex.Code}'). Rendered per matching exception and emitted via IDebugEventSink.OnLog. Pair with suspend='none' for non-stopping exception telemetry.")] string? logMessage = null,
        [Description("'all' (default — surface the stop) or 'none' (don't stop; intended for logpoint-style emission when paired with logMessage).")] string? suspend = null,
        CancellationToken ct = default)
    {
        return await _session.SetExceptionBreakpointAsync(typeName, phase, condition, hitCount, logMessage, suspend, ct);
    }

    [McpServerTool(Name = "drhook_break_remove"), Description(
        "Remove a breakpoint or exception filter by its substrate-assigned id. Use " +
        "drhook_break_list first to discover ids. Dispatches to the right substrate path " +
        "(source / function / exception) automatically based on the kind the id refers to. " +
        "Returns status='removed' on success, 'stale' if MCP tracking knew about the id but the " +
        "substrate had no matching entry (MCP-layer state is pruned either way).")]
    public async Task<string> BreakRemove(
        [Description("Substrate-assigned breakpoint id, as returned by drhook_break_source, drhook_break_function, or drhook_break_exception, or as listed in drhook_break_list.")] int id,
        CancellationToken ct = default)
    {
        return await _session.RemoveByIdAsync(id, ct);
    }

    [McpServerTool(Name = "drhook_break_list"), Description(
        "List all active breakpoints with full descriptors — source, function, and exception. " +
        "Each entry includes: id (use with drhook_break_remove), location (file:line / Type.Method / typeName+phase), " +
        "hits (running count of times the breakpoint's policy evaluator has been entered), and " +
        "policy (when one is attached — condition / hitCount / logMessage / suspend, as the agent originally supplied). " +
        "Use this to discover IDs and to verify what's armed before stepping.")]
    public string BreakList()
    {
        return _session.ListBreakpoints();
    }

    [McpServerTool(Name = "drhook_break_clear"), Description(
        "Clear all breakpoints, or clear by category: 'source', 'function', or 'exception'. " +
        "Omit category to clear everything.")]
    public async Task<string> BreakClear(
        [Description("Optional category: 'source', 'function', or 'exception'. Omit to clear all.")] string? category = null,
        CancellationToken ct = default)
    {
        return await _session.ClearBreakpointsAsync(category, ct);
    }

    [McpServerTool(Name = "drhook_locals"), Description(
        "Inspect local variables + arguments at the current stop. " +
        "Returns variable names, values, types, and process metrics. Top frame only — " +
        "frame selection is not yet available (ADR-010 Tier 2).")]
    public async Task<string> Locals(
        [Description("What you expect the variables to show (optional but valuable)")] string? hypothesis = null,
        [Description("Object inspection depth (default 1)")] int depth = 1,
        CancellationToken ct = default)
    {
        return await _session.InspectVariablesAsync(depth, hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_stop"), Description(
        "End the active debugging session — the normal way to finish. " +
        "Borrowed sessions (started with drhook_attach) detach and leave the target running. " +
        "Owned sessions (started with drhook_launch) ask the target to exit gracefully — SIGTERM, escalating " +
        "to SIGKILL only if it does not exit within the ~2s natural-exit window (ADR-008). " +
        "Use drhook_detach to explicitly keep an attached target running, or drhook_kill to force-terminate. " +
        "Requires a hypothesis — your closing read of the session.")]
    public async Task<string> Stop(
        [Description("Your closing read — what you concluded or expected at session end (e.g. 'confirmed the off-by-one at line 42').")] string hypothesis,
        CancellationToken ct = default)
    {
        return await _session.StopAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_detach"), Description(
        "Detach the debugger and LEAVE THE TARGET RUNNING — the deliberate 'disconnect, keep it alive' action. " +
        "Borrowed sessions (drhook_attach): the target keeps running un-debugged. " +
        "Owned sessions (drhook_launch): the launched target is detached cleanly and left running — it reparents " +
        "(to launchd/PPID=1 on macOS) and keeps executing un-debugged (F-010-2; breakpoints are deactivated before " +
        "Detach so it does not hang). Contrast drhook_stop, which ENDS the session (graceful-terminate for an Owned " +
        "target), and drhook_kill, which force-terminates. Requires a hypothesis.")]
    public async Task<string> Detach(
        [Description("What you expect the target to do after you disconnect (e.g. 'keeps serving requests un-debugged').")] string hypothesis,
        CancellationToken ct = default)
    {
        return await _session.DetachAsync(hypothesis, ct);
    }

    [McpServerTool(Name = "drhook_kill"), Description(
        "Forcibly terminate the target — the ANOMALY escape hatch, NOT normal cleanup. Every invocation is worth " +
        "investigating: a well-behaved target ends via drhook_stop. " +
        "Owned sessions (drhook_launch): SIGTERM with a brief (~200ms) grace, then SIGKILL (DebugSession.Abandon, ADR-008). " +
        "Borrowed sessions (drhook_attach): SIGKILL of the attached target — a deliberate force (F-010-1); the substrate " +
        "holds the death-detection handle and tears the session down cleanly. Prefer drhook_detach to disconnect and " +
        "leave it running. Requires a hypothesis recording WHY force was needed.")]
    public async Task<string> Kill(
        [Description("WHY force is needed — what state the target is stuck in (e.g. 'eternal loop, ignores SIGTERM'). Every kill records its reason — the anomaly's value.")] string hypothesis,
        CancellationToken ct = default)
    {
        return await _session.KillAsync(hypothesis, ct);
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

    [McpServerTool(Name = "drhook_drain_console"), Description(
        "Drain the captured console output of a LAUNCHED debuggee — its stdout/stderr, isolated to a " +
        "DrHook-owned pipe (ADR-011 D2/D3) so it never corrupts this MCP channel. Returns chunks " +
        "(newest-last), each tagged stream='Stdout'|'Stderr' with a UTF-8 text fragment; concatenate " +
        "text in order to reconstruct the stream (chunk boundaries are arbitrary, not line-aligned). " +
        "'dropped' reports chunks lost to the bounded buffer since the previous drain. Pull this " +
        "periodically while stepping a console app to see what it printed. (Only Launched/Owned " +
        "sessions produce captured output; an Attached target owns its own console.)")]
    public string DrainConsole()
    {
        return _session.DrainConsoleAsJson();
    }

    [McpServerTool(Name = "drhook_drain_log"), Description(
        "Drain logpoint output — the rendered logMessage templates from breakpoints set with " +
        "suspend='none' (non-stopping logpoints), plus any condition-evaluation faults (isFault=true). " +
        "Returns records newest-last; 'dropped' reports records lost to the bounded buffer since the " +
        "previous drain. This is where a logpoint's output goes: set one with drhook_break_source " +
        "logMessage='v={value}' suspend='none', run, then drain here.")]
    public string DrainLog()
    {
        return _session.DrainLogAsJson();
    }
}
