#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
//
// DrHook.Engine probe 118 — what holds a Windows target after a detach (ADR-016 6.6) =================================
//
// Question: when a borrowed detach leaves a Windows target held, what holds it? And how often does it happen?
//
// Why: after a detach DrHook means to leave the target running, un-debugged. On omega-windows it stayed held 4 times in
// 200 detach sessions (findings 102 and 112), and once on the Windows x64 runner (finding 112, 2026-11-23). Every held
// target looked like one stuck thread: in finding 102 the main thread never returned from the flood thread's Join, or
// the process never finished exiting after Main returned; in finding 112 a handler ran managed code after the detach,
// and the cancellation it started never completed. No state was recorded. ADR-016 6.6 decides that the teardown leaves
// no callback un-continued (each detach drops one, finding 102), measured before and after the change; this probe is
// the before, and records what holds the target.
//
// What is known without it (read in dotnet/runtime release/10.0, 2026-11-24):
//   - On Windows mscordbi also debugs the target natively (DebugActiveProcess), and continues every native debug event
//     at once in managed-only debugging (ShimProcess::HandleWin32DebugEvent). Detach ends that with
//     DebugActiveProcessStop (CordbWin32EventThread::ExitProcess).
//   - mscordbi asks for DebugSetProcessKillOnExit(FALSE), but the Windows pipeline only stores the value and never
//     passes it to Windows (WindowsNativePipeline::DebugSetProcessKillOnExit, windowspipeline.cpp). So a target still
//     natively debugged dies when its debugger process exits. Finding 102's held target outlived the debugger process,
//     so it was not natively debugged any more. This probe asks Windows directly (CheckRemoteDebuggerPresent).
//   - The target's runtime releases every thread it stopped for the debugger when it gets the detach
//     (DB_IPCE_DETACH_FROM_PROCESS -> ResumeThreads, debugger.cpp). A callback DrHook never continued is released
//     with them, unless the detach found the runtime in some other state.
//   - CordbProcess::Detach requires a synchronized process, and that "the user has drained the managed event queue,
//     but there is currently no way to really enforce that here" (process.cpp).
//
// Construction: probe 102's detach-then-end. The target floods thread events (one empty thread started and joined
// after another); the debugger attaches (borrowed, DebugSession.Attach), waits for 10 CreateThread events and a random
// 0 to 499 ms more, and disposes (synchronize, then detach). The target floods 1 s more, stops, joins the flood thread
// and returns. Each target writes the phases it reaches (attached, detached, stop, joined, done) to a file.
//
// Recorded per session: the attach and Dispose times; the debugger's events; its anomalies, and of them the callbacks
// the teardown dropped (LateCallback), by name; how long the target took to end after Dispose, its exit code and its
// phases.
//
// A held target: one still alive 10 s after Dispose returned (finding 102: they end 0.9 to 2.5 s after it). Recorded,
// passive first, each part on its own line as soon as it is taken, so a part that hangs leaves the earlier ones:
//   os        whether a native debugger is attached (CheckRemoteDebuggerPresent); each thread's state and wait
//             reason, suspend count (NtQueryInformationThread, ThreadSuspendCount), name (GetThreadDescription; .NET
//             names its own threads, and the target names its flood thread) and start address, as module+offset;
//   phases    the last phase the target's main thread reached;
//   dump      a dump with the managed heap, written by the target's runtime when asked through its diagnostics port
//             (createdump.exe no longer takes a pid), kept beside the facts (at most --dumps of them; nothing on
//             omega-windows reads one yet, so it is kept for later);
//   managed   each managed thread's stack, sampled through EventPipe for 1 s (DrHook's StackInspector);
//   reattach  a fresh attach: does it succeed, what does mscordbi report, and does the target end after the detach;
//   os-after  the threads again, when the target is still held after that.
// Then the probe ends the target.
//
// Bounds: a step of the child that does not finish within its bound is reported by a watchdog thread in the child; the
// parent then ends the child (a call blocked inside mscordbi cannot be cancelled), and waits for its targets: one still
// alive 75 s after it started is ended, and the record says so.
//
// N: --children (default 20) child processes of --sessions (default 20) sessions each. At finding 112's rate on
// omega-windows (3 held in 140), 400 sessions hold about 8 targets; 0 in 400 has probability 0.0002 at that rate.
//
// Windows only: no other machine has held a target after a detach.
//
// The probe is self-contained, as each probe here is: its record must not change when another probe's code does.
//
// Output: a report on stdout; with --out <file>, the facts as JSON, and the dumps in <file>-dumps.
// Result codes: 0 no target held, and nothing else deviated; 1 otherwise, which is a finding about DrHook or mscordbi,
// not a fault of the probe; 2 the probe could not run (not Windows, bad arguments, the target build failed).
//
// Usage (from poc/drhook-engine):
//   dotnet run --no-cache 118-held-after-detach-smoke.cs [--children N] [--sessions N] [--dumps N] [--out facts.json]

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Diagnostics;

