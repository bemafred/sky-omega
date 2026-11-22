#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine probe 117 — CTRL+BREAK to a target on a console of its own (ADR-016 6.4) ===========================
//
// ADR-016 6.4 decided Windows' exit request: after the detach, CTRL+BREAK to the target's own process group. The event
// reaches only processes on the sender's console (GenerateConsoleCtrlEvent). Probe 112's sender shared the target's
// console by construction. DrHook does not: WindowsSpawn starts a target with CREATE_NO_WINDOW, a console of its own,
// and drhook-worker is on drhook-mcp's console (WorkerProcess.Start). The ADR: "Whether a helper attached to it
// (AttachConsole) can send the event is to be built and measured." This measures it, with no debugger: the detach
// before the request, and what a target does with the event after a detach, are finding 112's.
//
// Construction, three processes, one program in four roles:
//   parent   plays drhook-mcp. It needs a console (one is allocated when it has none, as in probe 112), and counts any
//            CTRL+BREAK it receives (a SIGQUIT handler that cancels it). It starts the sender as WorkerProcess.Start
//            starts a worker: the dotnet host of this runtime, `exec`, stdin and stdout redirected, stderr inherited,
//            so the sender is on the parent's console.
//   sender   plays drhook-worker: its handlers for SIGTERM, SIGINT and SIGHUP, and none for SIGQUIT, so a CTRL+BREAK
//            that reached it would end it. Per session it starts a target as WindowsSpawn starts one (suspended, stdin
//            NUL, stdout and stderr a pipe, only those handles inherited, CREATE_NO_WINDOW) with CREATE_NEW_PROCESS_GROUP
//            added, so the target is the root of its own group and the group's id is its PID. It waits for the target
//            to say it is ready, then sends CTRL+BREAK one of two ways:
//              worker: itself. It leaves its console (FreeConsole), attaches to the target's (AttachConsole), sends
//                      GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, target PID), leaves the target's console, returns to
//                      its parent's (AttachConsole(ATTACH_PARENT_PROCESS)) and sets its standard handles back.
//              helper: a process it starts with no window (CreateNoWindow, so a console of the helper's own; stdin
//                      redirected and closed, so it holds no handle of the sender's stdin) leaves that console,
//                      attaches to the target's, sends, and exits.
//   target   break-handler: a SIGQUIT handler cancels the event's default, writes "signal", and the program returns 0.
//            break-default: no handler; a ProcessExit handler writes "process-exit" if it runs. Each writes "ready"
//            once its handler is in place, and returns at 15 s if nothing came.
//
// Hypothesis: in every session, both ways, each call succeeds; a break-handler target runs its handler and returns 0,
// and a break-default target ends with 0xC000013A (finding 112's detached end), each within 5 s of the send; neither
// the sender nor the parent receives the event; and the worker way leaves the sender back on its parent's console with
// its standard handles as they were. Refuted by a call that fails, a target that does not end so within 5 s, a signal
// the sender receives or a sender that does not exit 0, a CTRL+BREAK the parent receives, or a sender not back on its
// parent's console or with its standard handles changed.
//
// Recorded, not predicted: whether AttachConsole replaces a process's standard handles, how long the sender is off its
// own console, and how long a helper takes from its start to its exit.
//
// RESULT(0) when it holds; 1 when it is refuted; 2 when it could not run (not Windows, or the sender did not start).
//
// Usage, on Windows, from poc/drhook-engine:
//   dotnet run --no-cache 117-ctrl-break.cs [--sessions N]     (N per way and kind, default 20)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
const uint CTRL_BREAK_EVENT = 1;
const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);
const uint CREATE_SUSPENDED = 0x00000004, CREATE_NEW_PROCESS_GROUP = 0x00000200, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_NO_WINDOW = 0x08000000;
const int STARTF_USESTDHANDLES = 0x00000100;
const uint HANDLE_FLAG_INHERIT = 0x1, GENERIC_READ = 0x80000000, FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3;
const nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("NOT RUN(2): probe 117 is for Windows (ADR-016 6.4)");
    return 2;
}
string role = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "parent";
return role switch
{
    "sender" => Sender(int.Parse(args[1], CultureInfo.InvariantCulture), int.Parse(args[2], CultureInfo.InvariantCulture)),
    "helper" => Helper(int.Parse(args[1], CultureInfo.InvariantCulture)),
    "target" => Target(args[1]),
    _ => Parent(args),
};

