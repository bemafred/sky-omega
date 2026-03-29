using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyOmega.DrHook.Stepping;

/// <summary>
/// Manages a single controlled stepping session via DAP/netcoredbg.
///
/// This is where Claude Code steps through code line by line and narrates
/// what it sees in the terminal. The core of the "Inspect" discipline.
///
/// Each operation captures a hypothesis (when provided) so that the
/// delta between expectation and reality is part of the record.
/// </summary>
public sealed class SteppingSessionManager
{
    private DapClient? _client;
    private int _activeThreadId;
    private string? _sessionHypothesis;
    private string? _targetVersion;
    private int _stepCount;
    private bool _ownsProcess;

    public bool IsActive => _client?.IsConnected == true;

    public async Task<string> LaunchAsync(int pid, string sourceFile, int line, string hypothesis, CancellationToken ct)
    {
        if (IsActive)
            return Error("A stepping session is already active. Use drhook:step-stop first.");

        // Capture target version for anchoring
        try
        {
            var proc = Process.GetProcessById(pid);
            _targetVersion = proc.MainModule?.FileVersionInfo.FileVersion ?? "unknown";
        }
        catch
        {
            _targetVersion = "unknown";
        }

        var netcoredbgPath = NetCoreDbgLocator.LocateOrThrow();

        _client = new DapClient();
        _sessionHypothesis = hypothesis;
        _stepCount = 0;
        _ownsProcess = false;

        try
        {
            await _client.LaunchAsync(netcoredbgPath, ct);
            await _client.AttachAsync(pid, ct);
            await _client.SetBreakpointAsync(sourceFile, line, ct);
            await _client.ConfigurationDoneAsync(ct);

            // Get threads — use first thread for initial continue only
            var threads = await _client.GetThreadsAsync(ct);
            var threadArray = threads["threads"] as JsonArray;
            _activeThreadId = threadArray?[0]?["id"]?.GetValue<int>() ?? 1;

            // Continue execution — process runs until breakpoint is hit.
            // WaitForStoppedAsync blocks until DAP sends a "stopped" event;
            // the cancellation token is the only timeout.
            // Use the threadId from the stopped event as the active thread —
            // for async code, the breakpoint may fire on a thread pool thread,
            // not the main thread.
            await _client.ContinueAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            // Get current state
            var state = await GetCurrentStateAsync(ct);

            return JsonSerializer.Serialize(new JsonObject
            {
                ["status"] = "attached",
                ["pid"] = pid,
                ["assemblyVersion"] = _targetVersion,
                ["breakpoint"] = new JsonObject { ["file"] = sourceFile, ["line"] = line },
                ["hypothesis"] = hypothesis,
                ["currentState"] = state,
                ["instruction"] = "Use drhook:step-next (over), drhook:step-into (in), drhook:step-out (out) to navigate. Use drhook:step-continue to run to next breakpoint. Use drhook:step-vars for variable details."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            return Error($"Failed to launch stepping session: {ex.Message}");
        }
    }

    public async Task<string> RunAsync(
        string program, string[] args, string? cwd,
        string sourceFile, int line, string hypothesis,
        CancellationToken ct)
    {
        if (IsActive)
            return Error("A stepping session is already active. Use drhook:step-stop first.");

        var netcoredbgPath = NetCoreDbgLocator.LocateOrThrow();

        _client = new DapClient();
        _sessionHypothesis = hypothesis;
        _targetVersion = "launched";
        _stepCount = 0;
        _ownsProcess = true;

        try
        {
            await _client.LaunchAsync(netcoredbgPath, ct);
            await _client.LaunchTargetAsync(program, args, cwd, stopAtEntry: true, ct);
            await _client.SetBreakpointAsync(sourceFile, line, ct);
            await _client.ConfigurationDoneAsync(ct);

            // With stopAtEntry, netcoredbg sends a "stopped" event after configurationDone.
            // We must wait for it before threads are available.
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            // Now continue from entry to the actual breakpoint.
            await _client.ContinueAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            var state = await GetCurrentStateAsync(ct);

            return JsonSerializer.Serialize(new JsonObject
            {
                ["status"] = "launched",
                ["program"] = program,
                ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["breakpoint"] = new JsonObject { ["file"] = sourceFile, ["line"] = line },
                ["hypothesis"] = hypothesis,
                ["currentState"] = state,
                ["instruction"] = "Process launched and stopped at breakpoint. Use drhook:step-next, drhook:step-into, drhook:step-vars to inspect."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            return Error($"Failed to run stepping session: {ex.Message}");
        }
    }

    public async Task<string> StepNextAsync(string? hypothesis, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            _stepCount++;
            await _client.StepNextAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            var state = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "next",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            if (hypothesis is not null)
                result["hypothesis"] = hypothesis;

            result["prompt"] = hypothesis is not null
                ? $"Step {_stepCount} complete (next). Compare state with hypothesis: \"{hypothesis}\""
                : $"Step {_stepCount} complete (next). Describe what you observe and whether it matches expectations.";

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Step failed: {ex.Message}");
        }
    }

    public async Task<string> StepIntoAsync(string? hypothesis, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            _stepCount++;
            await _client.StepInAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            var state = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "stepIn",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            if (hypothesis is not null)
                result["hypothesis"] = hypothesis;

            result["prompt"] = hypothesis is not null
                ? $"Step {_stepCount} complete (into). Compare state with hypothesis: \"{hypothesis}\""
                : $"Step {_stepCount} complete (into). Describe what you observe — did stepping into the call reveal the expected code path?";

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Step-into failed: {ex.Message}");
        }
    }

    public async Task<string> StepOutAsync(string? hypothesis, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            _stepCount++;
            await _client.StepOutAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            var state = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "stepOut",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            if (hypothesis is not null)
                result["hypothesis"] = hypothesis;

            result["prompt"] = hypothesis is not null
                ? $"Step {_stepCount} complete (out). Compare state with hypothesis: \"{hypothesis}\""
                : $"Step {_stepCount} complete (out). Describe the return — did execution return to the expected caller frame?";

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Step-out failed: {ex.Message}");
        }
    }

    public async Task<string> ContinueAsync(string? hypothesis, bool waitForBreakpoint, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            await _client.ContinueAsync(_activeThreadId, ct);

            if (waitForBreakpoint)
            {
                // Block until a breakpoint is hit — the cancellation token is the only timeout.
                UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

                var state = await GetCurrentStateAsync(ct);

                var result = new JsonObject
                {
                    ["operation"] = "continue",
                    ["step"] = _stepCount,
                    ["assemblyVersion"] = _targetVersion,
                    ["currentState"] = state,
                };

                if (hypothesis is not null)
                    result["hypothesis"] = hypothesis;

                result["prompt"] = hypothesis is not null
                    ? $"Continued to breakpoint. Compare state with hypothesis: \"{hypothesis}\""
                    : "Continued to breakpoint. Describe what you observe at the current location.";

                return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                // Return immediately — caller will use step-pause to interrupt.
                var result = new JsonObject
                {
                    ["operation"] = "continue",
                    ["step"] = _stepCount,
                    ["assemblyVersion"] = _targetVersion,
                    ["status"] = "running",
                };

                if (hypothesis is not null)
                    result["hypothesis"] = hypothesis;

                result["prompt"] = "Process running freely. Use drhook:step-pause to interrupt.";

                return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (Exception ex)
        {
            return Error($"Continue failed: {ex.Message}");
        }
    }

    public async Task<string> PauseAsync(CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            await _client.PauseAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            var state = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "pause",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            result["prompt"] = "Process paused. Inspect current location and variables to understand where execution was interrupted.";

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Pause failed: {ex.Message}");
        }
    }

    public async Task<string> SetBreakpointAsync(string sourceFile, int line, string? condition, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            var response = await _client.SetBreakpointAsync(sourceFile, line, condition, ct);
            var breakpoints = response["breakpoints"] as JsonArray;
            var verified = breakpoints?[0]?["verified"]?.GetValue<bool>() ?? false;

            var result = new JsonObject
            {
                ["operation"] = "setBreakpoint",
                ["sourceFile"] = sourceFile,
                ["line"] = line,
                ["verified"] = verified,
                ["assemblyVersion"] = _targetVersion,
            };

            if (condition is not null)
                result["condition"] = condition;

            result["note"] = "DAP uses set-and-replace semantics: this replaces ALL breakpoints in this file. Multi-breakpoint-per-file registry is deferred.";

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Set breakpoint failed: {ex.Message}");
        }
    }

