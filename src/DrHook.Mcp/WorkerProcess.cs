using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Worker;

namespace SkyOmega.DrHook.Mcp;

/// <summary>A session tool's answer, as the worker gave it: the tool's JSON result, or a caption and an image; and what
/// the session is afterwards (<see cref="WorkerProtocol.Active"/>, <see cref="WorkerProtocol.Holding"/> or
/// <see cref="WorkerProtocol.Ended"/>).</summary>
public sealed record WorkerAnswer(string? Result, string? Caption, byte[]? Png, string Session);

/// <summary>One drhook-worker process, as drhook-mcp sees it (ADR-016 6.7): started on the runtime drhook-mcp runs on,
/// spoken to over its stdin and stdout (<see cref="WorkerProtocol"/>). A thread reads everything it sends: answers
/// complete their requests, and records go to drhook-mcp's buffers as they come. The end of its stdout means it is
/// gone. Its stderr is drhook-mcp's.</summary>
internal sealed class WorkerProcess : IDisposable
{
    /// <summary>How long a worker has to say it is ready: 6.8's bound for attach and launch. Finding 113 measured at
    /// most 75 ms.</summary>
    public static readonly TimeSpan ReadyBound = TimeSpan.FromSeconds(30);

    /// <summary>How long a worker has to end after it was asked to. Finding 113 measured 5 to 19 ms.</summary>
    public static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly IDebugEventSink _records;
    private readonly ConcurrentDictionary<long, (string Tool, TaskCompletionSource<WorkerAnswer> Answer)> _pending = new();
    private readonly TaskCompletionSource<JsonNode?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stdinLock = new();
    private long _lastId;
    private volatile bool _gone;
    private volatile int _targetPid;
    private volatile bool _targetEnded;

    private WorkerProcess(Process process, IDebugEventSink records)
    {
        _process = process;
        _records = records;
    }

    /// <summary>The worker's own PID.</summary>
    public int Pid => _process.Id;

    /// <summary>The PID of its session's target, from its target event; 0 before one.</summary>
    public int TargetPid => _targetPid;

    /// <summary>True once a holding worker has said that the target it held has ended.</summary>
    public bool TargetEnded => _targetEnded;

    /// <summary>True once the worker's stdout has ended: the worker is gone.</summary>
    public bool IsGone => _gone;