// ---- parent: drhook-mcp's place ------------------------------------------------------------------------------------

static int Parent(string[] args)
{
    int sessions = 20;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--sessions" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0) { sessions = n; i++; }
        else { Console.WriteLine($"FAIL(2): unknown argument '{args[i]}'. Usage: 117-ctrl-break.cs [--sessions N]"); return 2; }
    }
    Console.WriteLine($"probe 117: {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, {RuntimeInformation.FrameworkDescription}, {sessions} sessions per way and kind");

    uint[] onConsole = ConsoleProcesses();
    bool allocated = false;
    if (onConsole.Length == 0)
    {
        nint stdIn = GetStdHandle(STD_INPUT_HANDLE), stdOut = GetStdHandle(STD_OUTPUT_HANDLE), stdErr = GetStdHandle(STD_ERROR_HANDLE);
        allocated = AllocConsole();
        SetStdHandle(STD_INPUT_HANDLE, stdIn);
        SetStdHandle(STD_OUTPUT_HANDLE, stdOut);
        SetStdHandle(STD_ERROR_HANDLE, stdErr);
        onConsole = ConsoleProcesses();
    }
    Console.WriteLine($"parent: pid {Environment.ProcessId}, {onConsole.Length} processes on its console{(allocated ? " (allocated)" : "")}, standard handles {StdTypes()}");

    int breaksReceived = 0;
    using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context =>
    {
        context.Cancel = true;
        Interlocked.Increment(ref breaksReceived);
    });

    var start = new ProcessStartInfo(DotnetHost())
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = false, // as WorkerProcess.Start: the worker's stderr is drhook-mcp's
        UseShellExecute = false,
        StandardInputEncoding = new UTF8Encoding(false),
        StandardOutputEncoding = new UTF8Encoding(false),
    };
    foreach (string a in new[] { "exec", ThisDll(), "sender", sessions.ToString(CultureInfo.InvariantCulture), Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
        start.ArgumentList.Add(a);
    Process sender;
    try { sender = Process.Start(start)!; }
    catch (Exception e) { Console.WriteLine($"FAIL(2): the sender did not start: {e.GetType().Name}: {e.Message}"); return 2; }

    // Every session is bounded inside the sender; this bounds the sender as a whole, should a call never return.
    TimeSpan senderBound = TimeSpan.FromSeconds(60 + sessions * 4 * 30);
    bool senderKilled = false;
    var watch = new Thread(() =>
    {
        if (!sender.WaitForExit(senderBound)) { senderKilled = true; try { sender.Kill(); } catch (InvalidOperationException) { } }
    }) { IsBackground = true };
    watch.Start();

    var records = new List<JsonObject>();
    JsonObject? senderDone = null;
    string? line;
    while ((line = sender.StandardOutput.ReadLine()) is not null)
    {
        Console.WriteLine(line);
        if (line.StartsWith("session ", StringComparison.Ordinal)) records.Add(JsonNode.Parse(line["session ".Length..])!.AsObject());
        else if (line.StartsWith("sender-done ", StringComparison.Ordinal)) senderDone = JsonNode.Parse(line["sender-done ".Length..])!.AsObject();
    }
    sender.WaitForExit();
    int senderExit = sender.ExitCode;
    Thread.Sleep(500); // a CTRL+BREAK that reached the parent is counted by now

    Console.WriteLine();
    Console.WriteLine("| Way | Target | Sessions | Ended as stated | Send to end, ms, median [min-max] | Sender off its console, ms, max | Helper start to exit, ms, median [min-max] |");
    Console.WriteLine("|---|---|---|---|---|---|---|");
    foreach (string how in new[] { "worker", "helper" })
        foreach (string kind in new[] { "break-handler", "break-default" })
        {
            var group = records.Where(r => (string?)r["how"] == how && (string?)r["kind"] == kind).ToList();
            int ended = group.Count(r => (bool?)r["endedAsStated"] == true);
            string sendToEnd = Spread(group.Select(r => (double?)r["sendToEndMs"]));
            string offConsole = how == "worker" && group.Count > 0 ? $"{group.Max(r => (double?)r["worker"]?["offConsoleMs"] ?? 0):0.00}" : "-";
            string helperSpan = how == "helper" ? Spread(group.Select(r => (double?)r["helper"]?["startToExitMs"])) : "-";
            Console.WriteLine($"| {how} | {kind} | {group.Count} | {ended} | {sendToEnd} | {offConsole} | {helperSpan} |");
        }

    var refutations = new List<string>();
    foreach (JsonObject r in records)
        foreach (JsonNode? d in r["deviations"]!.AsArray())
            refutations.Add($"{r["how"]}/{r["kind"]} #{r["k"]}: {d}");
    if (records.Count != sessions * 4) refutations.Add($"{records.Count} sessions recorded, {sessions * 4} expected");
    if (senderKilled) refutations.Add($"the sender was ended at its bound, {senderBound.TotalSeconds:0} s");
    if (senderExit != 0) refutations.Add($"the sender exited 0x{senderExit:X8}");
    if (senderDone is null) refutations.Add("the sender did not finish");
    else
    {
        if (senderDone["signals"]!.AsArray().Count > 0) refutations.Add($"the sender received {senderDone["signals"]!.ToJsonString()}");
        Console.WriteLine($"sender at its end: {senderDone.ToJsonString()}");
    }
    if (breaksReceived > 0) refutations.Add($"the parent received {breaksReceived} CTRL+BREAK");
    Console.WriteLine($"parent: {breaksReceived} CTRL+BREAK received");

    if (refutations.Count == 0) { Console.WriteLine("RESULT(0): the hypothesis holds"); return 0; }
    Console.WriteLine($"RESULT(1): refuted, {refutations.Count} deviations:");
    foreach (string r in refutations.Take(40)) Console.WriteLine($"  {r}");
    return 1;
}

// ---- sender: drhook-worker's place ---------------------------------------------------------------------------------

static int Sender(int sessions, int parentPid)
{
    var signals = new ConcurrentQueue<string>();
    void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        signals.Enqueue(context.Signal.ToString());
    }
    // drhook-worker's handlers (Program.cs); it has none for SIGQUIT.
    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
    using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);

    uint[] onConsole = ConsoleProcesses();
    string std = StdTypes();
    Console.WriteLine($"sender: pid {Environment.ProcessId}, {onConsole.Length} processes on its console, the parent {(onConsole.Contains((uint)parentPid) ? "among them" : "NOT among them")}, standard handles {std}");
    Console.Out.Flush();

    string host = DotnetHost(), dll = ThisDll();
    foreach (string how in new[] { "worker", "helper" })
        for (int k = 1; k <= sessions; k++)
            foreach (string kind in new[] { "break-handler", "break-default" })
            {
                JsonObject record = Session(how, kind, k, host, dll, parentPid, std);
                Console.WriteLine("session " + record.ToJsonString());
                Console.Out.Flush();
            }

    Console.WriteLine("sender-done " + new JsonObject
    {
        ["signals"] = new JsonArray(signals.Select(s => (JsonNode?)s).ToArray()),
        ["processesOnConsole"] = ConsoleProcesses().Length,
        ["standardHandles"] = StdTypes(),
    }.ToJsonString());
    return 0;
}