    public async Task<string> SetFunctionBreakpointAsync(string functionName, string? condition, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            var response = await _client.SetFunctionBreakpointsAsync(functionName, condition, ct);
            var breakpoints = response["breakpoints"] as JsonArray;
            var verified = breakpoints?[0]?["verified"]?.GetValue<bool>() ?? false;

            var result = new JsonObject
            {
                ["operation"] = "setFunctionBreakpoint",
                ["functionName"] = functionName,
                ["verified"] = verified,
                ["assemblyVersion"] = _targetVersion,
            };

            if (condition is not null)
                result["condition"] = condition;

            result["note"] = "DAP uses set-and-replace semantics: this replaces ALL function breakpoints. Multi-breakpoint registry is deferred.";

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Set function breakpoint failed: {ex.Message}");
        }
    }

    public async Task<string> SetExceptionBreakpointAsync(string filter, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            await _client.SetExceptionBreakpointsAsync([filter], ct);

            var result = new JsonObject
            {
                ["operation"] = "setExceptionBreakpoint",
                ["filter"] = filter,
                ["assemblyVersion"] = _targetVersion,
                ["note"] = "Exception breakpoints use DAP filters ('all' or 'user-unhandled'), not exception type names. Type-specific exceptions require DrHook.Engine.",
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Set exception breakpoint failed: {ex.Message}");
        }
    }

    public async Task<string> InspectVariablesAsync(int depth, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            var stackTrace = await _client.GetStackTraceAsync(_activeThreadId, ct);
            var frames = stackTrace["stackFrames"] as JsonArray;
            if (frames is null || frames.Count == 0)
                return Error("No stack frames available.");

            var topFrameId = frames[0]?["id"]?.GetValue<int>() ?? 0;
            var scopes = await _client.GetScopesAsync(topFrameId, ct);
            var scopeArray = scopes["scopes"] as JsonArray;

            var allVars = new JsonArray();

            if (scopeArray is not null)
            {
                foreach (var scope in scopeArray)
                {
                    var varRef = scope?["variablesReference"]?.GetValue<int>() ?? 0;
                    if (varRef <= 0) continue;

                    var scopeName = scope?["name"]?.GetValue<string>() ?? "unknown";
                    var vars = await _client.GetVariablesAsync(varRef, ct);
                    var variables = vars["variables"] as JsonArray;

                    if (variables is not null)
                    {
                        foreach (var v in variables)
                        {
                            if (IsNoiseVariable(v)) continue;

                            var node = new JsonObject
                            {
                                ["scope"] = scopeName,
                                ["name"] = v?["name"]?.DeepClone(),
                                ["value"] = v?["value"]?.DeepClone(),
                                ["type"] = v?["type"]?.DeepClone(),
                            };

                            var childRef = v?["variablesReference"]?.GetValue<int>() ?? 0;
                            if (childRef > 0 && depth > 1)
                            {
                                var visited = new HashSet<int>();
                                node["children"] = await ExpandVariableAsync(childRef, depth - 1, visited, ct);
                            }

                            allVars.Add(node);
                        }
                    }
                }
            }

            return JsonSerializer.Serialize(new JsonObject
            {
                ["step"] = _stepCount,
                ["variableCount"] = allVars.Count,
                ["variables"] = allVars
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Variable inspection failed: {ex.Message}");
        }
    }

    public async Task<string> StopAsync(CancellationToken ct)
    {
        if (!IsActive)
            return Error("No active stepping session.");

        var summary = new JsonObject
        {
            ["status"] = "stopped",
            ["totalSteps"] = _stepCount,
            ["sessionHypothesis"] = _sessionHypothesis,
            ["assemblyVersion"] = _targetVersion,
            ["prompt"] = $"Session complete after {_stepCount} steps. " +
                         $"Did the observations confirm or challenge the hypothesis: \"{_sessionHypothesis}\"?"
        };

        await CleanupAsync();

        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<JsonObject> GetCurrentStateAsync(CancellationToken ct)
    {
        if (_client is null) return new JsonObject { ["error"] = "No client" };

        var stackTrace = await _client.GetStackTraceAsync(_activeThreadId, ct);
        var frames = stackTrace["stackFrames"] as JsonArray;

        if (frames is null || frames.Count == 0)
            return new JsonObject { ["location"] = "unknown" };

        var topFrame = frames[0] as JsonObject;
        var source = topFrame?["source"] as JsonObject;

        return new JsonObject
        {
            ["file"] = source?["path"]?.DeepClone(),
            ["line"] = topFrame?["line"]?.DeepClone(),
            ["column"] = topFrame?["column"]?.DeepClone(),
            ["functionName"] = topFrame?["name"]?.DeepClone(),
            ["callStackDepth"] = frames.Count,
            ["topFrames"] = new JsonArray(frames.Take(5).Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f?["name"]?.DeepClone(),
                ["line"] = f?["line"]?.DeepClone(),
            }).ToArray())
        };
    }

    private async Task<JsonArray> ExpandVariableAsync(int variablesReference, int remainingDepth, HashSet<int> visited, CancellationToken ct)
    {
        if (!visited.Add(variablesReference) || _client is null)
            return new JsonArray();

        var response = await _client.GetVariablesAsync(variablesReference, ct);
        var variables = response["variables"] as JsonArray;
        var result = new JsonArray();

        if (variables is not null)
        {
            foreach (var v in variables)
            {
                if (IsNoiseVariable(v)) continue;

                var node = new JsonObject
                {
                    ["name"] = v?["name"]?.DeepClone(),
                    ["value"] = v?["value"]?.DeepClone(),
                    ["type"] = v?["type"]?.DeepClone(),
                };

                var childRef = v?["variablesReference"]?.GetValue<int>() ?? 0;
                if (childRef > 0 && remainingDepth > 1)
                {
                    node["children"] = await ExpandVariableAsync(childRef, remainingDepth - 1, visited, ct);
                }

                result.Add(node);
            }
        }

        return result;
    }

    private void UpdateActiveThread(JsonObject stoppedEvent)
    {
        var threadId = stoppedEvent["threadId"]?.GetValue<int>();
        if (threadId is not null and > 0)
            _activeThreadId = threadId.Value;
    }

    private static bool IsNoiseVariable(JsonNode? v)
    {
        var name = v?["name"]?.GetValue<string>();
        var type = v?["type"]?.GetValue<string>();

        // Indexer properties that netcoredbg can't evaluate without an index argument
        if (type is "System.Reflection.TargetParameterCountException")
            return true;

        // Interface reimplementations that duplicate the primary members
        if (name is not null && name.StartsWith("System.Collections."))
            return true;

        // Self-referential BCL properties that cause redundant expansion
        if (name is "SyncRoot" or "Static members")
            return true;

        return false;
    }

    private async Task CleanupAsync()
    {
        if (_client is not null)
        {
            await _client.DisconnectAsync(terminateDebuggee: _ownsProcess, CancellationToken.None);
            await _client.DisposeAsync();
            _client = null;
        }
        _activeThreadId = 0;
        _sessionHypothesis = null;
        _targetVersion = null;
        _stepCount = 0;
        _ownsProcess = false;
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new JsonObject { ["error"] = message });
}
