#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
//
// DrHook.Engine probe 102 — the natural end (ADR-016 Step 2) ========================================================
//
// Question: when a debugged program ends by itself, or the debugger leaves and the program then ends by itself, does
// the debugger stay usable, session after session in one process?
//
// Why: finding 100's hang follows a forced end. The target dies (killed, or sent SIGTERM by an owned Dispose) while
// mscordbi reads its memory over the debugger transport; the read never returns, and every later attach in the process
// hangs. Step 2's hypothesis: a natural end narrows those races and avoids that failure. Whether it removes every
// dangerous race is open: a program's last thread can call exit() while the debugger dispatches another thread's
// event, so a natural end may still leave a read unanswered.
//
// Read paths (finding 101): mscordbi reads a target's memory directly or over the transport, decided at each attach,
// and only a transport read can wait forever. So each construction runs on both paths where the platform has both, by
// varying only what decides the path there:
//   Linux    the target's ancestry: a descendant is read directly, a reparented target over the transport;
//   macOS    the launcher: on the GitHub runners a dotnet-hosted target is read directly and one started through its
//            ad hoc signed apphost over the transport; on omega both go over the transport, so there the pair is a
//            control;
//   Windows  one path (no transport).
// Every session records the path it got (the call mscordbi makes, from the same process, as probe 101).
//
// Constructions, each run N times in one child process, which then attaches once more, to an idle target; that last
// attach must succeed:
//   quiet            the target floods thread events for 1.5 s, stops, and ends 300 ms later; the debugger waits for
//                    the exit, then disposes. An end with nothing in flight: the control.
//   mid-flood        the target ends while a background thread still floods; the debugger waits for the exit, then
//                    disposes. The natural end nearest to finding 100.
//   detach-then-end  the debugger disposes (borrowed: synchronize, then detach) at a random moment in the flood; the
//                    target floods 1 s more and ends by itself.
//   exit-at-detach   as detach-then-end, but the target ends as soon as the debugger has left (finding 63's case,
//                    without the kill).
// The flood is probe 100's: one empty thread started and joined after another, so the debugger is always dispatching
// CreateThread and ExitThread events and reading the target's memory to do it. Every session is borrowed
// (DebugSession.Attach): DrHook never ends the target, and the child signals, stops or kills nothing.
//
// Recorded per session: the read path; the attach, exit and Dispose times; the debugger events; anomalies, each with
// its context (for a late callback, which event arrived); whether the target ended by itself and wrote its marker;
// the debug pipes left once the target is gone (finding 100's orphaned read leaves them), which the child then
// removes; the child's thread count; on Linux, the debugger's transport threads (".NET DebugPipe") beyond those
// present before the first session, with their wchan and syscall.
//
// Bounds: a step of the child that does not finish within its bound (30 s for a debugger call) is reported by a
// watchdog thread in the child, on Linux with each thread's comm, wchan and syscall (finding 101; no outside sampler,
// ADR-016 Step 0). The parent then ends the child: a call blocked inside mscordbi cannot be cancelled, and discarding
// the process is the only recovery the debugging API supports (ADR-016). It ends only the child, then waits for that
// session's target to end by itself (within 60 s of starting, unless it is held stopped) and records whether it did;
// a target still alive after 75 s is ended, if it is still the target's executable, and the record says so.
//
// N (--sessions, default 20): a construction that left a session stuck with probability p would show it in 20
// sessions with probability 1-(1-p)^20: 0.88 for p = 0.1, 0.99 for p = 0.2.
//
// The probe is self-contained, as each probe here is: its record must not change when another probe's code does.
//
// Output: a report on stdout; with --out <file>, the facts as JSON.
// Result codes: 0 every session ended naturally (no step stuck, every target ended by itself and wrote its marker,
// every exit seen, every Dispose returned, no anomaly, no pipes left, every final attach succeeded); 1 otherwise,
// which is a finding about DrHook or mscordbi, not a fault of the probe; 2 the probe could not run (bad arguments,
// target build failed or left no apphost).
//
// Usage (from poc/drhook-engine):
//   dotnet run --no-cache 102-natural-end-smoke.cs [--sessions N] [--out facts.json]

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