static JsonObject Session(string how, string kind, int k, string host, string dll, int parentPid, string stdAtStart)
{
    var record = new JsonObject { ["how"] = how, ["kind"] = kind, ["k"] = k };
    var deviations = new List<string>();
    Spawned target;
    try { target = SpawnTarget(host, new[] { "exec", dll, "target", kind }); }
    catch (Win32Exception e)
    {
        deviations.Add($"the target was not started: {e.Message} ({e.NativeErrorCode})");
        record["deviations"] = new JsonArray(deviations.Select(d => (JsonNode?)d).ToArray());
        return record;
    }
    record["targetPid"] = target.Pid;

    var lines = new ConcurrentQueue<(long At, string Text)>();
    using var ready = new ManualResetEventSlim();
    var reader = new Thread(() =>
    {
        using var stream = new FileStream(target.StdoutRead, FileAccess.Read, 1);
        using var text = new StreamReader(stream, new UTF8Encoding(false));
        string? l;
        while ((l = text.ReadLine()) is not null)
        {
            lines.Enqueue((Stopwatch.GetTimestamp(), l));
            if (l.StartsWith("ready", StringComparison.Ordinal)) ready.Set();
        }
    }) { IsBackground = true };
    reader.Start();
    ResumeThread(target.Thread);

    long? sentAt = null;
    if (!ready.Wait(10_000)) deviations.Add("the target did not say it was ready within 10 s");
    else if (how == "worker")
    {
        nint[] std = { GetStdHandle(STD_INPUT_HANDLE), GetStdHandle(STD_OUTPUT_HANDLE), GetStdHandle(STD_ERROR_HANDLE) };
        long off = Stopwatch.GetTimestamp();
        bool freed = FreeConsole();
        int freeError = freed ? 0 : Marshal.GetLastPInvokeError();
        bool attached = AttachConsole((uint)target.Pid);
        int attachError = attached ? 0 : Marshal.GetLastPInvokeError();
        uint[] onTarget = attached ? ConsoleProcesses() : Array.Empty<uint>();
        bool stdReplaced = GetStdHandle(STD_INPUT_HANDLE) != std[0] || GetStdHandle(STD_OUTPUT_HANDLE) != std[1] || GetStdHandle(STD_ERROR_HANDLE) != std[2];
        bool sent = false;
        int sendError = 0;
        if (attached)
        {
            sent = GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)target.Pid);
            sendError = sent ? 0 : Marshal.GetLastPInvokeError();
            sentAt = Stopwatch.GetTimestamp();
        }
        bool left = !attached || FreeConsole();
        int leaveError = left ? 0 : Marshal.GetLastPInvokeError();
        bool back = AttachConsole(ATTACH_PARENT_PROCESS);
        int backError = back ? 0 : Marshal.GetLastPInvokeError();
        SetStdHandle(STD_INPUT_HANDLE, std[0]);
        SetStdHandle(STD_OUTPUT_HANDLE, std[1]);
        SetStdHandle(STD_ERROR_HANDLE, std[2]);
        double offMs = Stopwatch.GetElapsedTime(off).TotalMilliseconds;
        uint[] after = ConsoleProcesses();
        string stdAfter = StdTypes();
        record["worker"] = new JsonObject
        {
            ["freeConsole"] = freed ? "ok" : $"error {freeError}",
            ["attachConsole"] = attached ? "ok" : $"error {attachError}",
            ["onTargetConsole"] = new JsonArray(onTarget.Select(p => (JsonNode?)(p == target.Pid ? "target" : p == Environment.ProcessId ? "sender" : p.ToString(CultureInfo.InvariantCulture))).ToArray()),
            ["standardHandlesReplacedByAttach"] = stdReplaced,
            ["send"] = !attached ? "not sent" : sent ? "ok" : $"error {sendError}",
            ["leave"] = left ? "ok" : $"error {leaveError}",
            ["returnToParent"] = back ? "ok" : $"error {backError}",
            ["offConsoleMs"] = Math.Round(offMs, 3),
            ["processesOnConsoleAfter"] = after.Length,
            ["standardHandlesAfter"] = stdAfter,
        };
        if (!freed) deviations.Add($"FreeConsole failed, error {freeError}");
        if (!attached) deviations.Add($"AttachConsole(target) failed, error {attachError}");
        else if (!onTarget.Contains((uint)target.Pid)) deviations.Add("the target was not on the console attached to");
        if (attached && !sent) deviations.Add($"GenerateConsoleCtrlEvent failed, error {sendError}");
        if (!left) deviations.Add($"FreeConsole(target's console) failed, error {leaveError}");
        if (!back) deviations.Add($"AttachConsole(ATTACH_PARENT_PROCESS) failed, error {backError}");
        else if (!after.Contains((uint)parentPid) || !after.Contains((uint)Environment.ProcessId)) deviations.Add("the sender and its parent were not on one console after the return");
        if (stdAfter != stdAtStart) deviations.Add($"the standard handles were {stdAfter} after the return, {stdAtStart} at the start");
    }
    else
    {
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (string a in new[] { "exec", dll, "helper", target.Pid.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(a);
        long started = Stopwatch.GetTimestamp();
        using Process helper = Process.Start(start)!;
        helper.StandardInput.Close();
        Task<string> output = helper.StandardOutput.ReadToEndAsync();
        bool exited = helper.WaitForExit(10_000);
        double spanMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!exited) { try { helper.Kill(); } catch (InvalidOperationException) { } deviations.Add("the helper did not exit within 10 s"); }
        else
        {
            helper.WaitForExit();
            string json = output.Result.Split('\n').FirstOrDefault(l => l.StartsWith('{')) ?? "";
            JsonObject? said = json.Length > 0 ? JsonNode.Parse(json)!.AsObject() : null;
            if (said is null) deviations.Add($"the helper said nothing readable (exit 0x{helper.ExitCode:X8}): {output.Result.Trim()}");
            else
            {
                said["startToExitMs"] = Math.Round(spanMs, 3);
                said["exitCode"] = helper.ExitCode;
                record["helper"] = said.DeepClone();
                sentAt = (long?)said["sentAt"];
                foreach (string step in new[] { "freeConsole", "attachConsole", "send" })
                    if ((string?)said[step] != "ok") deviations.Add($"the helper's {step}: {said[step]}");
                if (said["onTargetConsole"]!.AsArray().All(p => (string?)p != "target")) deviations.Add("the target was not on the console the helper attached to");
            }
        }
    }

    uint waited = WaitForSingleObject(target.Process, 5000);
    long endedAt = Stopwatch.GetTimestamp();
    if (waited != 0)
    {
        deviations.Add(sentAt is null ? "the target was not asked, and was ended" : "the target did not end within 5 s of the send, and was ended");
        TerminateProcess(target.Process, 1);
        WaitForSingleObject(target.Process, 5000);
    }
    GetExitCodeProcess(target.Process, out uint code);
    reader.Join(2000);
    record["exitCode"] = $"0x{code:X8}";
    if (sentAt is long sent0)
    {
        if (waited == 0) record["sendToEndMs"] = Math.Round(Stopwatch.GetElapsedTime(sent0, endedAt).TotalMilliseconds, 3);
        record["lines"] = new JsonArray(lines.Select(l => (JsonNode?)$"{Stopwatch.GetElapsedTime(sent0, l.At).TotalMilliseconds:0.000} ms: {l.Text}").ToArray());
    }
    else record["lines"] = new JsonArray(lines.Select(l => (JsonNode?)l.Text).ToArray());

    string[] said2 = lines.Select(l => l.Text).ToArray();
    bool endedAsStated = waited == 0 && (kind == "break-handler"
        ? code == 0 && said2.Contains("signal") && said2.Contains("cancelled")
        : code == 0xC000013A);
    record["endedAsStated"] = endedAsStated;
    if (waited == 0 && !endedAsStated) deviations.Add($"the target ended with 0x{code:X8}, saying {string.Join(", ", said2)}");
    record["deviations"] = new JsonArray(deviations.Select(d => (JsonNode?)d).ToArray());
    target.Dispose();
    return record;
}

