// EngineSteppingSession — the BCL-only replacement for SteppingSessionManager (the netcoredbg-
// DAP-backed one in src/DrHook/Stepping/). Same 17-method async surface; consumers (DrHookTools
// and through it the MCP wire) see the same JSON-shaped responses. Backed entirely by
// SkyOmega.DrHook.Engine.DebugSession.
//
// One session at a time (the engine model — DI-injected as singleton). step_launch / step_run /
// step_test create the session; step_stop disposes it; other tools require IsActive.
//
// JSON shapes preserved at the TOP-LEVEL key level (status, operation, step, currentState,
// stoppedReason, hypothesis, prompt, metrics). Inner shapes (currentState, variables) carry
// engine-derived data — richer than the old DAP-via-JSON shape, but the top-level keys are
// compatible so existing MCP consumers don't break. The `metrics` block is currently a stub
// (the EventPipe-based gathering used by the old session manager is a separate concern that the
// snapshot tool already serves directly).
//
// Phase 3 close (finding 51): retires the netcoredbg dependency from the stepping path. Snapshot
// + processes tools still use DrHook (EventPipe — independent of netcoredbg DAP); retiring that
// project entirely is a follow-on cleanup.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Interop;

namespace SkyOmega.DrHook.Mcp;

public sealed class EngineSteppingSession : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private DebugSession? _session;
    private string _sessionHypothesis = "";
    private string _targetVersion = "unknown";
    private int _stepCount;
    private int _targetPid;
    // _launchedProcess removed (finding 64) — DebugSession.Launch now acquires its own
    // Process handle and kill-firsts internally on Dispose. EngineSteppingSession no
    // longer manages target lifecycle directly.
    // breakpoint id maps (so remove-by-natural-key resolves to engine ids)
    private readonly Dictionary<string, int> _lineBreakpoints = new();       // key = "file:line"
    private readonly Dictionary<string, int> _functionBreakpoints = new();   // key = "Type.Method"
    private readonly Dictionary<string, int> _exceptionFilters = new();      // key = "all" / "user-unhandled"

    // EA-5: substrate-anomaly buffer drained by drhook_drain_anomalies (per ADR-007 Phase 1).
    // Cross-session — anomalies accumulate until the consumer drains; capacity 256 (~128 KB max).
    private readonly BoundedAnomalySink _anomalies = new(capacity: 256);

    /// <summary>Drain the substrate-anomaly buffer as a structured JSON envelope. Anomalies
    /// array is newest-last; dropped count reports records lost to capacity since last drain
    /// (the substrate's honesty marker — no silent loss). Backing for drhook_drain_anomalies.
    /// Public to allow direct invocation from validation probes (e.g. Probe 41).</summary>
    public string DrainAnomaliesAsJson()
    {
        AnomalyDrainResult drain = _anomalies.Drain();
        JsonArray records = new();
        foreach (EngineAnomaly a in drain.Anomalies)
        {
            JsonObject record = new()
            {
                ["capturedAt"] = a.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
                ["kind"]       = a.Kind.ToString(),
                ["thread"]     = a.Thread,
                ["operation"]  = a.Operation,
                ["observed"]   = a.Observed,
                ["expected"]   = a.Expected,
            };
            if (a.Context is { Count: > 0 } ctx)
            {
                JsonObject contextNode = new();
                foreach ((string k, string v) in ctx) contextNode[k] = v;
                record["context"] = contextNode;
            }
            records.Add(record);
        }

        return Render(new JsonObject
        {
            ["status"]   = "ok",
            ["count"]    = drain.Anomalies.Count,
            ["dropped"]  = drain.Dropped,
            ["capacity"] = _anomalies.Capacity,
            ["anomalies"] = records,
            ["prompt"] = drain.Anomalies.Count == 0 && drain.Dropped == 0
                ? "No anomalies captured since previous drain."
                : $"{drain.Anomalies.Count} anomalies surfaced ({drain.Dropped} dropped to capacity since previous drain). Each is structured evidence of a substrate-correctness invariant that wasn't upheld — review the Kind and observed-vs-expected delta to decide whether to escalate.",
        });
    }

    public bool IsActive => _session is not null;

    // ─── Session lifecycle ────────────────────────────────────────────────────────────────────

    public Task<string> LaunchAsync(int pid, string sourceFile, int line, string hypothesis, CancellationToken ct)
    {
        if (IsActive) return Task.FromResult(Error("A stepping session is already active. Use drhook:step-stop first."));

        try
        {
            try { _targetVersion = Process.GetProcessById(pid).MainModule?.FileVersionInfo.FileVersion ?? "unknown"; }
            catch { _targetVersion = "unknown"; }

            _sessionHypothesis = hypothesis;
            _stepCount = 0;
            _targetPid = pid;

            _session = DebugSession.Attach(pid, _anomalies);
            // Setup stop: the engine breaks on attach (Debugger.IsAttached transition). Drain it.
            _session.WaitForStop(TimeSpan.FromSeconds(10));

            // Set the initial breakpoint and run to it.
            int id = _session.SetBreakpointAtLine(ModuleSubstrForFile(sourceFile), sourceFile, line);
            if (id == 0) return Task.FromResult(Error($"Could not set breakpoint at {sourceFile}:{line}."));
            _lineBreakpoints[KeyLine(sourceFile, line)] = id;

            _session.Resume();
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) return Task.FromResult(Error("Timed out waiting for the breakpoint to hit."));

            JsonObject result = new()
            {
                ["status"] = "attached",
                ["pid"] = pid,
                ["assemblyVersion"] = _targetVersion,
                ["breakpoint"] = new JsonObject { ["file"] = sourceFile, ["line"] = line },
                ["hypothesis"] = hypothesis,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
                ["prompt"] = $"Attached and stopped at breakpoint. Compare state with hypothesis: \"{hypothesis}\""
            };
            return Task.FromResult(Render(result));
        }
        catch (Exception ex)
        {
            CleanupSession();
            return Task.FromResult(Error($"Failed to launch stepping session: {ex.Message}"));
        }
    }

    public Task<string> RunAsync(
        string program, string[] args, string? cwd,
        string sourceFile, int line, string hypothesis,
        Dictionary<string, string>? env,
        CancellationToken ct)
    {
        if (IsActive) return Task.FromResult(Error("A stepping session is already active. Use drhook:step-stop first."));

        _sessionHypothesis = hypothesis;
        _targetVersion = "launched";
        _stepCount = 0;

        try
        {
            // env override is not yet plumbed through DebugSession.Launch (Phase 3 polish item —
            // dedicated env block via CreateProcessForLaunch). For now the launched child inherits
            // our env, which covers the common cases (no per-launch override required).
            _session = DebugSession.Launch(program, args, cwd, _anomalies);

            // Capture the launched PID for status reporting. DebugSession.Launch (finding 64)
            // now owns the Process handle internally and kill-firsts on Dispose — no separate
            // _launchedProcess management here.
            _targetPid = _session.ProcessId;

            // Setup stop (Debugger.Break() in user code or the attach break).
            _session.WaitForStop(TimeSpan.FromSeconds(10));

            int id = _session.SetBreakpointAtLine(ModuleSubstrForFile(sourceFile), sourceFile, line);
            if (id == 0) { CleanupSession(); return Task.FromResult(Error($"Could not set breakpoint at {sourceFile}:{line}.")); }
            _lineBreakpoints[KeyLine(sourceFile, line)] = id;

            _session.Resume();
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) { CleanupSession(); return Task.FromResult(Error("Timed out waiting for the breakpoint to hit.")); }

            JsonObject result = new()
            {
                ["status"] = "launched",
                ["program"] = program,
                ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["assemblyVersion"] = _targetVersion,
                ["pid"] = _targetPid,
                ["breakpoint"] = new JsonObject { ["file"] = sourceFile, ["line"] = line },
                ["hypothesis"] = hypothesis,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
                ["prompt"] = $"Launched and stopped at breakpoint. Compare state with hypothesis: \"{hypothesis}\""
            };
            return Task.FromResult(Render(result));
        }
        catch (Exception ex)
        {
            CleanupSession();
            return Task.FromResult(Error($"Failed to launch program: {ex.Message}"));
        }
    }

    public Task<string> RunTestAsync(string project, string? filter, string? cwd, string sourceFile, int line, string hypothesis, CancellationToken ct)
    {
        // Phase 3 polish — the VSTEST_HOST_DEBUG dance (launch dotnet test, wait for the test
        // host to print its PID, then Attach to it) is not yet ported. Surface this as a
        // structured "not implemented yet" so the consumer gets a clean signal rather than a hang.
        return Task.FromResult(Error("drhook_step_test is a Phase 3 polish item — the VSTEST_HOST_DEBUG / testhost-PID-discovery dance is not yet ported to the DrHook.Engine path. Use drhook_step_run or drhook_step_launch in the meantime."));
    }

    public Task<string> StopAsync(CancellationToken ct)
    {
        if (!IsActive) return Task.FromResult(Error("No active stepping session."));

        JsonObject summary = new()
        {
            ["status"] = "stopped",
            ["totalSteps"] = _stepCount,
            ["sessionHypothesis"] = _sessionHypothesis,
            ["assemblyVersion"] = _targetVersion,
            ["prompt"] = $"Session complete after {_stepCount} steps. " +
                         $"Did the observations confirm or challenge the hypothesis: \"{_sessionHypothesis}\"?"
        };

        CleanupSession();
        return Task.FromResult(Render(summary));
    }

    // ─── Step operations ──────────────────────────────────────────────────────────────────────

    public Task<string> StepNextAsync(string? hypothesis, CancellationToken ct) => StepOperation("next", s => s.StepOver(), hypothesis);
    public Task<string> StepIntoAsync(string? hypothesis, CancellationToken ct) => StepOperation("stepIn", s => s.StepInto(), hypothesis);
    public Task<string> StepOutAsync(string? hypothesis, CancellationToken ct)  => StepOperation("stepOut", s => s.StepOut(), hypothesis);

    public Task<string> ContinueAsync(string? hypothesis, bool waitForBreakpoint, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session. Use drhook:step-launch first."));
        try
        {
            _session.Resume();
            if (!waitForBreakpoint)
            {
                return Task.FromResult(Render(new JsonObject
                {
                    ["operation"] = "continue",
                    ["status"] = "running",
                    ["assemblyVersion"] = _targetVersion,
                    ["prompt"] = "Process resumed without waiting. Use drhook_step_pause to interrupt or drhook_step_continue with waitForBreakpoint=true to block on a stop."
                }));
            }
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) return Task.FromResult(Error("Timed out waiting for a stop."));

            JsonObject result = new()
            {
                ["operation"] = "continue",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["prompt"] = hypothesis is not null
                ? $"Continued to a stop. Compare state with hypothesis: \"{hypothesis}\""
                : "Continued to a stop. Describe what you observe at the new location.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"Continue failed: {ex.Message}")); }
    }

    public Task<string> PauseAsync(string? hypothesis, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        try
        {
            _session.Pause();
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromSeconds(10));
            if (stop is null) return Task.FromResult(Error("Pause timed out — the engine did not surface a stop within budget."));

            JsonObject result = new()
            {
                ["operation"] = "pause",
                ["assemblyVersion"] = _targetVersion,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["prompt"] = "Execution interrupted. Inspect with drhook_step_vars or step through.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"Pause failed: {ex.Message}")); }
    }

    private Task<string> StepOperation(string operationName, Action<DebugSession> step, string? hypothesis)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session. Use drhook:step-launch first."));
        try
        {
            _stepCount++;
            step(_session);
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) return Task.FromResult(Error("Step did not complete within budget."));

            JsonObject result = new()
            {
                ["operation"] = operationName,
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["metrics"] = StubMetrics();
            result["prompt"] = hypothesis is not null
                ? $"Step {_stepCount} complete ({operationName}). Compare state with hypothesis: \"{hypothesis}\""
                : $"Step {_stepCount} complete ({operationName}). Describe what you observe.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"{operationName} failed: {ex.Message}")); }
    }

    // ─── Breakpoints ─────────────────────────────────────────────────────────────────────────

    public Task<string> SetBreakpointAsync(string sourceFile, int line, string? condition, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        if (!string.IsNullOrEmpty(condition))
            // Conditional breakpoints via Roslyn would compose probe-25/29 walkers; not in this slice.
            return Task.FromResult(Error("Conditional breakpoints are a polish item — the Roslyn walker lives in the probes today and hasn't been extracted into DrHook.Engine.Expressions yet."));

        int id = _session.SetBreakpointAtLine(ModuleSubstrForFile(sourceFile), sourceFile, line);
        if (id == 0) return Task.FromResult(Error($"Could not set breakpoint at {sourceFile}:{line} — module/PDB unavailable or no sequence point at that line."));
        _lineBreakpoints[KeyLine(sourceFile, line)] = id;

        return Task.FromResult(Render(new JsonObject
        {
            ["status"] = "added",
            ["type"] = "source",
            ["file"] = sourceFile,
            ["line"] = line,
            ["id"] = id,
            ["prompt"] = $"Breakpoint added at {sourceFile}:{line} (id={id}). Use drhook_step_continue to run to it."
        }));
    }

    public Task<string> SetFunctionBreakpointAsync(string functionName, string? condition, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        if (!string.IsNullOrEmpty(condition))
            return Task.FromResult(Error("Conditional breakpoints are a polish item (see drhook_step_breakpoint)."));

        (string moduleSubstr, string typeName, string methodName) = SplitFunction(functionName);
        int id = _session.SetBreakpoint(moduleSubstr, typeName, methodName);
        if (id == 0) return Task.FromResult(Error($"Could not resolve function '{functionName}' (looked for type '{typeName}', method '{methodName}' in module '{moduleSubstr}')."));
        _functionBreakpoints[functionName] = id;

        return Task.FromResult(Render(new JsonObject
        {
            ["status"] = "added",
            ["type"] = "function",
            ["function"] = functionName,
            ["id"] = id,
            ["prompt"] = $"Function breakpoint added at {functionName} entry (id={id})."
        }));
    }

    public Task<string> SetExceptionBreakpointAsync(string filter, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));

        // Map the DAP-style filter names to engine filters. "all" = any type, first-chance.
        // "user-unhandled" = any type, unhandled. The engine model is type-first; the * wildcard
        // is what DAP "all" translates to.
        (string typeName, ExceptionStopKind phase) = filter switch
        {
            "all"             => (ExceptionFilterInfo.AnyType, ExceptionStopKind.FirstChance),
            "user-unhandled"  => (ExceptionFilterInfo.AnyType, ExceptionStopKind.Unhandled),
            _ => (filter, ExceptionStopKind.FirstChance), // treat as a literal type name
        };
        int id = _session.ArmExceptionFilter(typeName, phase);
        _exceptionFilters[filter] = id;

        return Task.FromResult(Render(new JsonObject
        {
            ["status"] = "added",
            ["type"] = "exception",
            ["filter"] = filter,
            ["typeName"] = typeName,
            ["phase"] = phase.ToString(),
            ["id"] = id,
            ["prompt"] = $"Exception filter armed ({filter} → type=\"{typeName}\" phase={phase}, id={id})."
        }));
    }

    public Task<string> RemoveBreakpointAsync(string sourceFile, int line, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        string key = KeyLine(sourceFile, line);
        if (!_lineBreakpoints.TryGetValue(key, out int id) || !_session.RemoveBreakpoint(id))
            return Task.FromResult(Error($"No breakpoint registered at {sourceFile}:{line}."));
        _lineBreakpoints.Remove(key);
        return Task.FromResult(Render(new JsonObject { ["status"] = "removed", ["type"] = "source", ["file"] = sourceFile, ["line"] = line, ["id"] = id }));
    }

    public Task<string> RemoveFunctionBreakpointAsync(string functionName, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        if (!_functionBreakpoints.TryGetValue(functionName, out int id) || !_session.RemoveBreakpoint(id))
            return Task.FromResult(Error($"No function breakpoint registered for '{functionName}'."));
        _functionBreakpoints.Remove(functionName);
        return Task.FromResult(Render(new JsonObject { ["status"] = "removed", ["type"] = "function", ["function"] = functionName, ["id"] = id }));
    }

    public Task<string> RemoveExceptionBreakpointAsync(string filter, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        if (!_exceptionFilters.TryGetValue(filter, out int id) || !_session.RemoveExceptionFilter(id))
            return Task.FromResult(Error($"No exception filter '{filter}' is armed."));
        _exceptionFilters.Remove(filter);
        return Task.FromResult(Render(new JsonObject { ["status"] = "removed", ["type"] = "exception", ["filter"] = filter, ["id"] = id }));
    }

    public string ListBreakpoints()
    {
        if (_session is null) return Error("No active stepping session.");

        JsonArray source = new();
        foreach ((string key, int id) in _lineBreakpoints)
        {
            int colon = key.LastIndexOf(':');
            source.Add(new JsonObject
            {
                ["id"] = id,
                ["file"] = colon > 0 ? key[..colon] : key,
                ["line"] = colon > 0 && int.TryParse(key[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0,
            });
        }

        JsonArray function = new();
        foreach ((string fn, int id) in _functionBreakpoints)
            function.Add(new JsonObject { ["id"] = id, ["function"] = fn });

        JsonArray exception = new();
        foreach ((string filter, int id) in _exceptionFilters)
            exception.Add(new JsonObject { ["id"] = id, ["filter"] = filter });

        return Render(new JsonObject
        {
            ["status"] = "ok",
            ["source"] = source,
            ["function"] = function,
            ["exception"] = exception,
            ["count"] = source.Count + function.Count + exception.Count,
        });
    }

    public Task<string> ClearBreakpointsAsync(string? category, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));

        int sourceCleared = 0, functionCleared = 0, exceptionCleared = 0;
        bool clearSource    = category is null || category == "source";
        bool clearFunction  = category is null || category == "function";
        bool clearException = category is null || category == "exception";

        if (clearSource)
        {
            foreach (int id in _lineBreakpoints.Values) if (_session.RemoveBreakpoint(id)) sourceCleared++;
            _lineBreakpoints.Clear();
        }
        if (clearFunction)
        {
            foreach (int id in _functionBreakpoints.Values) if (_session.RemoveBreakpoint(id)) functionCleared++;
            _functionBreakpoints.Clear();
        }
        if (clearException)
        {
            foreach (int id in _exceptionFilters.Values) if (_session.RemoveExceptionFilter(id)) exceptionCleared++;
            _exceptionFilters.Clear();
        }

        return Task.FromResult(Render(new JsonObject
        {
            ["status"] = "cleared",
            ["category"] = category ?? "all",
            ["sourceCleared"] = sourceCleared,
            ["functionCleared"] = functionCleared,
            ["exceptionCleared"] = exceptionCleared,
        }));
    }

    // ─── Variables ────────────────────────────────────────────────────────────────────────────

    public Task<string> InspectVariablesAsync(int depth, string? hypothesis, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        try
        {
            IReadOnlyList<LocalValue> locals = _session.GetLocals(depth);
            IReadOnlyList<ArgumentValue> args = _session.GetArguments(depth);

            JsonArray vars = new();
            foreach (LocalValue l in locals)
                vars.Add(LocalValueToJson(l));
            for (int i = 0; i < args.Count; i++)
                vars.Add(ArgumentValueToJson(args[i], i));

            JsonObject result = new()
            {
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["variableCount"] = vars.Count,
                ["variables"] = vars,
                ["metrics"] = StubMetrics(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["prompt"] = hypothesis is not null
                ? $"Step {_stepCount} variables inspected. Compare values with hypothesis: \"{hypothesis}\""
                : $"Step {_stepCount} variables inspected. Describe what you observe.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"Variable inspection failed: {ex.Message}")); }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────

    private JsonObject BuildCurrentState()
    {
        if (_session is null) return new JsonObject();
        IReadOnlyList<string> frames = _session.GetStackFrames();
        return new JsonObject
        {
            ["topFrame"] = frames.Count > 0 ? frames[0] : "(no frame)",
            ["stack"] = new JsonArray(frames.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        };
    }

    private static string MapStopReason(StopInfo stop) => stop.Reason switch
    {
        StopReason.Breakpoint => "breakpoint",
        StopReason.Step => "step",
        StopReason.Break => "debuggerBreak",
        StopReason.Exception => $"exception:{stop.ExceptionKind}",
        StopReason.EvalComplete => "evalComplete",
        StopReason.EvalException => "evalException",
        StopReason.Pause => "pause",
        StopReason.ConditionError => "conditionError",
        StopReason.ProcessExited => "processExited",
        _ => stop.Reason.ToString().ToLowerInvariant(),
    };

    private static JsonObject StubMetrics()
        => new() { ["note"] = "metrics moved to drhook_snapshot (EventPipe) — this field is preserved for shape compat" };

    private static JsonNode LocalValueToJson(LocalValue l)
    {
        JsonObject node = new()
        {
            ["scope"] = "local",
            ["name"] = l.Name,
            ["elementType"] = $"0x{l.ElementType:X2}",
        };
        if (l.RawValue is { } raw) node["value"] = raw;
        if (l.StringValue is not null) node["string"] = l.StringValue;
        if (l.Fields is { Count: > 0 } fs) node["fields"] = FieldsToJson(fs);
        return node;
    }

    private static JsonNode ArgumentValueToJson(ArgumentValue a, int index)
    {
        JsonObject node = new()
        {
            ["scope"] = "argument",
            ["name"] = index == 0 ? "this" : $"arg{index}",
            ["elementType"] = $"0x{a.ElementType:X2}",
        };
        if (a.RawValue is { } raw) node["value"] = raw;
        if (a.StringValue is not null) node["string"] = a.StringValue;
        if (a.Fields is { Count: > 0 } fs) node["fields"] = FieldsToJson(fs);
        return node;
    }

    private static JsonArray FieldsToJson(IReadOnlyList<FieldValue> fields)
    {
        JsonArray arr = new();
        foreach (FieldValue f in fields)
        {
            JsonObject node = new()
            {
                ["name"] = f.Name,
                ["elementType"] = $"0x{f.ElementType:X2}",
            };
            if (f.RawValue is { } raw) node["value"] = raw;
            if (f.StringValue is not null) node["string"] = f.StringValue;
            if (f.Fields is { Count: > 0 } sub) node["fields"] = FieldsToJson(sub);
            arr.Add(node);
        }
        return arr;
    }

    private static string ModuleSubstrForFile(string sourceFile)
    {
        // Naive heuristic: the assembly name typically matches the project / file stem. For
        // file-based apps and most projects this works; richer resolution (search loaded modules
        // for one whose PDB references the file) is a polish item.
        string stem = Path.GetFileNameWithoutExtension(sourceFile);
        return stem.Length > 0 ? stem : sourceFile;
    }

    private static (string Module, string Type, string Method) SplitFunction(string functionName)
    {
        // Accept "Namespace.Type.Method", "Type.Method", or a bare "Method" (last-segment fallback).
        int lastDot = functionName.LastIndexOf('.');
        if (lastDot < 0) return ("", "", functionName);
        string method = functionName[(lastDot + 1)..];
        string typePart = functionName[..lastDot];
        int prevDot = typePart.LastIndexOf('.');
        if (prevDot < 0) return ("", typePart, method);
        string typeName = typePart;          // full "Namespace.Type"
        string module  = typePart[..prevDot]; // best-effort module guess from the namespace prefix
        return (module, typeName, method);
    }

    private static string KeyLine(string file, int line) => $"{file}:{line.ToString(CultureInfo.InvariantCulture)}";

    private static string Error(string message)
        => Render(new JsonObject { ["status"] = "error", ["message"] = message });

    // ToJsonString on the JsonNode tree avoids the reflection-based serializer (which is
    // disabled when the host runs trimmed/AOT — e.g., the .NET 10 file-based-app context used
    // by Probe 41). Equivalent output for the JsonObject inputs we use.
    private static string Render(JsonObject obj) => obj.ToJsonString(Indented);

    private void CleanupSession()
    {
        if (_session is not null)
        {
            // Finding 64 — substrate-enforced lifecycle: DebugSession.Dispose handles the
            // kill-first protocol internally for Owned (Launched) sessions. Was previously
            // a manual Kill-then-Dispose dance in this method; substrate now owns the
            // ordering. CleanupSession is just a Dispose + bookkeeping reset.
            try
            {
                _session.Dispose();
            }
            catch (Exception ex)
            {
                // EA capture (UnexpectedCleanupException): DebugSession.Dispose threw.
                // The substrate's Interlocked gates (ENG-DS-1) and idempotent native frees mean
                // most exception paths are now structurally avoided, but anything that escapes
                // surfaces here rather than silently disappearing.
                _anomalies.OnAnomaly(new EngineAnomaly(
                    DateTimeOffset.UtcNow, AnomalyKind.UnexpectedCleanupException, "mcp-request",
                    "CleanupSession.SessionDispose",
                    Observed: $"{ex.GetType().Name}: {ex.Message}",
                    Expected: "DebugSession.Dispose completes cleanly",
                    Context: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().FullName ?? ex.GetType().Name }));
            }
            _session = null;
        }
        _lineBreakpoints.Clear();
        _functionBreakpoints.Clear();
        _exceptionFilters.Clear();
        _stepCount = 0;
        _targetPid = 0;
        _targetVersion = "unknown";
        _sessionHypothesis = "";
    }

    public void Dispose() => CleanupSession();
}