if (args.Length == 8 && args[0] == "child")
    return Child(args[1], args[2], args[3], args[4], args[5], int.Parse(args[6], CultureInfo.InvariantCulture), int.Parse(args[7], CultureInfo.InvariantCulture));

int sessions = 20;
string? outFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--sessions" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int n) && n > 0) { sessions = n; i++; }
    else if (args[i] == "--out" && i + 1 < args.Length) outFile = Path.GetFullPath(args[++i]);
    else { Console.WriteLine($"FAIL(2): unknown argument '{args[i]}'. Usage: 102-natural-end-smoke.cs [--sessions N] [--out facts.json]"); return 2; }
}

string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
string host = DotnetHostOf(runtimeDirectory);
string? dll = BuildTarget(host, Path.GetFullPath("102-natural-end-target.cs"));
if (dll is null) { Console.WriteLine("FAIL(2): could not build 102-natural-end-target.cs"); return 2; }
string appHost = AppHostOf(dll);
if (!File.Exists(appHost)) { Console.WriteLine($"FAIL(2): the target build left no apphost at {appHost}"); return 2; }

JsonObject environment = EnvironmentFacts(host, runtimeDirectory, appHost, sessions);
Console.WriteLine("environment:");
foreach (var (key, value) in environment) Console.WriteLine($"  {key}: {value?.ToJsonString().Trim('"')}");

(string Launcher, string Mode)[] paths = OperatingSystem.IsWindows() ? new[] { ("dotnet", "started") }
    : OperatingSystem.IsMacOS() ? new[] { ("dotnet", "descendant"), ("apphost", "descendant") }
    : new[] { ("dotnet", "descendant"), ("dotnet", "reparented") };
string[] constructions = { "quiet", "mid-flood", "detach-then-end", "exit-at-detach" };

var children = new JsonArray();
var summary = new List<string>();
bool allNatural = true;
int seed = 102_000;
foreach (string construction in constructions)
foreach (var (launcher, mode) in paths)
{
    seed++;
    Console.WriteLine($"{construction}, {launcher} {mode} (seed {seed}):");
    JsonObject record = RunChild(construction, launcher, mode, host, dll, appHost, sessions, seed);
    string outcome = record["outcome"]!.GetValue<string>();
    int natural = record["sessions"]!.AsArray().Count(s => s!["natural"]!.GetValue<bool>());
    int recorded = record["sessions"]!.AsArray().Count;
    string reads = string.Join("/", record["sessions"]!.AsArray().Select(s => s!["reads"]?.GetValue<string>() ?? "?").Distinct());
    summary.Add($"  {construction,-16} {launcher + " " + mode,-20} reads {reads,-10} natural {natural}/{sessions} (recorded {recorded}), final attach {(record["final"]?["ok"]?.GetValue<bool>() == true ? "ok" : "NOT ok")}: {outcome}");
    Console.WriteLine($"  outcome: {outcome}");
    allNatural &= outcome == "natural";
    children.Add(record);
}

Console.WriteLine("summary:");
foreach (string line in summary) Console.WriteLine(line);
if (outFile is not null)
{
    var report = new JsonObject { ["probe"] = "102-natural-end", ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["environment"] = environment, ["children"] = children };
    File.WriteAllText(outFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"facts written to {outFile}");
}
Console.WriteLine(allNatural
    ? "RESULT(0): every session ended naturally, and every final attach succeeded"
    : "RESULT(1): a session did not end naturally (see above); a finding, not a fault of the probe");
return allNatural ? 0 : 1;

// ---- the parent: one child per construction and read path --------------------------------------------------------

static JsonObject RunChild(string construction, string launcher, string mode, string host, string dll, string appHost, int sessions, int seed)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { "child", construction, launcher, mode, host, dll, sessions.ToString(CultureInfo.InvariantCulture), seed.ToString(CultureInfo.InvariantCulture) })
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

    var record = new JsonObject { ["construction"] = construction, ["launcher"] = launcher, ["mode"] = mode, ["seed"] = seed };
    var sessionFacts = new JsonArray();
    var parentActions = new JsonArray();
    var targets = new List<(int Pid, Stopwatch Since)>();
    var endedPids = new HashSet<int>(); // targets the child saw end; a later process may reuse their PIDs
    string outcome = "child-ended-without-result";
    string lastStage = "";
    while (true)
    {
        // The longest silent step of the child is waiting up to 75 s for a target to end.
        if (!lines.TryTake(out string? line, TimeSpan.FromSeconds(90))) { outcome = "child-silent"; parentActions.Add($"no output from the child for 90 s after '{lastStage}'"); break; }
        if (line is null) break;
        if (line.StartsWith("child: stage=", StringComparison.Ordinal)) { lastStage = line["child: stage=".Length..]; continue; }
        if (line.StartsWith("child: target=", StringComparison.Ordinal)) { targets.Add((int.Parse(line["child: target=".Length..], CultureInfo.InvariantCulture), Stopwatch.StartNew())); continue; }
        if (line.StartsWith("child: session=", StringComparison.Ordinal))
        {
            JsonNode s = JsonNode.Parse(line["child: session=".Length..])!;
            sessionFacts.Add(s);
            if (s["targetEnded"]?.GetValue<bool>() == true) endedPids.Add(s["pid"]!.GetValue<int>());
            Console.WriteLine($"    {s["session"],3} reads {s["reads"]?.GetValue<string>(),-9} attach {s["attachMs"],5} ms, exit seen {s["exitSeenMs"]?.ToJsonString() ?? "-",5}, dispose {s["disposeMs"],5} ms, ended {s["targetEnded"]}, marker {s["marker"]}, pipes left {s["pipesLeft"]?.AsArray().Count}, threads {s["ownThreads"]}{(s["natural"]!.GetValue<bool>() ? "" : "  NOT natural: " + s["deviations"]!.ToJsonString())}");
            continue;
        }
        if (line.StartsWith("child: stuck=", StringComparison.Ordinal))
        {
            record["stuck"] = JsonNode.Parse(line["child: stuck=".Length..]);
            outcome = "stuck";
            Console.WriteLine($"    STUCK: {line["child: stuck=".Length..]}");
            break;
        }
        if (line.StartsWith("child: final=", StringComparison.Ordinal))
        {
            record["final"] = JsonNode.Parse(line["child: final=".Length..]);
            if (record["final"]!["targetEnded"]?.GetValue<bool>() == true) endedPids.Add(record["final"]!["pid"]!.GetValue<int>());
            Console.WriteLine($"    final attach: {line["child: final=".Length..]}");
            continue;
        }
        if (line.StartsWith("child: result=", StringComparison.Ordinal)) { outcome = line["child: result=".Length..].Trim(); break; }
    }

    if (!child.WaitForExit(outcome is "stuck" or "child-silent" ? 2000 : 10_000))
    {
        // Only the child: a descendant target would die with a process-tree kill, and its own end is what is recorded.
        child.Kill();
        child.WaitForExit(5000);
        parentActions.Add($"ended the child ({outcome}, at '{lastStage}'): a call blocked inside mscordbi cannot be cancelled");
    }

    // Any target still alive is given its own end first: it returns within 60 s of starting unless it is held stopped.
    string expectedName = launcher == "dotnet" ? "dotnet" : Path.GetFileNameWithoutExtension(appHost);
    foreach (var (pid, since) in targets)
    {
        if (endedPids.Contains(pid) || !TargetAlive(pid)) continue;
        while (TargetAlive(pid) && since.Elapsed < TimeSpan.FromSeconds(75)) Thread.Sleep(200);
        if (!TargetAlive(pid)) { parentActions.Add($"target {pid} ended by itself {since.ElapsedMilliseconds} ms after it started, after the child was gone"); continue; }
        try
        {
            using Process leftover = Process.GetProcessById(pid);
            if (leftover.ProcessName.Length > 0 && expectedName.StartsWith(leftover.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                leftover.Kill();
                parentActions.Add($"target {pid} still alive 75 s after it started (held stopped?): ended by the parent");
            }
        }
        catch (ArgumentException) { }
    }

    record["sessions"] = sessionFacts;
    record["outcome"] = outcome;
    record["parentActions"] = parentActions;
    foreach (var action in parentActions) Console.WriteLine($"    parent: {action}");
    if (outcome != "natural")
        lock (errors) record["stderr"] = new JsonArray(errors.Select(e => (JsonNode?)e).ToArray());
    return record;
}