if (args.Length == 7 && args[0] == "child")
    return Child(args[1], args[2], int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture), args[5], int.Parse(args[6], CultureInfo.InvariantCulture));

int childCount = 20, sessions = 20, maxDumps = 3;
string? outFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--children" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int c) && c > 0) { childCount = c; i++; }
    else if (args[i] == "--sessions" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int n) && n > 0) { sessions = n; i++; }
    else if (args[i] == "--dumps" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int d) && d >= 0) { maxDumps = d; i++; }
    else if (args[i] == "--out" && i + 1 < args.Length) outFile = Path.GetFullPath(args[++i]);
    else { Console.WriteLine($"FAIL(2): unknown argument '{args[i]}'. Usage: 118-held-after-detach-smoke.cs [--children N] [--sessions N] [--dumps N] [--out facts.json]"); return 2; }
}
if (!OperatingSystem.IsWindows()) { Console.WriteLine("FAIL(2): probe 118 is Windows only (no other machine has held a target after a detach)"); return 2; }

string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
string host = DotnetHostOf(runtimeDirectory);
string? dll = BuildTarget(host, Path.GetFullPath("118-held-after-detach-target.cs"));
if (dll is null) { Console.WriteLine("FAIL(2): could not build 118-held-after-detach-target.cs"); return 2; }
string dumpDirectory = (outFile ?? Path.GetFullPath("probe118.json")) + "-dumps";
Directory.CreateDirectory(dumpDirectory);

