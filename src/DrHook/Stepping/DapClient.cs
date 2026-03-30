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
    private Stream? _stream;
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
        _stream = _debugger.StandardOutput.BaseStream;
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

    public async Task LaunchTargetAsync(string program, string[] args, string? cwd, bool stopAtEntry, Dictionary<string, string>? env, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["program"] = program,
            ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["cwd"] = cwd ?? Environment.CurrentDirectory,
            ["stopAtEntry"] = stopAtEntry,
        };

        if (env is { Count: > 0 })
        {
            var envObj = new JsonObject();
            foreach (var (key, value) in env)
                envObj[key] = value;
            request["env"] = envObj;
        }

        await SendRequestAsync("launch", request, ct);
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

    public async Task<JsonObject> SetBreakpointsAsync(string sourceFile, IReadOnlyList<(int Line, string? Condition)> breakpoints, CancellationToken ct)
    {
        var bpArray = new JsonArray();
        foreach (var (line, condition) in breakpoints)
        {
            var obj = new JsonObject { ["line"] = line };
            if (condition is not null)
                obj["condition"] = condition;
            bpArray.Add(obj);
        }

        return await SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["path"] = sourceFile
            },
            ["breakpoints"] = bpArray
        }, ct);
    }

    public async Task<JsonObject> SetFunctionBreakpointsAsync(IReadOnlyList<(string Name, string? Condition)> breakpoints, CancellationToken ct)
    {
        var bpArray = new JsonArray();
        foreach (var (name, condition) in breakpoints)
        {
            var obj = new JsonObject { ["name"] = name };
            if (condition is not null)
                obj["condition"] = condition;
            bpArray.Add(obj);
        }

        return await SendRequestAsync("setFunctionBreakpoints", new JsonObject
        {
            ["breakpoints"] = bpArray
        }, ct);
    }

    public async Task<JsonObject> SetExceptionBreakpointsAsync(string[] filters, CancellationToken ct)
    {
        return await SendRequestAsync("setExceptionBreakpoints", new JsonObject
        {
            ["filters"] = new JsonArray(filters.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray())
        }, ct);
    }

    public async Task<JsonObject> EvaluateAsync(int frameId, string expression, string context, CancellationToken ct)
    {
        return await SendRequestAsync("evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["frameId"] = frameId,
            ["context"] = context
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

    public async Task DisconnectAsync(bool terminateDebuggee, CancellationToken ct)
    {
        if (IsConnected)
        {
            try
            {
                await SendRequestAsync("disconnect", new JsonObject
                {
                    ["terminateDebuggee"] = terminateDebuggee
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
        await DisconnectAsync(terminateDebuggee: true, CancellationToken.None);

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
        if (_stdin is null || _stream is null)
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
        if (_stream is null) return null;

        // Read headers byte-by-byte until \r\n\r\n.
        // DAP headers are ASCII — reading bytes is safe.
        // We must not use StreamReader because it buffers ahead and
        // would consume body bytes that Content-Length counts as raw bytes.
        var contentLength = -1;
        var headerBytes = new List<byte>(128);

        while (true)
        {
            var b = _stream.ReadByte();
            if (b < 0) return null;

            headerBytes.Add((byte)b);

            // Check for \r\n\r\n (end of headers)
            if (headerBytes.Count >= 4 &&
                headerBytes[^4] == '\r' && headerBytes[^3] == '\n' &&
                headerBytes[^2] == '\r' && headerBytes[^1] == '\n')
            {
                var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                }
                break;
            }
        }

        if (contentLength <= 0) return null;

        // Read body as raw bytes — Content-Length is byte count, not char count.
        // Critical for non-ASCII content (Swedish chars, Unicode paths).
        var buffer = new byte[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), ct);
            if (read == 0) return null;
            totalRead += read;
        }

        var body = Encoding.UTF8.GetString(buffer, 0, totalRead);
        return JsonNode.Parse(body) as JsonObject;
    }
}