// ---- the child: N sessions in one process, then a final attach ----------------------------------------------------

static int Child(string construction, string launcher, string mode, string host, string dll, int sessions, int seed)
{
    var clock = Stopwatch.StartNew();
    var random = new Random(seed);
    var steps = new StepClock();
    void Line(string text) { lock (steps) { Console.WriteLine($"child: {text}"); Console.Out.Flush(); } }
    void Step(int session, string name, int boundMs) { steps.Enter(session, name, boundMs); Line($"stage={session} {name}"); }

    // The watchdog reports a step past its bound once; the parent then ends this process.
    var watchdog = new Thread(() =>
    {
        while (true)
        {
            Thread.Sleep(500);
            if (steps.Overdue() is not { } overdue) continue;
            var stuck = new JsonObject
            {
                ["session"] = overdue.Session,
                ["step"] = overdue.Step,
                ["elapsedMs"] = overdue.ElapsedMs,
                ["ownThreads"] = ThreadCount(),
                ["threads"] = OwnThreads(),
            };
            Line($"stuck={stuck.ToJsonString()}");
        }
    }) { IsBackground = true };
    watchdog.Start();

    string[] command = launcher == "dotnet" ? new[] { host, dll } : new[] { AppHostOf(dll) };
    string? dotnetRoot = launcher == "apphost" ? DotnetRootOf(host) : null;
    int baselineTransportThreads = DebugPipeThreads().Count;
    bool allNatural = true;

    for (int k = 1; k <= sessions; k++)
    {
        var s = new JsonObject { ["session"] = k };
        var deviations = new JsonArray();
        string marker = Path.Combine(Path.GetTempPath(), $"probe102-done-{Environment.ProcessId}-{k}");
        File.Delete(marker);

        Step(k, "start-target", 30_000);
        (int pid, Process? waitable) = StartTarget(mode, command, dotnetRoot, new[] { construction, marker });
        Line($"target={pid}");
        s["pid"] = pid;

        var sink = new Sink();
        Step(k, "attach", 30_000);
        long t = clock.ElapsedMilliseconds;
        DebugSession? session = null;
        try { session = DebugSession.Attach(pid, sink); }
        catch (Exception e) { s["attachError"] = $"{e.GetType().Name}: {e.Message}"; deviations.Add("attach failed"); }
        s["attachMs"] = clock.ElapsedMilliseconds - t;

        if (session is not null)
        {
            JsonObject memoryOpen = MemoryOpen(pid);
            s["reads"] = memoryOpen["reads"]!.GetValue<string>();
            s["memoryOpen"] = memoryOpen;

            if (construction is "quiet" or "mid-flood")
            {
                Step(k, "wait-exit", 40_000);
                var waited = Stopwatch.StartNew();
                var otherStops = new JsonArray();
                bool exitSeen = false;
                while (waited.Elapsed < TimeSpan.FromSeconds(30))
                {
                    StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(30) - waited.Elapsed);
                    if (stop is null) break;
                    if (stop.Reason == StopReason.ProcessExited) { exitSeen = true; break; }
                    otherStops.Add(stop.Reason.ToString());
                    session.Resume();
                }
                s["exitSeenMs"] = exitSeen ? waited.ElapsedMilliseconds : null;
                s["otherStops"] = otherStops;
                if (!exitSeen) deviations.Add("the exit was not seen within 30 s");
                if (otherStops.Count > 0) deviations.Add("stops other than the exit");
            }
            else
            {
                Step(k, "wait-flood", 40_000);
                var waited = Stopwatch.StartNew();
                while (sink.CountOf("CreateThread") < 10 && waited.Elapsed < TimeSpan.FromSeconds(30))
                    Thread.Sleep(5);
                bool floodSeen = sink.CountOf("CreateThread") >= 10;
                int delay = random.Next(0, 500);
                Thread.Sleep(delay);
                s["floodSeenMs"] = floodSeen ? waited.ElapsedMilliseconds - delay : null;
                s["detachDelayMs"] = delay;
                if (!floodSeen) deviations.Add("no flood within 30 s");
            }

            Step(k, "dispose", 30_000);
            t = clock.ElapsedMilliseconds;
            try { session.Dispose(); }
            catch (Exception e) { s["disposeError"] = $"{e.GetType().Name}: {e.Message}"; deviations.Add("Dispose threw"); }
            s["disposeMs"] = clock.ElapsedMilliseconds - t;
        }

        // The target's own end: it writes the marker just before it returns.
        Step(k, "wait-end", 75_000);
        var ending = Stopwatch.StartNew();
        bool ended = false;
        while (ending.Elapsed < TimeSpan.FromSeconds(70))
        {
            if (waitable is not null ? waitable.HasExited : !TargetAlive(pid)) { ended = true; break; }
            Thread.Sleep(20);
        }
        s["targetEnded"] = ended;
        s["endWaitMs"] = ending.ElapsedMilliseconds;
        s["exitCode"] = ended && waitable is not null ? waitable.ExitCode : null;
        s["marker"] = File.Exists(marker);
        File.Delete(marker);
        if (!ended) deviations.Add("the target did not end within 70 s");
        else if (!s["marker"]!.GetValue<bool>()) deviations.Add("the target ended without writing its marker");

        // What the ended session left behind. Pipes of a target that is gone are removed after they are recorded.
        Step(k, "after-end", 30_000);
        Thread.Sleep(300); // lets the debugger's and the system's handling of the target's end finish
        JsonArray pipes = DebugPipes(pid);
        s["pipesLeft"] = pipes;
        if (ended)
            foreach (JsonNode? p in pipes)
                try { File.Delete(Path.Combine(Path.GetTempPath(), p!.GetValue<string>())); } catch (IOException) { }
        if (pipes.Count > 0) deviations.Add("debug pipes left after the target ended");
        s["events"] = sink.Histogram();
        JsonArray anomalies = sink.Anomalies();
        s["anomalies"] = anomalies;
        if (anomalies.Count > 0) deviations.Add("anomalies");
        s["ownThreads"] = ThreadCount();
        if (OperatingSystem.IsLinux())
        {
            JsonArray transport = DebugPipeThreads();
            s["transportThreadsBeyondBaseline"] = transport.Count - baselineTransportThreads;
            s["transportThreads"] = transport;
        }

        bool natural = deviations.Count == 0;
        s["natural"] = natural;
        s["deviations"] = deviations;
        allNatural &= natural;
        Line($"session={s.ToJsonString()}");
    }

    // The final attach: an idle target, after every session above. It must succeed.
    var f = new JsonObject();
    string finalMarker = Path.Combine(Path.GetTempPath(), $"probe102-done-{Environment.ProcessId}-final");
    File.Delete(finalMarker);
    Step(0, "final-start-target", 30_000);
    (int fpid, Process? fwait) = StartTarget(mode == "started" ? "started" : "descendant", new[] { host, dll }, null, new[] { "idle", finalMarker });
    Line($"target={fpid}");
    f["pid"] = fpid;
    var fsink = new Sink();
    Step(0, "final-attach", 30_000);
    long ft = clock.ElapsedMilliseconds;
    bool finalAttached = false;
    try
    {
        DebugSession fs = DebugSession.Attach(fpid, fsink);
        f["attachMs"] = clock.ElapsedMilliseconds - ft;
        long attachedAt = clock.ElapsedMilliseconds;
        while ((fsink.Count == 0 || fsink.QuietMilliseconds < 500) && clock.ElapsedMilliseconds - attachedAt < 10_000)
            Thread.Sleep(50);
        f["events"] = fsink.Count;
        Step(0, "final-dispose", 30_000);
        fs.Dispose();
        finalAttached = true;
    }
    catch (Exception e) { f["error"] = $"{e.GetType().Name}: {e.Message}"; }
    Step(0, "final-wait-end", 75_000);
    var fending = Stopwatch.StartNew();
    bool fended = false;
    while (fending.Elapsed < TimeSpan.FromSeconds(70))
    {
        if (fwait is not null ? fwait.HasExited : !TargetAlive(fpid)) { fended = true; break; }
        Thread.Sleep(20);
    }
    bool fmarker = File.Exists(finalMarker);
    File.Delete(finalMarker);
    f["targetEnded"] = fended;
    f["marker"] = fmarker;
    f["anomalies"] = fsink.Anomalies();
    bool finalOk = finalAttached && fended && fmarker && fsink.Anomalies().Count == 0;
    f["ok"] = finalOk;
    Line($"final={f.ToJsonString()}");
    steps.Enter(0, "done", int.MaxValue);
    Line($"result={(allNatural && finalOk ? "natural" : "deviations")}");
    return 0;
}