var environment = new JsonObject
{
    ["machine"] = Environment.MachineName,
    ["os"] = RuntimeInformation.OSDescription,
    ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
    ["cores"] = Environment.ProcessorCount,
    ["probeRuntime"] = RuntimeInformation.FrameworkDescription,
    ["runtimeDirectory"] = runtimeDirectory,
    ["dotnetHost"] = host,
    ["children"] = childCount,
    ["sessionsPerChild"] = sessions,
    ["dumpDirectory"] = dumpDirectory,
    ["commit"] = Run("git", "rev-parse", "HEAD").Trim(),
    ["ci"] = string.Join(' ', new[] { "GITHUB_ACTIONS", "RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion" }
        .Select(v => (v, Environment.GetEnvironmentVariable(v))).Where(p => p.Item2 is not null).Select(p => $"{p.v}={p.Item2}")),
};
Console.WriteLine("environment:");
foreach (var (key, value) in environment) Console.WriteLine($"  {key}: {value?.ToJsonString().Trim('"')}");

var children = new JsonArray();
int held = 0, total = 0, dumpsTaken = 0;
bool clean = true;
var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
for (int k = 1; k <= childCount; k++)
{
    int seed = 118_000 + k;
    Console.WriteLine($"child {k} of {childCount} (seed {seed}):");
    JsonObject record = RunChild(host, dll, sessions, seed, dumpDirectory, Math.Max(0, maxDumps - dumpsTaken));
    foreach (JsonNode? s in record["sessions"]!.AsArray())
    {
        total++;
        if (s!["held"] is not null) held++;
        if (s["held"]?["dump"]?["ok"]?.GetValue<bool>() == true) dumpsTaken++;
        foreach (JsonNode? name in s["dropped"]!.AsArray()) dropped[name!.GetValue<string>()] = dropped.GetValueOrDefault(name.GetValue<string>()) + 1;
    }
    clean &= record["outcome"]!.GetValue<string>() == "clean";
    children.Add(record);
}

Console.WriteLine("summary:");
Console.WriteLine($"  sessions {total}, held {held}");
Console.WriteLine($"  dropped callbacks: {(dropped.Count == 0 ? "none" : string.Join(", ", dropped.Select(kv => $"{kv.Key} {kv.Value}")))}");
foreach (JsonNode? child in children)
foreach (JsonNode? s in child!["sessions"]!.AsArray())
{
    if (s!["held"] is not JsonObject h) continue;
    var suspended = h["os"]?["threads"]?.AsArray().Where(t => t?["suspendCount"] is JsonValue v && v.TryGetValue(out uint count) && count > 0).Select(t => $"{t!["tid"]}({t["name"]?.GetValue<string>() ?? "-"})").ToList();
    Console.WriteLine($"  held pid {s["pid"]}: debugger present {h["os"]?["debuggerPresent"]?.ToJsonString() ?? "?"}, suspended threads {(suspended is null ? "?" : suspended.Count == 0 ? "none" : string.Join(" ", suspended))}, last phase {h["phases"]?.AsArray().LastOrDefault()?.GetValue<string>() ?? "none"}, dropped {s["dropped"]!.ToJsonString()}, after a reattach and detach ended {h["reattach"]?["endedAfterDetach"]?.ToJsonString() ?? "?"}");
}
if (outFile is not null)
{
    var report = new JsonObject { ["probe"] = "118-held-after-detach", ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["environment"] = environment, ["children"] = children };
    File.WriteAllText(outFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"facts written to {outFile}");
}
Console.WriteLine(clean
    ? "RESULT(0): no target was held, and nothing else deviated"
    : "RESULT(1): a target was held or a session deviated (see above); a finding, not a fault of the probe");
return clean ? 0 : 1;

// ---- the parent: one child process per batch of sessions -----------------------------------------------------------

static JsonObject RunChild(string host, string dll, int sessions, int seed, string dumpDirectory, int dumpsLeft)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { "child", host, dll, sessions.ToString(CultureInfo.InvariantCulture), seed.ToString(CultureInfo.InvariantCulture), dumpDirectory, dumpsLeft.ToString(CultureInfo.InvariantCulture) })
        psi.ArgumentList.Add(a);
    using Process child = Process.Start(psi)!;
    var errors = new List<string>();
    child.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) if (errors.Count < 100) errors.Add(e.Data); };
    child.BeginErrorReadLine();
    var lines = new BlockingCollection<string?>();
    var reader = new Thread(() =>
    {
        string? line;
        while ((line = child.StandardOutput.ReadLine()) is not null) lines.Add(line);
        lines.Add(null);
    }) { IsBackground = true };
    reader.Start();

    var record = new JsonObject { ["seed"] = seed };
    var sessionFacts = new JsonArray();
    var heldParts = new Dictionary<int, JsonObject>();
    var parentActions = new JsonArray();
    var targets = new List<(int Pid, Stopwatch Since)>();
    var endedPids = new HashSet<int>();
    string outcome = "child-ended-without-result";
    string lastStage = "";
    while (true)
    {
        // The longest silent step of the child is the dump, bounded at 90 s.
        if (!lines.TryTake(out string? line, TimeSpan.FromSeconds(120))) { outcome = "child-silent"; parentActions.Add($"no output from the child for 120 s after '{lastStage}'"); break; }
        if (line is null) break;
        if (line.StartsWith("child: stage=", StringComparison.Ordinal)) { lastStage = line["child: stage=".Length..]; continue; }
        if (line.StartsWith("child: target=", StringComparison.Ordinal)) { targets.Add((int.Parse(line["child: target=".Length..], CultureInfo.InvariantCulture), Stopwatch.StartNew())); continue; }
        if (line.StartsWith("child: held=", StringComparison.Ordinal))
        {
            JsonNode part = JsonNode.Parse(line["child: held=".Length..])!;
            int session = part["session"]!.GetValue<int>();
            if (!heldParts.TryGetValue(session, out JsonObject? parts)) heldParts[session] = parts = new JsonObject();
            parts[part["part"]!.GetValue<string>()] = part["value"]!.DeepClone();
            Console.WriteLine($"    {session,3} held, {part["part"]}: {Shorten(part["value"]!.ToJsonString())}");
            continue;
        }
        if (line.StartsWith("child: session=", StringComparison.Ordinal))
        {
            JsonNode s = JsonNode.Parse(line["child: session=".Length..])!;
            int session = s["session"]!.GetValue<int>();
            if (heldParts.Remove(session, out JsonObject? parts)) s["held"] = parts;
            sessionFacts.Add(s);
            if (s["ended"]?.GetValue<bool>() == true) endedPids.Add(s["pid"]!.GetValue<int>());
            Console.WriteLine($"    {session,3} pid {s["pid"]}, attach {s["attachMs"]} ms, dispose {s["disposeMs"]} ms, ended {s["endMs"]?.ToJsonString() ?? "-"} ms after it, exit {s["exitCode"]?.ToJsonString() ?? "-"}, dropped {s["dropped"]!.ToJsonString()}{(s["deviations"]!.AsArray().Count == 0 ? "" : "  DEVIATIONS: " + s["deviations"]!.ToJsonString())}");
            continue;
        }
        if (line.StartsWith("child: stuck=", StringComparison.Ordinal))
        {
            record["stuck"] = JsonNode.Parse(line["child: stuck=".Length..]);
            outcome = "stuck";
            Console.WriteLine($"    STUCK: {line["child: stuck=".Length..]}");
            break;
        }
        if (line.StartsWith("child: result=", StringComparison.Ordinal)) { outcome = line["child: result=".Length..].Trim(); break; }
    }

    if (!child.WaitForExit(outcome is "stuck" or "child-silent" ? 2000 : 10_000))
    {
        child.Kill();
        child.WaitForExit(5000);
        parentActions.Add($"ended the child ({outcome}, at '{lastStage}'): a call blocked inside mscordbi cannot be cancelled");
    }
    // A session the child did not finish keeps the held parts it reported.
    foreach (var (session, parts) in heldParts) sessionFacts.Add(new JsonObject { ["session"] = session, ["unfinished"] = true, ["held"] = parts, ["dropped"] = new JsonArray(), ["deviations"] = new JsonArray("unfinished") });

    foreach (var (pid, since) in targets)
    {
        if (endedPids.Contains(pid) || !TargetAlive(pid)) continue;
        while (TargetAlive(pid) && since.Elapsed < TimeSpan.FromSeconds(75)) Thread.Sleep(200);
        if (!TargetAlive(pid)) { parentActions.Add($"target {pid} ended {since.ElapsedMilliseconds} ms after it started, after the child was gone"); continue; }
        try
        {
            using Process leftover = Process.GetProcessById(pid);
            if (leftover.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                leftover.Kill();
                parentActions.Add($"target {pid} still alive 75 s after it started: ended by the parent");
            }
        }
        catch (ArgumentException) { }
    }

    record["sessions"] = sessionFacts;
    record["outcome"] = outcome;
    record["parentActions"] = parentActions;
    foreach (var action in parentActions) Console.WriteLine($"    parent: {action}");
    if (outcome != "clean")
        lock (errors) record["stderr"] = new JsonArray(errors.Select(e => (JsonNode?)e).ToArray());
    return record;
}

