using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

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
    private Process? _testProcess;
    private int _activeThreadId;
    private string? _sessionHypothesis;
    private string? _targetVersion;
    private int _stepCount;
    private bool _ownsProcess;

    // Process metrics — OS-level via syscall, managed-level via eval (with timeout)
    private int _targetPid;
    private ProcessMetrics? _previousMetrics;

    private record ProcessMetrics(
        double WorkingSetMB,
        double PrivateBytesMB,
        int ThreadCount,
        long? ManagedHeapBytes,
        int? GcGen0,
        int? GcGen1,
        int? GcGen2);

    // Breakpoint registry — tracks all active breakpoints so DAP's
    // set-and-replace semantics don't silently discard previous ones.
    private readonly Dictionary<string, Dictionary<int, string?>> _sourceBreakpoints = new();
    private readonly Dictionary<string, string?> _functionBreakpoints = new();
    private readonly HashSet<string> _exceptionFilters = new();

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
        _targetPid = pid;

        try
        {
            await _client.LaunchAsync(netcoredbgPath, ct);
            await _client.AttachAsync(pid, ct);
            _sourceBreakpoints[sourceFile] = new Dictionary<int, string?> { [line] = null };
            await SyncSourceBreakpointsAsync(sourceFile, ct);
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
            var (state, _) = await GetCurrentStateAsync(ct);

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
        Dictionary<string, string>? env,
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
            await _client.LaunchTargetAsync(program, args, cwd, stopAtEntry: true, env, ct);
            _sourceBreakpoints[sourceFile] = new Dictionary<int, string?> { [line] = null };
            await SyncSourceBreakpointsAsync(sourceFile, ct);
            await _client.ConfigurationDoneAsync(ct);

            // With stopAtEntry, netcoredbg sends a "stopped" event after configurationDone.
            // We must wait for it before threads are available.
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            // Now continue from entry to the actual breakpoint.
            await _client.ContinueAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            var (state, _) = await GetCurrentStateAsync(ct);

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

    public async Task<string> RunTestAsync(
        string project, string? filter, string? cwd,
        string sourceFile, int line, string hypothesis,
        CancellationToken ct)
    {
        if (IsActive)
            return Error("A stepping session is already active. Use drhook:step-stop first.");

        // Phase 1: Launch dotnet test with VSTEST_HOST_DEBUG=1 as a regular process.
        // testhost will pause and print its PID to stdout.
        var args = new List<string> { "test", "--no-build" };
        if (filter is not null)
        {
            args.Add("--filter");
            args.Add(filter);
        }
        args.Add(project);

        var testProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            }
        };
        foreach (var arg in args)
            testProcess.StartInfo.ArgumentList.Add(arg);
        testProcess.StartInfo.Environment["VSTEST_HOST_DEBUG"] = "1";

        testProcess.Start();
        _testProcess = testProcess;

        // Phase 2: Read stdout until we find the testhost PID.
        // Pattern: "Process Id: 12345, Name: dotnet"
        int testhostPid = -1;
        var pidPattern = new System.Text.RegularExpressions.Regex(@"Process Id:\s*(\d+)");

        while (!ct.IsCancellationRequested)
        {
            var line2 = await testProcess.StandardOutput.ReadLineAsync(ct);
            if (line2 is null) break;

            var match = pidPattern.Match(line2);
            if (match.Success)
            {
                testhostPid = int.Parse(match.Groups[1].Value);
                break;
            }
        }

        if (testhostPid < 0)
        {
            testProcess.Kill();
            _testProcess = null;
            return Error("dotnet test exited without producing a testhost PID. Is VSTEST_HOST_DEBUG supported?");
        }

        // Phase 3: Attach netcoredbg to the testhost process (same as LaunchAsync).
        _targetVersion = "testhost";
        var netcoredbgPath = NetCoreDbgLocator.LocateOrThrow();

        _client = new DapClient();
        _sessionHypothesis = hypothesis;
        _stepCount = 0;
        _ownsProcess = false; // netcoredbg attached, not launched — testProcess owns the lifecycle
        _targetPid = testhostPid;

        try
        {
            await _client.LaunchAsync(netcoredbgPath, ct);
            await _client.AttachAsync(testhostPid, ct);
            _sourceBreakpoints[sourceFile] = new Dictionary<int, string?> { [line] = null };
            await SyncSourceBreakpointsAsync(sourceFile, ct);
            await _client.ConfigurationDoneAsync(ct);

            var threads = await _client.GetThreadsAsync(ct);
            var threadArray = threads["threads"] as JsonArray;
            _activeThreadId = threadArray?[0]?["id"]?.GetValue<int>() ?? 1;

            // Continue — testhost resumes from its debug pause, runs to our breakpoint.
            await _client.ContinueAsync(_activeThreadId, ct);
            UpdateActiveThread(await _client.WaitForStoppedAsync(ct));

            var (state, _) = await GetCurrentStateAsync(ct);

            return JsonSerializer.Serialize(new JsonObject
            {
                ["status"] = "attached-to-testhost",
                ["testhostPid"] = testhostPid,
                ["project"] = project,
                ["filter"] = filter,
                ["breakpoint"] = new JsonObject { ["file"] = sourceFile, ["line"] = line },
                ["hypothesis"] = hypothesis,
                ["currentState"] = state,
                ["instruction"] = "Attached to testhost and stopped at breakpoint. Use drhook:step-next, drhook:step-into, drhook:step-vars to inspect."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            return Error($"Failed to attach to testhost: {ex.Message}");
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

            var (state, topFrameId) = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "next",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            if (hypothesis is not null)
                result["hypothesis"] = hypothesis;

            result["metrics"] = await CaptureMetricsAsync(ct);

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

            var (state, topFrameId) = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "stepIn",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            if (hypothesis is not null)
                result["hypothesis"] = hypothesis;

            result["metrics"] = await CaptureMetricsAsync(ct);

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

            var (state, topFrameId) = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "stepOut",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            if (hypothesis is not null)
                result["hypothesis"] = hypothesis;

            result["metrics"] = await CaptureMetricsAsync(ct);

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

                var (state, topFrameId) = await GetCurrentStateAsync(ct);

                var result = new JsonObject
                {
                    ["operation"] = "continue",
                    ["step"] = _stepCount,
                    ["assemblyVersion"] = _targetVersion,
                    ["currentState"] = state,
                };

                if (hypothesis is not null)
                    result["hypothesis"] = hypothesis;

                result["metrics"] = await CaptureMetricsAsync(ct);

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

            var (state, topFrameId) = await GetCurrentStateAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "pause",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["currentState"] = state,
            };

            result["metrics"] = await CaptureMetricsAsync(ct);

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
            if (!_sourceBreakpoints.TryGetValue(sourceFile, out var fileBps))
            {
                fileBps = new Dictionary<int, string?>();
                _sourceBreakpoints[sourceFile] = fileBps;
            }
            fileBps[line] = condition;

            var response = await SyncSourceBreakpointsAsync(sourceFile, ct);
            var verified = GetVerifiedStatus(response, line);

            var result = new JsonObject
            {
                ["operation"] = "setBreakpoint",
                ["sourceFile"] = sourceFile,
                ["line"] = line,
                ["verified"] = verified,
                ["assemblyVersion"] = _targetVersion,
                ["totalInFile"] = fileBps.Count,
            };

            if (condition is not null)
                result["condition"] = condition;

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
            _functionBreakpoints[functionName] = condition;

            var response = await SyncFunctionBreakpointsAsync(ct);
            var breakpoints = response["breakpoints"] as JsonArray;
            var verified = breakpoints?.LastOrDefault()?["verified"]?.GetValue<bool>() ?? false;

            var result = new JsonObject
            {
                ["operation"] = "setFunctionBreakpoint",
                ["functionName"] = functionName,
                ["verified"] = verified,
                ["assemblyVersion"] = _targetVersion,
                ["totalFunctionBreakpoints"] = _functionBreakpoints.Count,
            };

            if (condition is not null)
                result["condition"] = condition;

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
            _exceptionFilters.Add(filter);
            await SyncExceptionBreakpointsAsync(ct);

            var result = new JsonObject
            {
                ["operation"] = "setExceptionBreakpoint",
                ["filter"] = filter,
                ["assemblyVersion"] = _targetVersion,
                ["activeFilters"] = new JsonArray(_exceptionFilters.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Set exception breakpoint failed: {ex.Message}");
        }
    }

    public async Task<string> RemoveBreakpointAsync(string sourceFile, int line, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            if (!_sourceBreakpoints.TryGetValue(sourceFile, out var fileBps) || !fileBps.Remove(line))
                return Error($"No breakpoint at {sourceFile}:{line}");

            if (fileBps.Count == 0)
                _sourceBreakpoints.Remove(sourceFile);

            await SyncSourceBreakpointsAsync(sourceFile, ct);

            return JsonSerializer.Serialize(new JsonObject
            {
                ["operation"] = "removeBreakpoint",
                ["sourceFile"] = sourceFile,
                ["line"] = line,
                ["assemblyVersion"] = _targetVersion,
                ["remainingInFile"] = fileBps.Count,
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Remove breakpoint failed: {ex.Message}");
        }
    }

    public async Task<string> RemoveFunctionBreakpointAsync(string functionName, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            if (!_functionBreakpoints.Remove(functionName))
                return Error($"No function breakpoint for '{functionName}'");

            await SyncFunctionBreakpointsAsync(ct);

            return JsonSerializer.Serialize(new JsonObject
            {
                ["operation"] = "removeFunctionBreakpoint",
                ["functionName"] = functionName,
                ["assemblyVersion"] = _targetVersion,
                ["remainingFunctionBreakpoints"] = _functionBreakpoints.Count,
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Remove function breakpoint failed: {ex.Message}");
        }
    }

    public async Task<string> RemoveExceptionBreakpointAsync(string filter, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            if (!_exceptionFilters.Remove(filter))
                return Error($"No exception filter '{filter}'");

            await SyncExceptionBreakpointsAsync(ct);

            return JsonSerializer.Serialize(new JsonObject
            {
                ["operation"] = "removeExceptionBreakpoint",
                ["filter"] = filter,
                ["assemblyVersion"] = _targetVersion,
                ["activeFilters"] = new JsonArray(_exceptionFilters.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Remove exception breakpoint failed: {ex.Message}");
        }
    }

    public string ListBreakpoints()
    {
        var sourceArray = new JsonArray();
        foreach (var (file, bps) in _sourceBreakpoints)
        {
            foreach (var (line, condition) in bps)
            {
                var bp = new JsonObject { ["file"] = file, ["line"] = line };
                if (condition is not null) bp["condition"] = condition;
                sourceArray.Add(bp);
            }
        }

        var functionArray = new JsonArray();
        foreach (var (name, condition) in _functionBreakpoints)
        {
            var bp = new JsonObject { ["name"] = name };
            if (condition is not null) bp["condition"] = condition;
            functionArray.Add(bp);
        }

        return JsonSerializer.Serialize(new JsonObject
        {
            ["source"] = sourceArray,
            ["function"] = functionArray,
            ["exception"] = new JsonArray(_exceptionFilters.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["totalCount"] = sourceArray.Count + functionArray.Count + _exceptionFilters.Count,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> ClearBreakpointsAsync(string? category, CancellationToken ct)
    {
        if (!IsActive || _client is null)
            return Error("No active stepping session. Use drhook:step-launch first.");

        try
        {
            var cleared = new JsonObject { ["operation"] = "clearBreakpoints" };

            if (category is null or "source")
            {
                var files = _sourceBreakpoints.Keys.ToList();
                _sourceBreakpoints.Clear();
                foreach (var file in files)
                    await SyncSourceBreakpointsAsync(file, ct);
                cleared["sourceCleared"] = true;
            }

            if (category is null or "function")
            {
                _functionBreakpoints.Clear();
                await SyncFunctionBreakpointsAsync(ct);
                cleared["functionCleared"] = true;
            }

            if (category is null or "exception")
            {
                _exceptionFilters.Clear();
                await SyncExceptionBreakpointsAsync(ct);
                cleared["exceptionCleared"] = true;
            }

            cleared["assemblyVersion"] = _targetVersion;
            return JsonSerializer.Serialize(cleared, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Error($"Clear breakpoints failed: {ex.Message}");
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

    private async Task<(JsonObject State, int FrameId)> GetCurrentStateAsync(CancellationToken ct)
    {
        if (_client is null) return (new JsonObject { ["error"] = "No client" }, 0);

        var stackTrace = await _client.GetStackTraceAsync(_activeThreadId, ct);
        var frames = stackTrace["stackFrames"] as JsonArray;

        if (frames is null || frames.Count == 0)
            return (new JsonObject { ["location"] = "unknown" }, 0);

        var topFrame = frames[0] as JsonObject;
        var topFrameId = topFrame?["id"]?.GetValue<int>() ?? 0;
        var source = topFrame?["source"] as JsonObject;

        var state = new JsonObject
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

        return (state, topFrameId);
    }

    // ─── Process metrics (OS syscalls + EventPipe counters, no DAP/eval) ──

    private async Task<JsonObject> CaptureMetricsAsync(CancellationToken ct)
    {
        var result = new JsonObject();
        if (_targetPid <= 0) return result;

        try
        {
            // OS layer — always available, even when paused
            var proc = Process.GetProcessById(_targetPid);
            var workingSetMB = proc.WorkingSet64 / (1024.0 * 1024.0);
            var privateBytesMB = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
            var threadCount = proc.Threads.Count;

            result["workingSetMB"] = Math.Round(workingSetMB, 1);
            result["privateBytesMB"] = Math.Round(privateBytesMB, 1);
            result["threadCount"] = threadCount;

            // Managed layer — EventPipe System.Runtime counters (no eval, no DAP)
            long? managedHeap = null;
            int? gen0 = null, gen1 = null, gen2 = null;

            try
            {
                var counters = await CaptureRuntimeCountersAsync(_targetPid, ct);
                managedHeap = counters.GcHeapBytes;
                gen0 = counters.GcGen0;
                gen1 = counters.GcGen1;
                gen2 = counters.GcGen2;
            }
            catch
            {
                // EventPipe may not be available (process exiting, permissions) — degrade gracefully
            }

            if (managedHeap is not null)
                result["managedHeapMB"] = Math.Round(managedHeap.Value / (1024.0 * 1024.0), 1);
            if (gen0 is not null) result["gcGen0"] = gen0.Value;
            if (gen1 is not null) result["gcGen1"] = gen1.Value;
            if (gen2 is not null) result["gcGen2"] = gen2.Value;

            var current = new ProcessMetrics(workingSetMB, privateBytesMB, threadCount,
                managedHeap, gen0, gen1, gen2);

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
        }
        catch (ArgumentException)
        {
            // Process exited
            result["error"] = "Target process has exited";
        }

        return result;
    }

    private record RuntimeCounters(long? GcHeapBytes, int? GcGen0, int? GcGen1, int? GcGen2);

    private static async Task<RuntimeCounters> CaptureRuntimeCountersAsync(int pid, CancellationToken ct)
    {
        long? heapBytes = null;
        int? gen0 = null, gen1 = null, gen2 = null;

        var client = new DiagnosticsClient(pid);
        var providers = new[]
        {
            new EventPipeProvider(
                "System.Runtime",
                System.Diagnostics.Tracing.EventLevel.Informational,
                (long)ClrTraceEventParser.Keywords.None,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = "0" })
        };

        using var session = client.StartEventPipeSession(providers, requestRundown: false);
        using var source = new EventPipeEventSource(session.EventStream);

        source.Dynamic.All += e =>
        {
            if (e.EventName != "EventCounters") return;
            var payload = e.PayloadByName("Payload") as IDictionary<string, object>;
            if (payload is null) return;

            var name = payload.TryGetValue("Name", out var n) ? n as string : null;
            var value = payload.TryGetValue("Mean", out var m) ? m
                      : payload.TryGetValue("Increment", out var i) ? i : null;

            switch (name)
            {
                case "gc-heap-size":
                    if (value is double d) heapBytes = (long)(d * 1024 * 1024); // reported in MB
                    break;
                case "gen-0-gc-count":
                    if (value is double g0) gen0 = (int)g0;
                    break;
                case "gen-1-gc-count":
                    if (value is double g1) gen1 = (int)g1;
                    break;
                case "gen-2-gc-count":
                    if (value is double g2) gen2 = (int)g2;
                    break;
            }
        };

        // Process events briefly — counters are emitted on session start
        using var timeout = new CancellationTokenSource(1000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await Task.Run(() => source.Process(), linked.Token); }
        catch (OperationCanceledException) { }

        await session.StopAsync(ct);
        return new RuntimeCounters(heapBytes, gen0, gen1, gen2);
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

    // ─── Breakpoint registry sync ─────────────────────────────────────────

    private async Task<JsonObject> SyncSourceBreakpointsAsync(string sourceFile, CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("No DAP client");

        if (_sourceBreakpoints.TryGetValue(sourceFile, out var fileBps) && fileBps.Count > 0)
            return await _client.SetBreakpointsAsync(sourceFile, fileBps.Select(kv => (kv.Key, kv.Value)).ToList(), ct);

        // No breakpoints left in this file — send empty to clear DAP
        return await _client.SetBreakpointsAsync(sourceFile, Array.Empty<(int, string?)>(), ct);
    }

    private async Task<JsonObject> SyncFunctionBreakpointsAsync(CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("No DAP client");
        return await _client.SetFunctionBreakpointsAsync(_functionBreakpoints.Select(kv => (kv.Key, kv.Value)).ToList(), ct);
    }

    private async Task SyncExceptionBreakpointsAsync(CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("No DAP client");
        await _client.SetExceptionBreakpointsAsync(_exceptionFilters.ToArray(), ct);
    }

    private static bool GetVerifiedStatus(JsonObject response, int line)
    {
        if (response["breakpoints"] is not JsonArray breakpoints) return false;

        foreach (var bp in breakpoints)
        {
            if (bp?["line"]?.GetValue<int>() == line)
                return bp?["verified"]?.GetValue<bool>() ?? false;
        }

        return breakpoints.LastOrDefault()?["verified"]?.GetValue<bool>() ?? false;
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
        if (_testProcess is not null)
        {
            try { _testProcess.Kill(); } catch { }
            _testProcess.Dispose();
            _testProcess = null;
        }
        _activeThreadId = 0;
        _sessionHypothesis = null;
        _targetVersion = null;
        _stepCount = 0;
        _ownsProcess = false;
        _targetPid = 0;
        _previousMetrics = null;
        _sourceBreakpoints.Clear();
        _functionBreakpoints.Clear();
        _exceptionFilters.Clear();
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new JsonObject { ["error"] = message });
}