// Descendant: bash starts the target and waits for it, so it is bash's child and this process's grandchild (finding
// 87: not a direct child of the debugger process). Reparented: bash starts it and exits at once, so it is handed to
// init or the nearest subreaper. Windows: a direct child; its first line shows its runtime is up (on Unix the attach
// itself waits for the target's debugger thread, finding 99). For the apphost, DOTNET_ROOT points it at the probe's
// own runtime, so the debugger never mixes runtime versions (finding 86).
static (int Pid, Process? Waitable) StartTarget(string mode, string[] command, string? dotnetRoot, string[] targetArgs)
{
    string[] arguments = command.Skip(1).Concat(targetArgs).ToArray();
    if (OperatingSystem.IsWindows())
    {
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (string a in arguments) start.ArgumentList.Add(a);
        if (dotnetRoot is not null) start.Environment["DOTNET_ROOT"] = dotnetRoot;
        Process target = Process.Start(start)!;
        target.StandardOutput.ReadLine();
        return (target.Id, target);
    }
    var psi = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add(mode == "descendant"
        ? "\"$0\" \"$@\" >/dev/null 2>&1 </dev/null & echo $!; wait $!"
        : "\"$0\" \"$@\" >/dev/null 2>&1 </dev/null & echo $!");
    psi.ArgumentList.Add(command[0]);
    foreach (string a in arguments) psi.ArgumentList.Add(a);
    if (dotnetRoot is not null) psi.Environment["DOTNET_ROOT"] = dotnetRoot;
    Process shell = Process.Start(psi)!;
    int pid = int.Parse(shell.StandardOutput.ReadLine()!.Trim(), CultureInfo.InvariantCulture);
    if (mode == "reparented") shell.WaitForExit();
    return (pid, mode == "descendant" ? shell : null);
}