static string Shorten(string text) => text.Length <= 240 ? text : text[..240] + "…";

// ---- the child: N sessions in one process --------------------------------------------------------------------------

static int Child(string host, string dll, int sessions, int seed, string dumpDirectory, int dumpsLeft)
{
    var random = new Random(seed);
    var steps = new StepClock();
    void Line(string text) { lock (steps) { Console.WriteLine($"child: {text}"); Console.Out.Flush(); } }
    void Step(int session, string name, int boundMs) { steps.Enter(session, name, boundMs); Line($"stage={session} {name}"); }
    void Held(int session, string part, JsonNode value) => Line($"held={new JsonObject { ["session"] = session, ["part"] = part, ["value"] = value }.ToJsonString()}");

    var watchdog = new Thread(() =>
    {
        while (true)
        {
            Thread.Sleep(500);
            if (steps.Overdue() is not { } overdue) continue;
            Line($"stuck={new JsonObject { ["session"] = overdue.Session, ["step"] = overdue.Step, ["elapsedMs"] = overdue.ElapsedMs }.ToJsonString()}");
        }
    }) { IsBackground = true };
    watchdog.Start();

    bool allClean = true;
    for (int k = 1; k <= sessions; k++)
    {
        var s = new JsonObject { ["session"] = k };
        var deviations = new JsonArray();
        string phaseFile = Path.Combine(Path.GetTempPath(), $"probe118-phases-{Environment.ProcessId}-{k}");
        File.Delete(phaseFile);

        Step(k, "start-target", 30_000);
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(phaseFile);
        using Process target = Process.Start(start)!;
        target.StandardOutput.ReadLine(); // its first line: its runtime is up
        Line($"target={target.Id}");
        s["pid"] = target.Id;

        var sink = new Sink();
        Step(k, "attach", 30_000);
        var clock = Stopwatch.StartNew();
        DebugSession? session = null;
        try { session = DebugSession.Attach(target.Id, sink); }
        catch (Exception e) { s["attachError"] = $"{e.GetType().Name}: {e.Message}"; deviations.Add("attach failed"); }
        s["attachMs"] = clock.ElapsedMilliseconds;

        if (session is not null)
        {
            Step(k, "wait-flood", 40_000);
            clock.Restart();
            while (sink.CountOf("CreateThread") < 10 && clock.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(5);
            if (sink.CountOf("CreateThread") < 10) deviations.Add("no flood within 30 s");
            int delay = random.Next(0, 500);
            Thread.Sleep(delay);
            s["detachDelayMs"] = delay;

            Step(k, "dispose", 30_000);
            clock.Restart();
            try { session.Dispose(); }
            catch (Exception e) { s["disposeError"] = $"{e.GetType().Name}: {e.Message}"; deviations.Add("Dispose threw"); }
            s["disposeMs"] = clock.ElapsedMilliseconds;
        }

        Step(k, "wait-end", 30_000);
        clock.Restart();
        bool ended = target.WaitForExit(10_000);
        s["ended"] = ended;
        s["endMs"] = ended ? clock.ElapsedMilliseconds : null;
        s["exitCode"] = ended ? target.ExitCode : null;
        s["events"] = sink.Histogram();
        s["dropped"] = sink.Dropped();
        JsonArray others = sink.OtherAnomalies();
        s["otherAnomalies"] = others;
        if (others.Count > 0) deviations.Add("anomalies other than a dropped callback");

        if (!ended)
        {
            deviations.Add("held: the target did not end within 10 s of the detach");

            Step(k, "held-os", 30_000);
            Held(k, "os", OsState(target.Id));
            Held(k, "phases", Phases(phaseFile));

            if (dumpsLeft > 0)
            {
                Step(k, "held-dump", 90_000);
                JsonObject dump = Dump(target.Id, dumpDirectory);
                if (dump["ok"]?.GetValue<bool>() == true) dumpsLeft--;
                Held(k, "dump", dump);
            }

            Step(k, "held-managed", 40_000);
            Held(k, "managed", ManagedStacks(target.Id));

            Step(k, "held-reattach", 60_000);
            JsonObject reattach = Reattach(target);
            Held(k, "reattach", reattach);

            if (!target.HasExited)
            {
                Step(k, "held-os-after", 30_000);
                Held(k, "os-after", OsState(target.Id));
                Step(k, "held-kill", 15_000);
                target.Kill();
                target.WaitForExit(5000);
                Held(k, "ended-by", "the probe, after the record");
            }
            else Held(k, "ended-by", "itself, after the reattach and detach");
        }

        s["phases"] = Phases(phaseFile);
        JsonArray phases = s["phases"]!.AsArray();
        if (ended && (phases.Count == 0 || !phases[^1]!.GetValue<string>().StartsWith("done", StringComparison.Ordinal))) deviations.Add("ended without reaching done");
        if (ended && target.ExitCode != 0) deviations.Add("exit code not 0");
        File.Delete(phaseFile);

        s["deviations"] = deviations;
        allClean &= deviations.Count == 0;
        Line($"session={s.ToJsonString()}");
    }
    steps.Enter(0, "done", int.MaxValue);
    Line($"result={(allClean ? "clean" : "deviations")}");
    return 0;
}

// ---- the facts of a held target ------------------------------------------------------------------------------------

// What Windows shows of the target and its threads; reads only.
static JsonObject OsState(int pid)
{
    const uint THREAD_QUERY_INFORMATION = 0x0040, THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    const int ThreadQuerySetWin32StartAddress = 9, ThreadSuspendCount = 35;
    var o = new JsonObject();
    try
    {
        using Process p = Process.GetProcessById(pid);
        o["debuggerPresent"] = CheckRemoteDebuggerPresent(p.Handle, out bool present) ? present : $"unreadable: error {Marshal.GetLastPInvokeError()}";
        var modules = new List<(string Name, long Base, long Size)>();
        try { foreach (ProcessModule m in p.Modules) modules.Add((m.ModuleName, (long)m.BaseAddress, m.ModuleMemorySize)); }
        catch (Exception e) { o["modulesError"] = $"{e.GetType().Name}: {e.Message}"; }
        var threads = new JsonArray();
        foreach (ProcessThread t in p.Threads)
        {
            var th = new JsonObject { ["tid"] = t.Id };
            try
            {
                th["state"] = t.ThreadState.ToString();
                if (t.ThreadState == System.Diagnostics.ThreadState.Wait) th["waitReason"] = t.WaitReason.ToString();
                th["cpuMs"] = (long)t.TotalProcessorTime.TotalMilliseconds;
            }
            catch (Exception e) { th["stateError"] = $"{e.GetType().Name}: {e.Message}"; }
            IntPtr h = OpenThread(THREAD_QUERY_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, (uint)t.Id);
            if (h == IntPtr.Zero) th["openError"] = Marshal.GetLastPInvokeError();
            else
            {
                int status = NtQueryThreadSuspendCount(h, ThreadSuspendCount, out uint count, sizeof(uint), IntPtr.Zero);
                th["suspendCount"] = status == 0 ? count : $"NTSTATUS 0x{status:X8}";
                status = NtQueryThreadStartAddress(h, ThreadQuerySetWin32StartAddress, out IntPtr startAddress, IntPtr.Size, IntPtr.Zero);
                if (status != 0) th["start"] = $"NTSTATUS 0x{status:X8}";
                else
                {
                    long address = (long)startAddress;
                    var module = modules.FirstOrDefault(m => address >= m.Base && address < m.Base + m.Size);
                    th["start"] = module.Name is null ? $"0x{address:x}" : $"{module.Name}+0x{address - module.Base:x}";
                }
                if (GetThreadDescription(h, out IntPtr description) >= 0 && description != IntPtr.Zero)
                {
                    string name = Marshal.PtrToStringUni(description) ?? "";
                    if (name.Length > 0) th["name"] = name;
                    LocalFree(description);
                }
                CloseHandle(h);
            }
            threads.Add(th);
        }
        o["threads"] = threads;
    }
    catch (ArgumentException) { o["gone"] = true; }
    return o;
}

// The phases the target's main thread reached, one line each.
static JsonArray Phases(string phaseFile)
{
    var phases = new JsonArray();
    try { foreach (string line in File.ReadAllLines(phaseFile)) if (line.Length > 0) phases.Add(line); }
    catch (IOException) { }
    return phases;
}

// A dump with the managed heap, written by the target's runtime when asked through its diagnostics port, bounded at
// 60 s. Run directly with a pid, createdump.exe 10.0.15 answers "The pid argument is no longer supported" (omega-windows,
// 2026-11-24), so the runtime is asked to run it.
static JsonObject Dump(int pid, string dumpDirectory)
{
    string path = Path.Combine(dumpDirectory, $"probe118-held-{pid}.dmp");
    var d = new JsonObject { ["tool"] = "DiagnosticsClient.WriteDump (WithHeap)", ["path"] = path };
    var clock = Stopwatch.StartNew();
    var write = System.Threading.Tasks.Task.Run(() => new Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient(pid).WriteDump(Microsoft.Diagnostics.NETCore.Client.DumpType.WithHeap, path));
    try
    {
        if (!write.Wait(TimeSpan.FromSeconds(60))) { d["error"] = "no answer within 60 s"; return d; }
        d["ok"] = true;
    }
    catch (AggregateException e) { d["error"] = $"{e.InnerException?.GetType().Name}: {e.InnerException?.Message}"; }
    d["ms"] = clock.ElapsedMilliseconds;
    d["bytes"] = File.Exists(path) ? new FileInfo(path).Length : null;
    return d;
}

// Each managed thread's stack, sampled through EventPipe for 1 s; the capture is bounded at 20 s.
static JsonNode ManagedStacks(int pid)
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try
    {
        ObservationSnapshot snapshot = new StackInspector().CaptureAsync(pid, 1000, "probe 118: where each managed thread of a held target is", cancel.Token).GetAwaiter().GetResult();
        return JsonNode.Parse(snapshot.ToJson())!;
    }
    catch (Exception e) { return JsonValue.Create($"{e.GetType().Name}: {e.Message}")!; }
}