// ---- helper: a process of its own that attaches and sends ----------------------------------------------------------

static int Helper(int pid)
{
    nint[] std = { GetStdHandle(STD_INPUT_HANDLE), GetStdHandle(STD_OUTPUT_HANDLE), GetStdHandle(STD_ERROR_HANDLE) };
    uint[] own = ConsoleProcesses();
    bool freed = FreeConsole();
    int freeError = freed ? 0 : Marshal.GetLastPInvokeError();
    bool attached = AttachConsole((uint)pid);
    int attachError = attached ? 0 : Marshal.GetLastPInvokeError();
    uint[] onTarget = attached ? ConsoleProcesses() : Array.Empty<uint>();
    bool stdReplaced = GetStdHandle(STD_INPUT_HANDLE) != std[0] || GetStdHandle(STD_OUTPUT_HANDLE) != std[1] || GetStdHandle(STD_ERROR_HANDLE) != std[2];
    bool sent = false;
    int sendError = 0;
    long sentAt = 0;
    if (attached)
    {
        sent = GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)pid);
        sendError = sent ? 0 : Marshal.GetLastPInvokeError();
        sentAt = Stopwatch.GetTimestamp(); // QueryPerformanceCounter: one clock for every process on the machine
    }
    // Its answer goes to its stdout, the sender's pipe, whatever the attach did to the standard handles.
    SetStdHandle(STD_INPUT_HANDLE, std[0]);
    SetStdHandle(STD_OUTPUT_HANDLE, std[1]);
    SetStdHandle(STD_ERROR_HANDLE, std[2]);
    Console.WriteLine(new JsonObject
    {
        ["processesOnOwnConsole"] = own.Length,
        ["freeConsole"] = freed ? "ok" : $"error {freeError}",
        ["attachConsole"] = attached ? "ok" : $"error {attachError}",
        ["onTargetConsole"] = new JsonArray(onTarget.Select(p => (JsonNode?)(p == pid ? "target" : p == Environment.ProcessId ? "helper" : p.ToString(CultureInfo.InvariantCulture))).ToArray()),
        ["standardHandlesReplacedByAttach"] = stdReplaced,
        ["send"] = !attached ? "not sent" : sent ? "ok" : $"error {sendError}",
        ["sentAt"] = attached ? sentAt : null,
    }.ToJsonString());
    return sent ? 0 : 1;
}