// ---- the facts ----------------------------------------------------------------------------------------------------

// The call mscordbi makes when it attaches (PAL_OpenProcessMemory), from the same process; what it opens is released.
static JsonObject MemoryOpen(int pid)
{
    if (OperatingSystem.IsMacOS())
    {
        uint self = MachTaskSelf();
        int kr = task_for_pid(self, pid, out uint port);
        if (kr == 0) mach_port_deallocate(self, port);
        return new JsonObject { ["call"] = "task_for_pid", ["kern_return"] = kr, ["reads"] = kr == 0 ? "direct" : "transport" };
    }
    if (OperatingSystem.IsLinux())
    {
        int fd = open($"/proc/{pid}/mem", 0);
        int errno = fd == -1 ? Marshal.GetLastPInvokeError() : 0;
        if (fd != -1) close(fd);
        return new JsonObject { ["call"] = "open(/proc/<pid>/mem, O_RDONLY)", ["errno"] = errno, ["reads"] = fd != -1 ? "direct" : "transport" };
    }
    return new JsonObject { ["call"] = "none", ["reads"] = "direct" }; // Windows has no debugger transport
}

// The runtime names its debug transport pipes clr-debug-pipe-<pid>-<key>-in/out and its diagnostic socket
// dotnet-diagnostic-<pid>-<key>-socket, in its temp directory.
static JsonArray DebugPipes(int pid)
{
    var names = new JsonArray();
    if (OperatingSystem.IsWindows()) return names;
    foreach (string entry in Directory.EnumerateFileSystemEntries(Path.GetTempPath())
        .Where(e => Path.GetFileName(e).StartsWith($"clr-debug-pipe-{pid}-", StringComparison.Ordinal) || Path.GetFileName(e).StartsWith($"dotnet-diagnostic-{pid}-", StringComparison.Ordinal))
        .OrderBy(e => e, StringComparer.Ordinal))
        names.Add(Path.GetFileName(entry));
    return names;
}