// A fresh attach to the held target, the events until mscordbi goes quiet for 500 ms (at most 5 s), a detach, and
// whether the target then ends within 10 s.
static JsonObject Reattach(Process target)
{
    var r = new JsonObject();
    var sink = new Sink();
    var clock = Stopwatch.StartNew();
    DebugSession session;
    try { session = DebugSession.Attach(target.Id, sink); }
    catch (Exception e) { r["attachError"] = $"{e.GetType().Name}: {e.Message}"; r["attachMs"] = clock.ElapsedMilliseconds; return r; }
    r["attachMs"] = clock.ElapsedMilliseconds;
    clock.Restart();
    while ((sink.Count == 0 || sink.QuietMilliseconds < 500) && clock.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);
    r["events"] = sink.Histogram();
    clock.Restart();
    try { session.Dispose(); }
    catch (Exception e) { r["disposeError"] = $"{e.GetType().Name}: {e.Message}"; }
    r["disposeMs"] = clock.ElapsedMilliseconds;
    r["dropped"] = sink.Dropped();
    r["otherAnomalies"] = sink.OtherAnomalies();
    clock.Restart();
    bool ended = target.WaitForExit(10_000);
    r["endedAfterDetach"] = ended;
    r["endMs"] = ended ? clock.ElapsedMilliseconds : null;
    r["exitCode"] = ended ? target.ExitCode : null;
    return r;
}

