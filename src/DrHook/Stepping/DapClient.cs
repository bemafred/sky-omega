using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyOmega.DrHook.Stepping;

/// <summary>
/// Minimal Debug Adapter Protocol (DAP) client over stdio.
/// BCL only — DAP is a JSON protocol with Content-Length framing.
///
/// This is Layer 2 in the sovereignty stack — DrHook OWNS this.
/// It speaks to whatever debug engine is Layer 3 (netcoredbg today,
/// potentially DrHook.Engine via dbgshim in the future).
///
/// The DAP spec: https://microsoft.github.io/debug-adapter-protocol/
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private Process? _debugger;
    private StreamReader? _stdout;
    private StreamWriter? _stdin;
    private int _seq = 1;
    private readonly System.Collections.Concurrent.ConcurrentQueue<JsonObject> _stoppedEvents = new();

    public bool IsConnected => _debugger is not null && !_debugger.HasExited;

    public async Task LaunchAsync(string netcoredbgPath, CancellationToken ct)
    {
        _debugger = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = netcoredbgPath,
                Arguments = "--interpreter=vscode",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        _debugger.Start();
        _stdout = _debugger.StandardOutput;
        _stdin = _debugger.StandardInput;

        // Initialize DAP session
        var initResponse = await SendRequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "drhook",
            ["clientName"] = "DrHook",
            ["adapterID"] = "netcoredbg",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["supportsVariableType"] = true,
        }, ct);
    }

    public async Task AttachAsync(int pid, CancellationToken ct)
    {
        await SendRequestAsync("attach", new JsonObject
        {
            ["processId"] = pid,
        }, ct);
    }

    public async Task ConfigurationDoneAsync(CancellationToken ct)
    {
        await SendRequestAsync("configurationDone", new JsonObject(), ct);
    }

    public async Task<JsonObject> SetBreakpointAsync(string sourceFile, int line, CancellationToken ct)
    {
        return await SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["path"] = sourceFile
            },
            ["breakpoints"] = new JsonArray(new JsonObject
            {
                ["line"] = line
            })
        }, ct);
    }

    public async Task<JsonObject> ContinueAsync(int threadId, CancellationToken ct)
    {
        return await SendRequestAsync("continue", new JsonObject
        {
            ["threadId"] = threadId
        }, ct);
    }

    public async Task<JsonObject> StepNextAsync(int threadId, CancellationToken ct)
    {
        return await SendRequestAsync("next", new JsonObject
        {
            ["threadId"] = threadId
        }, ct);
    }

    public async Task<JsonObject> StepInAsync(int threadId, CancellationToken ct)
    {
        return await SendRequestAsync("stepIn", new JsonObject
        {
            ["threadId"] = threadId
        }, ct);
    }

    public async Task<JsonObject> StepOutAsync(int threadId, CancellationToken ct)
    {
        return await SendRequestAsync("stepOut", new JsonObject
        {
            ["threadId"] = threadId
        }, ct);
    }

    public async Task<JsonObject> PauseAsync(int threadId, CancellationToken ct)
    {
        return await SendRequestAsync("pause", new JsonObject
        {
            ["threadId"] = threadId
        }, ct);
    }

    public async Task<JsonObject> SetBreakpointAsync(string sourceFile, int line, string? condition, CancellationToken ct)
    {
        var breakpointObj = new JsonObject { ["line"] = line };
        if (condition is not null)
            breakpointObj["condition"] = condition;

        return await SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["path"] = sourceFile
            },
            ["breakpoints"] = new JsonArray(breakpointObj)
        }, ct);
    }

    public async Task<JsonObject> SetFunctionBreakpointsAsync(string functionName, string? condition, CancellationToken ct)
    {
        var breakpointObj = new JsonObject { ["name"] = functionName };
        if (condition is not null)
            breakpointObj["condition"] = condition;

        return await SendRequestAsync("setFunctionBreakpoints", new JsonObject
        {
            ["breakpoints"] = new JsonArray(breakpointObj)
        }, ct);
    }

    public async Task<JsonObject> SetExceptionBreakpointsAsync(string[] filters, CancellationToken ct)
    {
        return await SendRequestAsync("setExceptionBreakpoints", new JsonObject
        {
            ["filters"] = new JsonArray(filters.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray())
        }, ct);
    }

    public async Task<JsonObject> GetThreadsAsync(CancellationToken ct)
    {
        return await SendRequestAsync("threads", new JsonObject(), ct);
    }

    public async Task<JsonObject> GetStackTraceAsync(int threadId, CancellationToken ct)
    {
        return await SendRequestAsync("stackTrace", new JsonObject
        {
            ["threadId"] = threadId,
            ["startFrame"] = 0,
            ["levels"] = 20
        }, ct);
    }

    public async Task<JsonObject> GetScopesAsync(int frameId, CancellationToken ct)
    {
        return await SendRequestAsync("scopes", new JsonObject
        {
            ["frameId"] = frameId
        }, ct);
    }

    public async Task<JsonObject> GetVariablesAsync(int variablesReference, CancellationToken ct)
    {
        return await SendRequestAsync("variables", new JsonObject
        {
            ["variablesReference"] = variablesReference
        }, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (IsConnected)
        {
            try
            {
                await SendRequestAsync("disconnect", new JsonObject
                {
                    ["terminateDebuggee"] = false
                }, ct);
            }
            catch
            {
                // Best-effort disconnect
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);

        if (_debugger is not null && !_debugger.HasExited)
        {
            _debugger.Kill();
            await _debugger.WaitForExitAsync();
        }

        _debugger?.Dispose();
    }

    /// <summary>
    /// Reads DAP messages until a "stopped" event arrives.
    /// The cancellation token is the sole timeout mechanism — the caller decides how long to wait.
    /// </summary>
    public async Task<JsonObject> WaitForStoppedAsync(CancellationToken ct)
    {
        if (_stoppedEvents.TryDequeue(out var buffered))
            return buffered;

        while (!ct.IsCancellationRequested)
        {
            var message = await ReadDapMessageAsync(ct);
            if (message is null) throw new InvalidOperationException("DAP connection closed while waiting for stopped event");

            var type = message["type"]?.GetValue<string>();
            if (type == "event" && message["event"]?.GetValue<string>() == "stopped")
                return message["body"] as JsonObject ?? new JsonObject();
        }

        throw new OperationCanceledException();
    }

    // ─── DAP wire protocol ──────────────────────────────────────────────

    private async Task<JsonObject> SendRequestAsync(string command, JsonObject arguments, CancellationToken ct)
    {
        if (_stdin is null || _stdout is null)
            throw new InvalidOperationException("DAP client not connected");

        var seq = _seq++;
        var request = new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
            ["arguments"] = arguments
        };

        var json = JsonSerializer.Serialize(request);
        var bytes = Encoding.UTF8.GetBytes(json);

        // DAP uses Content-Length framing
        await _stdin.WriteAsync($"Content-Length: {bytes.Length}\r\n\r\n");
        await _stdin.WriteAsync(json);
        await _stdin.FlushAsync(ct);

        // Read response — skip events, wait for the matching response
        while (!ct.IsCancellationRequested)
        {
            var message = await ReadDapMessageAsync(ct);
            if (message is null) throw new InvalidOperationException("DAP connection closed");

            var type = message["type"]?.GetValue<string>();

            if (type == "response" && message["command"]?.GetValue<string>() == command)
            {
                var success = message["success"]?.GetValue<bool>() ?? false;
                if (!success)
                {
                    var errorMessage = message["message"]?.GetValue<string>() ?? "Unknown DAP error";
                    throw new InvalidOperationException($"DAP error ({command}): {errorMessage}");
                }
                return message["body"] as JsonObject ?? new JsonObject();
            }

            // Buffer stopped events so WaitForStoppedAsync can consume them
            if (type == "event" && message["event"]?.GetValue<string>() == "stopped")
            {
                _stoppedEvents.Enqueue(message["body"] as JsonObject ?? new JsonObject());
                continue;
            }

            // Other events (output, etc.) — skip
        }

        throw new OperationCanceledException();
    }

    private async Task<JsonObject?> ReadDapMessageAsync(CancellationToken ct)
    {
        if (_stdout is null) return null;

        // Read headers until empty line
        var contentLength = -1;
        while (true)
        {
            var headerLine = await _stdout.ReadLineAsync(ct);
            if (headerLine is null) return null;
            if (string.IsNullOrWhiteSpace(headerLine)) break;

            if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = headerLine["Content-Length:".Length..].Trim();
                contentLength = int.Parse(value);
            }
        }

        if (contentLength <= 0) return null;

        // Read body
        var buffer = new char[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await _stdout.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), ct);
            if (read == 0) return null;
            totalRead += read;
        }

        var body = new string(buffer, 0, totalRead);
        return JsonNode.Parse(body) as JsonObject;
    }
}