static int ThreadCount()
{
    using Process self = Process.GetCurrentProcess();
    return self.Threads.Count;
}

// Linux: this process's threads named ".NET DebugPipe" (mscordbi's transport threads, and one of the probe's own
// runtime, finding 101), with what they wait in.
static JsonArray DebugPipeThreads()
{
    var threads = new JsonArray();
    if (!OperatingSystem.IsLinux()) return threads;
    foreach (JsonNode? thread in OwnThreads())
        if (thread!["comm"]?.GetValue<string>() == ".NET DebugPipe")
            threads.Add(new JsonObject { ["tid"] = thread["tid"]!.GetValue<int>(), ["wchan"] = thread["wchan"]?.GetValue<string>(), ["syscall"] = thread["syscall"]?.GetValue<string>() });
    return threads;
}

// Linux: what this process can read of each of its own threads without ptrace rights (finding 101).
static JsonArray OwnThreads()
{
    var threads = new JsonArray();
    if (!OperatingSystem.IsLinux()) return threads;
    foreach (string task in Directory.EnumerateDirectories("/proc/self/task"))
    {
        var thread = new JsonObject { ["tid"] = int.Parse(Path.GetFileName(task), CultureInfo.InvariantCulture) };
        foreach (string file in new[] { "comm", "wchan", "syscall" })
        {
            try
            {
                string text = File.ReadAllText(Path.Combine(task, file)).Trim();
                thread[file] = file == "syscall" ? text.Split(' ')[0] : text;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { thread[file] = $"unreadable: {e.GetType().Name}"; }
        }
        threads.Add(thread);
    }
    return threads;
}

static bool TargetAlive(int pid)
{
    if (!OperatingSystem.IsWindows()) return kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != 3; // 3 = ESRCH
    try { using Process p = Process.GetProcessById(pid); return !p.HasExited; }
    catch (ArgumentException) { return false; }
}

// ---- the environment, recorded once by the parent -----------------------------------------------------------------

static JsonObject EnvironmentFacts(string host, string runtimeDirectory, string appHost, int sessions)
{
    var e = new JsonObject
    {
        ["machine"] = Environment.MachineName,
        ["os"] = RuntimeInformation.OSDescription,
        ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
        ["cores"] = Environment.ProcessorCount,
        ["probeRuntime"] = RuntimeInformation.FrameworkDescription,
        ["runtimeDirectory"] = runtimeDirectory,
        ["dotnetHost"] = host,
        ["debuggerExecutable"] = Environment.ProcessPath, // the children, which attach, run this same file
        ["targetAppHost"] = appHost,
        ["sessionsPerChild"] = sessions,
        ["tempDirectory"] = Path.GetTempPath(),
        ["commit"] = Run("git", "rev-parse", "HEAD").Trim(),
        ["ci"] = string.Join(' ', new[] { "GITHUB_ACTIONS", "RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion" }
            .Select(v => (v, Environment.GetEnvironmentVariable(v))).Where(p => p.Item2 is not null).Select(p => $"{p.v}={p.Item2}")),
    };
    if (OperatingSystem.IsMacOS())
    {
        e["macOS"] = Run("sw_vers", "-productVersion").Trim();
        e["systemIntegrityProtection"] = Run("csrutil", "status").Trim();
    }
    if (OperatingSystem.IsLinux())
    {
        e["kernel"] = Run("uname", "-r").Trim();
        e["distribution"] = File.Exists("/etc/os-release") ? File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) : null;
        e["ptraceScope"] = File.Exists("/proc/sys/kernel/yama/ptrace_scope") ? File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim() : "no Yama";
    }
    return e;
}