// ---- target --------------------------------------------------------------------------------------------------------

static int Target(string kind)
{
    using var signalled = new ManualResetEventSlim();
    using PosixSignalRegistration? registration = kind == "break-handler"
        ? PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context =>
        {
            context.Cancel = true;
            Console.WriteLine("signal");
            signalled.Set();
        })
        : null;
    if (registration is null) AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.WriteLine("process-exit");
    Console.WriteLine($"ready pid={Environment.ProcessId}");
    signalled.Wait(TimeSpan.FromSeconds(15));
    Console.WriteLine(signalled.IsSet ? "cancelled" : "cap");
    return 0;
}

// ---- what the roles share ------------------------------------------------------------------------------------------

// The dotnet host of this runtime (<root>/shared/Microsoft.NETCore.App/<version>/), as WorkerProcess.Host.
static string DotnetHost() => Path.Combine(
    Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")), "dotnet.exe");

// This program's assembly, beside its runtimeconfig, which `dotnet exec` runs.
static string ThisDll() => Path.Combine(AppContext.BaseDirectory, typeof(Spawned).Assembly.GetName().Name + ".dll");

static uint[] ConsoleProcesses()
{
    var list = new uint[64];
    uint count = GetConsoleProcessList(list, (uint)list.Length);
    return count == 0 || count > list.Length ? Array.Empty<uint>() : list.Take((int)count).ToArray();
}

static string StdTypes() => string.Join("/", new[] { STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE }
    .Select(h => GetFileType(GetStdHandle(h)) switch { 1 => "disk", 2 => "char", 3 => "pipe", 0 => "unknown", var t => t.ToString(CultureInfo.InvariantCulture) }));

static string Spread(IEnumerable<double?> values)
{
    double[] sorted = values.Where(v => v is not null).Select(v => v!.Value).Order().ToArray();
    if (sorted.Length == 0) return "-";
    double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    return $"{median:0.00} [{sorted[0]:0.00}-{sorted[^1]:0.00}]";
}

// As WindowsSpawn.SpawnSuspendedRedirected (src/DrHook.Engine/Interop/WindowsSpawn.cs), with CREATE_NEW_PROCESS_GROUP
// added: suspended, stdin NUL, stdout and stderr one pipe (WindowsSpawn gives each its own), only those handles
// inherited, CREATE_NO_WINDOW. The environment is inherited (WindowsSpawn passes this process's, with overrides).
static unsafe Spawned SpawnTarget(string host, string[] args)
{
    SECURITY_ATTRIBUTES inheritable = new() { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
    if (!CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref inheritable, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe");
    SafeFileHandle? nul = null;
    nint list = 0;
    try
    {
        if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation");
        nul = CreateFileW("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref inheritable, OPEN_EXISTING, 0, 0);
        if (nul.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFile(NUL)");
        nint bytes = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
        list = Marshal.AllocHGlobal(bytes);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList");
        nint* inherited = stackalloc nint[2];
        inherited[0] = nul.DangerousGetHandle();
        inherited[1] = write.DangerousGetHandle();
        if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, 2 * sizeof(nint), 0, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute");
        STARTUPINFOEXW startup = default;
        startup.StartupInfo.cb = sizeof(STARTUPINFOEXW);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = inherited[0];
        startup.StartupInfo.hStdOutput = inherited[1];
        startup.StartupInfo.hStdError = inherited[1];
        startup.lpAttributeList = list;
        static string Quoted(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
        char[] commandLine = (string.Join(" ", new[] { host }.Concat(args).Select(Quoted)) + "\0").ToCharArray();
        PROCESS_INFORMATION info;
        bool created;
        fixed (char* p = commandLine)
            created = CreateProcessW(null, p, 0, 0, 1,
                CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP,
                0, 0, ref startup, out info);
        if (!created) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess");
        return new Spawned((int)info.dwProcessId, new SafeProcessHandle(info.hProcess, true), new SafeFileHandle(info.hThread, true), read);
    }
    catch
    {
        read.Dispose();
        throw;
    }
    finally
    {
        write.Dispose(); // the child holds its own copy; ours must close, or the read end never sees the end
        nul?.Dispose();
        if (list != 0)
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }
}

[DllImport("kernel32", SetLastError = true)] static extern bool FreeConsole();
[DllImport("kernel32", SetLastError = true)] static extern bool AttachConsole(uint processId);
[DllImport("kernel32", SetLastError = true)] static extern bool AllocConsole();
[DllImport("kernel32", SetLastError = true)] static extern uint GetConsoleProcessList(uint[] processList, uint count);
[DllImport("kernel32", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
[DllImport("kernel32", SetLastError = true)] static extern nint GetStdHandle(int which);
[DllImport("kernel32", SetLastError = true)] static extern bool SetStdHandle(int which, nint handle);
[DllImport("kernel32")] static extern uint GetFileType(nint handle);
[DllImport("kernel32", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, uint size);
[DllImport("kernel32", SetLastError = true)] static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES attributes, uint disposition, uint flags, nint template);
[DllImport("kernel32", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
[DllImport("kernel32", SetLastError = true)] static extern unsafe bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint* value, nint size, nint previousValue, nint returnSize);
[DllImport("kernel32")] static extern void DeleteProcThreadAttributeList(nint list);
[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern unsafe bool CreateProcessW(string? applicationName, char* commandLine, nint processAttributes, nint threadAttributes, int inheritHandles, uint creationFlags, nint environment, nint currentDirectory, ref STARTUPINFOEXW startupInfo, out PROCESS_INFORMATION processInformation);
[DllImport("kernel32", SetLastError = true)] static extern uint ResumeThread(SafeFileHandle thread);
[DllImport("kernel32", SetLastError = true)] static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
[DllImport("kernel32", SetLastError = true)] static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
[DllImport("kernel32", SetLastError = true)] static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

sealed record Spawned(int Pid, SafeProcessHandle Process, SafeFileHandle Thread, SafeFileHandle StdoutRead) : IDisposable
{
    public void Dispose()
    {
        Thread.Dispose();
        Process.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
struct SECURITY_ATTRIBUTES
{
    public int nLength;
    public nint lpSecurityDescriptor;
    public int bInheritHandle;
}

[StructLayout(LayoutKind.Sequential)]
struct STARTUPINFOW
{
    public int cb;
    public nint lpReserved, lpDesktop, lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2;
    public nint lpReserved2, hStdInput, hStdOutput, hStdError;
}

[StructLayout(LayoutKind.Sequential)]
struct STARTUPINFOEXW
{
    public STARTUPINFOW StartupInfo;
    public nint lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
struct PROCESS_INFORMATION
{
    public nint hProcess, hThread;
    public uint dwProcessId, dwThreadId;
}