    // The dotnet host of the runtime drhook-mcp runs on (<root>/shared/Microsoft.NETCore.App/<version>/ is the runtime
    // directory), so that the worker runs on the same runtime, whatever is on the PATH.
    private static string Host => Path.Combine(
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    /// <summary>Start a worker from <paramref name="workerDirectory"/> and wait for it to say it is ready. Returns null
    /// and says why in <paramref name="failure"/> when it does not start, is not ready within <see cref="ReadyBound"/>,
    /// or speaks another protocol version.</summary>
    public static WorkerProcess? Start(string workerDirectory, IDebugEventSink records, out string? failure)
    {
        string program = Path.Combine(workerDirectory, WorkerProtocol.WorkerAssembly);
        var start = new ProcessStartInfo(Host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // the worker's stderr is drhook-mcp's
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory, // a launch's relative paths mean what they meant in drhook-mcp
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(program);

        Process process;
        try { process = Process.Start(start)!; }
        catch (Exception ex)
        {
            failure = $"The debugger worker could not be started ({Host} exec {program}): {ex.GetType().Name}: {ex.Message}";
            return null;
        }
        var worker = new WorkerProcess(process, records);
        new Thread(worker.ReadWorker) { IsBackground = true, Name = $"drhook-mcp worker {process.Id}" }.Start();

        if (!worker._ready.Task.Wait(ReadyBound))
            failure = $"The debugger worker (pid {process.Id}) did not say it was ready within {ReadyBound.TotalSeconds:0} s.";
        else if (worker._ready.Task.Result is not { } ready)
            failure = $"The debugger worker (pid {process.Id}) ended before it was ready; drhook-mcp's stderr has what it wrote.";
        else if ((int?)ready["protocol"] is not WorkerProtocol.Version)
            failure = $"The debugger worker speaks protocol {(int?)ready["protocol"]}, and this drhook-mcp speaks {WorkerProtocol.Version}: " +
                      "the build changed under a running drhook-mcp. Restart drhook-mcp (reconnect the DrHook MCP server).";
        else
        {
            failure = null;
            return worker;
        }
        worker.Kill();
        worker.Dispose();
        return null;
    }

    /// <summary>Run <paramref name="tool"/> in the worker's session. A worker that is gone, or goes before it answers,
    /// answers with an error, and its session is over.</summary>
    public Task<WorkerAnswer> CallAsync(string tool, JsonObject args)
    {
        long id = Interlocked.Increment(ref _lastId);
        var answer = new TaskCompletionSource<WorkerAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (tool, answer);
        if (_gone || !Send(WorkerProtocol.Tool(id, tool, args)))
        {
            // Gone before the request was read: the reader has already answered what was pending, or answers it now.
            if (_pending.TryRemove(id, out _)) return Task.FromResult(GoneBefore(tool));
        }
        return answer.Task;
    }

    /// <summary>Ask the worker to exit, and wait for it to, up to <see cref="ExitBound"/>. A worker that is late is
    /// killed (only the worker: a target it launched is not its process tree's to lose here).</summary>
    public void End()
    {
        Send(WorkerProtocol.Exit());
        if (!_process.WaitForExit(ExitBound))
        {
            Console.Error.WriteLine($"drhook-mcp: the debugger worker (pid {Pid}) did not end within {ExitBound.TotalSeconds:0} s of its exit request; killing it");
            Kill();
        }
        Dispose();
    }

    /// <summary>Close the worker's stdin, as drhook-mcp's own end does: a session still open is ended as drhook_stop
    /// ends it, and a target left running is left running. Then wait for the worker, up to <paramref name="bound"/>.</summary>
    public bool CloseAndWait(TimeSpan bound)
    {
        lock (_stdinLock)
        {
            try { _process.StandardInput.Close(); }
            catch (IOException) { }
        }
        return _process.WaitForExit(bound);
    }

    private void Kill()
    {
        try { _process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { /* already ended */ }
    }

    private bool Send(string line)
    {
        lock (_stdinLock)
        {
            try
            {
                _process.StandardInput.Write(line);
                _process.StandardInput.Write('\n');
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private void ReadWorker()
    {
        try
        {
            string? line;
            while ((line = _process.StandardOutput.ReadLine()) is not null)
            {
                JsonNode? message;
                try { message = JsonNode.Parse(line); }
                catch (System.Text.Json.JsonException) { Console.Error.WriteLine($"drhook-mcp: the debugger worker (pid {Pid}) wrote what is not a message: {line}"); continue; }
                if (message is null) continue;
                if (message["event"] is JsonNode kind)
                {
                    switch ((string?)kind)
                    {
                        case "ready": _ready.TrySetResult(message); break;
                        case "target": _targetPid = (int)message["pid"]!; break;
                        case "anomaly": _records.OnAnomaly(WorkerProtocol.ReadAnomaly(message)); break;
                        case "log": _records.OnLog(WorkerProtocol.ReadLog(message)); break;
                        case "console": _records.OnConsoleOutput(WorkerProtocol.ReadConsole(message)); break;
                        case "ended": _targetEnded = true; break;
                    }
                }
                else if (message["id"] is JsonNode id && _pending.TryRemove((long)id, out var pending))
                {
                    pending.Answer.TrySetResult(new WorkerAnswer(
                        (string?)message["result"],
                        (string?)message["caption"],
                        message["image"] is JsonNode image ? Convert.FromBase64String((string)image!) : null,
                        (string?)message["session"] ?? WorkerProtocol.Ended));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe broke: the worker is gone, as at the end of its stdout.
        }
        _gone = true;
        _ready.TrySetResult(null);
        foreach (long id in _pending.Keys)
            if (_pending.TryRemove(id, out var pending)) pending.Answer.TrySetResult(GoneBefore(pending.Tool));
    }

    private WorkerAnswer GoneBefore(string tool) => new(
        OperatorJson.Error($"The debugger worker (pid {Pid}) ended before it answered {tool}, so the session is over" +
                           (_targetPid != 0 ? $"; its target was pid {_targetPid}" : "") +
                           ". drhook-mcp's stderr has what the worker wrote."),
        null, null, WorkerProtocol.Ended);

    public void Dispose() => _process.Dispose();
}