// ---- support ------------------------------------------------------------------------------------------------------

// The dotnet that runs this probe: <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet.
static string DotnetHostOf(string runtimeDirectory)
{
    string root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
    string candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    return File.Exists(candidate) ? candidate : "dotnet";
}

// The dotnet root the host lives in, or null when the host was not found by path.
static string? DotnetRootOf(string host) => Path.IsPathRooted(host) ? Path.GetDirectoryName(host) : null;

// The SDK puts a program's apphost beside its dll: the same name without .dll, with .exe on Windows.
static string AppHostOf(string dll) => Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null);

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

static uint MachTaskSelf()
{
    IntPtr libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
    return (uint)Marshal.ReadInt32(NativeLibrary.GetExport(libSystem, "mach_task_self_"));
}

[DllImport("/usr/lib/libSystem.B.dylib")] static extern int task_for_pid(uint targetTport, int pid, out uint task);
[DllImport("/usr/lib/libSystem.B.dylib")] static extern int mach_port_deallocate(uint task, uint name);
[DllImport("libc", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
[DllImport("libc")] static extern int close(int fd);
[DllImport("libc", SetLastError = true)] static extern int kill(int pid, int sig);

// Counts the debugger's events and keeps its anomalies; nothing here acts on the target.
sealed class Sink : IDebugEventSink
{
    private readonly Dictionary<string, int> _events = new();
    private readonly List<string> _anomalies = new();
    private long _lastEventTicks = Stopwatch.GetTimestamp();
    public int Count { get { lock (_events) return _events.Values.Sum(); } }
    public int CountOf(string name) { lock (_events) return _events.GetValueOrDefault(name); }
    public void OnEvent(string name) { lock (_events) { _events[name] = _events.GetValueOrDefault(name) + 1; _lastEventTicks = Stopwatch.GetTimestamp(); } }
    public void OnAnomaly(EngineAnomaly a)
    {
        string context = a.Context is { Count: > 0 } c ? " (" + string.Join(", ", c.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")) + ")" : "";
        lock (_anomalies) _anomalies.Add($"{a.Kind} in {a.Operation}: {a.Observed}{context}");
    }
    public long QuietMilliseconds { get { lock (_events) return (long)Stopwatch.GetElapsedTime(_lastEventTicks).TotalMilliseconds; } }
    public JsonObject Histogram() { lock (_events) return new JsonObject(_events.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))); }
    public JsonArray Anomalies() { lock (_anomalies) return new JsonArray(_anomalies.Select(a => (JsonNode?)a).ToArray()); }
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