static bool TargetAlive(int pid)
{
    try { using Process p = Process.GetProcessById(pid); return !p.HasExited; }
    catch (ArgumentException) { return false; }
}

// ---- support ------------------------------------------------------------------------------------------------------

// The dotnet that runs this probe: <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet.exe.
static string DotnetHostOf(string runtimeDirectory)
{
    string root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
    string candidate = Path.Combine(root, "dotnet.exe");
    return File.Exists(candidate) ? candidate : "dotnet";
}

// A read-only command, bounded at 10 s; its output, or the reason there is none.
static string Run(string file, params string[] arguments)
{
    try
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10_000)) { p.Kill(); return "(no answer in 10 s)"; }
        return stdout.Result.Length > 0 ? stdout.Result : stderr.Result;
    }
    catch (Exception e) { return $"(not run: {e.GetType().Name}: {e.Message})"; }
}

static string? BuildTarget(string host, string csPath)
{
    var psi = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in new[] { "build", csPath, "-c", "Debug" }) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(output); return null; }
    foreach (var line in output.Split('\n'))
    {
        int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow < 0) continue;
        string path = line[(arrow + 3)..].Trim();
        if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path)) return path;
    }
    return null;
}

[DllImport("kernel32", SetLastError = true)] static extern bool CheckRemoteDebuggerPresent(IntPtr process, out bool present);
[DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
[DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
[DllImport("kernel32")] static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
[DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr memory);
[DllImport("ntdll", EntryPoint = "NtQueryInformationThread")] static extern int NtQueryThreadSuspendCount(IntPtr thread, int infoClass, out uint count, int length, IntPtr returnLength);
[DllImport("ntdll", EntryPoint = "NtQueryInformationThread")] static extern int NtQueryThreadStartAddress(IntPtr thread, int infoClass, out IntPtr address, int length, IntPtr returnLength);

// Counts the debugger's events and keeps its anomalies, the callbacks the teardown dropped apart; acts on nothing.
sealed class Sink : IDebugEventSink
{
    private readonly Dictionary<string, int> _events = new();
    private readonly List<string> _dropped = new();
    private readonly List<string> _others = new();
    private long _lastEventTicks = Stopwatch.GetTimestamp();
    public int Count { get { lock (_events) return _events.Values.Sum(); } }
    public int CountOf(string name) { lock (_events) return _events.GetValueOrDefault(name); }
    public void OnEvent(string name) { lock (_events) { _events[name] = _events.GetValueOrDefault(name) + 1; _lastEventTicks = Stopwatch.GetTimestamp(); } }
    public void OnAnomaly(EngineAnomaly a)
    {
        lock (_others)
        {
            if (a.Kind == AnomalyKind.LateCallback && a.Context is { } c && c.TryGetValue("callbackName", out string? name))
                _dropped.Add($"{name}, {a.Observed}");
            else
            {
                string context = a.Context is { Count: > 0 } ctx ? " (" + string.Join(", ", ctx.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")) + ")" : "";
                _others.Add($"{a.Kind} in {a.Operation}: {a.Observed}{context}");
            }
        }
    }
    public long QuietMilliseconds { get { lock (_events) return (long)Stopwatch.GetElapsedTime(_lastEventTicks).TotalMilliseconds; } }
    public JsonObject Histogram() { lock (_events) return new JsonObject(_events.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))); }
    public JsonArray Dropped() { lock (_others) return new JsonArray(_dropped.Select(a => (JsonNode?)a).ToArray()); }
    public JsonArray OtherAnomalies() { lock (_others) return new JsonArray(_others.Select(a => (JsonNode?)a).ToArray()); }
}

// The child's current step and when it began; the watchdog asks whether it has outlasted its bound.
sealed class StepClock
{
    private readonly object _gate = new();
    private int _session;
    private string _step = "";
    private int _boundMs = int.MaxValue;
    private long _since = Stopwatch.GetTimestamp();
    private bool _reported;

    public void Enter(int session, string step, int boundMs)
    {
        lock (_gate) { _session = session; _step = step; _boundMs = boundMs; _since = Stopwatch.GetTimestamp(); _reported = false; }
    }

    public (int Session, string Step, long ElapsedMs)? Overdue()
    {
        lock (_gate)
        {
            long ms = (long)Stopwatch.GetElapsedTime(_since).TotalMilliseconds;
            if (_reported || ms < _boundMs) return null;
            _reported = true;
            return (_session, _step, ms);
        }
    }
}
