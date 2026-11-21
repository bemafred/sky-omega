using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Worker;

namespace SkyOmega.DrHook.Mcp;

/// <summary>A session tool's answer, as the worker gave it: the tool's JSON result, or a caption and an image; what the
/// session is afterwards (<see cref="WorkerProtocol.Active"/>, <see cref="WorkerProtocol.Holding"/>,
/// <see cref="WorkerProtocol.Ended"/>, <see cref="WorkerProtocol.Lost"/>, or <see cref="WorkerProcess.Gone"/> when the
/// worker ended before it answered); and, when the debugger is lost, why.</summary>
public sealed record WorkerAnswer(string? Result, string? Caption, byte[]? Png, string Session, string? LostCause = null);

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

    /// <summary>The session's state in an answer the worker never gave: it ended before it answered.</summary>
    public const string Gone = "gone";

    /// <summary>The session's state in the answer drhook-mcp gives for a worker it discarded.</summary>
    public const string Discarded = "discarded";

    private readonly Process _process;
    private readonly IDebugEventSink _records;
    private readonly ConcurrentDictionary<long, (string Tool, TaskCompletionSource<WorkerAnswer> Answer)> _pending = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _health = new();
    private readonly TaskCompletionSource<JsonNode?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stdinLock = new();
    private long _lastId;
    private volatile bool _gone;
    private volatile int _targetPid;
    private volatile bool _targetEnded;
    private volatile TargetIdentity? _target;
    private volatile string? _lastState;

    // The target's identity, from the worker's target event: its PID with its start time on Unix (ProcessLifetime, the
    // same reading DebugSession makes), or a handle drhook-mcp opens at once on Windows, which keeps the PID from being
    // given to another process while it is held.
    private sealed record TargetIdentity(int Pid, long? Start, bool Owned, Process? Handle);

    private WorkerProcess(Process process, IDebugEventSink records)
    {
        _process = process;
        _records = records;
        Pid = process.Id;
    }

    /// <summary>The worker's own PID, kept from its start: it is still named after the worker has ended.</summary>
    public int Pid { get; }

    /// <summary>The PID of its session's target, from its target event; 0 before one.</summary>
    public int TargetPid => _targetPid;

    /// <summary>True once a holding worker has said that the target it held has ended.</summary>
    public bool TargetEnded => _targetEnded;

    /// <summary>True once the worker's stdout has ended: the worker is gone.</summary>
    public bool IsGone => _gone;

    /// <summary>The target's last state as the worker reported it ("stopped at …", "running", "ended"), and when
    /// drhook-mcp received it; null before the first.</summary>
    public string? LastState => _lastState;

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

    /// <summary>Ask the worker how it is (ADR-016 6.8). The answer, or null when none came within
    /// <paramref name="bound"/> or the worker is gone.</summary>
    public async Task<JsonNode?> HealthAsync(TimeSpan bound)
    {
        long id = Interlocked.Increment(ref _lastId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _health[id] = answer;
        if (_gone || !Send(WorkerProtocol.Health(id))) { _health.TryRemove(id, out _); return null; }
        Task done = await Task.WhenAny(answer.Task, Task.Delay(bound));
        _health.TryRemove(id, out _);
        return done == answer.Task ? answer.Task.Result : null;
    }

    /// <summary>Ask the worker to exit, and wait for it to, up to <see cref="ExitBound"/>. True when it ended in time;
    /// a worker that is late is killed (only the worker: a target it launched is not its process tree's to lose here),
    /// and false is returned.</summary>
    public bool End()
    {
        Send(WorkerProtocol.Exit());
        bool ended = _process.WaitForExit(ExitBound);
        if (!ended) Kill();
        Dispose();
        return ended;
    }

    /// <summary>Kill the worker, and wait for its end (the discard, ADR-016 6.7). Only the worker: its target is dealt
    /// with by <see cref="EndOwnedTarget"/>.</summary>
    public void KillAndWait()
    {
        Kill();
        _process.WaitForExit(ExitBound);
    }

    /// <summary>The target after its worker was discarded (ADR-016 6.7): an owned target still running under the same
    /// identity is ended through the operating system; a borrowed one is left as it is. Returns the target's PID (0 when
    /// the worker had none), whether it was owned, its state as the operating system shows it afterwards ("ended",
    /// "running" or "unknown"), and whether drhook-mcp ended it. Unix reads the end by PID and start time; between that
    /// read and the kill the PID could in principle be given to another process, which the held handle rules out on
    /// Windows only.</summary>
    public (int Pid, bool Owned, string State, bool EndedByDrHook) EndOwnedTarget()
    {
        if (_target is not { } target) return (0, false, "unknown", false);
        bool? ended = TargetHasEnded(target);
        if (ended != false || !target.Owned)
            return (target.Pid, target.Owned, ended switch { true => "ended", false => "running", _ => "unknown" }, false);
        try
        {
            if (target.Handle is { } handle) handle.Kill();
            else
            {
                using Process process = Process.GetProcessById(target.Pid);
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Ended meanwhile, or not ours to kill: read below.
        }
        var waited = Stopwatch.StartNew();
        while (TargetHasEnded(target) == false && waited.Elapsed < ExitBound) Thread.Sleep(20);
        ended = TargetHasEnded(target);
        return (target.Pid, true, ended switch { true => "ended", false => "running", _ => "unknown" }, ended == true);
    }

    private static bool? TargetHasEnded(TargetIdentity target)
    {
        if (target.Handle is { } handle)
        {
            try { return handle.HasExited; }
            catch (InvalidOperationException) { return null; }
        }
        return target.Start is long start ? SkyOmega.DrHook.Engine.Interop.ProcessLifetime.HasEnded(target.Pid, start) : null;
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
                        case "target":
                            _targetPid = (int)message["pid"]!;
                            _target = IdentityOf(_targetPid, (long?)message["start"], (bool)message["owned"]!);
                            break;
                        case "state":
                            _lastState = $"{(string?)message["state"]}" +
                                         ((string?)message["reason"] is { } reason ? $" ({reason})" : "") +
                                         ((string?)message["where"] is { } where ? $" at {where}" : "") +
                                         $", reported by the worker at {DateTimeOffset.UtcNow:O}";
                            break;
                        case "anomaly": _records.OnAnomaly(WorkerProtocol.ReadAnomaly(message)); break;
                        case "log": _records.OnLog(WorkerProtocol.ReadLog(message)); break;
                        case "console": _records.OnConsoleOutput(WorkerProtocol.ReadConsole(message)); break;
                        case "ended": _targetEnded = true; break;
                    }
                }
                else if (message["id"] is JsonNode healthId && message["health"] is JsonObject health
                         && _health.TryRemove((long)healthId, out var healthAnswer))
                {
                    healthAnswer.TrySetResult(health);
                }
                else if (message["id"] is JsonNode id && _pending.TryRemove((long)id, out var pending))
                {
                    pending.Answer.TrySetResult(new WorkerAnswer(
                        (string?)message["result"],
                        (string?)message["caption"],
                        message["image"] is JsonNode image ? Convert.FromBase64String((string)image!) : null,
                        (string?)message["session"] ?? WorkerProtocol.Ended,
                        (string?)message["lost"]));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe broke: the worker is gone, as at the end of its stdout.
        }
        _gone = true;
        _ready.TrySetResult(null);
        foreach (long id in _health.Keys)
            if (_health.TryRemove(id, out var health)) health.TrySetResult(null);
        foreach (long id in _pending.Keys)
            if (_pending.TryRemove(id, out var pending)) pending.Answer.TrySetResult(GoneBefore(pending.Tool));
    }

    // An answer the worker never gave. The supervisor discards the worker and answers instead (Gone).
    private static WorkerAnswer GoneBefore(string tool) => new(null, null, null, Gone);

    // The target's identity as drhook-mcp keeps it: on Windows a handle, opened now, while the worker holds the target.
    private static TargetIdentity IdentityOf(int pid, long? start, bool owned)
    {
        Process? handle = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                handle = Process.GetProcessById(pid);
                _ = handle.SafeHandle; // opened now: the process cannot be replaced under this handle
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                handle = null; // ended already, or not ours to open: the end is then unknown
            }
        }
        return new TargetIdentity(pid, start, owned, handle);
    }

    public void Dispose()
    {
        _target?.Handle?.Dispose();
        _process.Dispose();
    }
}
