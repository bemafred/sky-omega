#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 113 — what a worker per session costs at its start (ADR-016 6.7) ===============================
//
// ADR-016 6.7 decided a worker per session: the debugger runs in a process of its own, started for one session and
// discarded at its end. "Its cost is a start-up per session. That was not measured on its own, and is measured before
// Step 7 is accepted." Probe 106 logged the worker's own start, process start to its "ready" line (13 to 92 ms on four
// machines, finding 106's logs), but not what its first session costs. In a fresh process the engine's code is compiled
// on first use, and dbgshim, mscordbi and the DAC are loaded; a process that has run a session before has done that.
//
// Construction: probe 106's worker (106-worker.cs) and target (106-worker-target.cs), both unchanged. The target runs one
// round: one Break stop, then its natural end. Each run starts one fresh worker and runs K sessions in it, one after the
// other. A session is: launch, wait for the Break stop, read locals, run to the natural end (ProcessExited), close. Every
// phase is timed on this process's clock, from the command written to its answer read, so it includes the NDJSON round
// trip a supervisor sees. The worker is built twice: Debug, which drhook-mcp runs from source, and Release, which the
// installed tool runs. Each run starts a Debug worker and then a Release one, so a change in the machine's load falls on
// both.
//
// Recorded, per configuration:
//   ready     the worker's start: process start to its "ready" line.
//   first     the first session in a fresh worker, per phase.
//   warm      sessions 2..K in the same worker, per phase: a session in a process that has run one before.
//   start-up  ready + first − the median of that worker's warm sessions: what a worker per session adds to each session.
// Run 1 is reported apart, since only it meets cold file caches.
//
// Hypothesis: the start-up is the worker's ready (tens of ms) plus the first session's excess over a warm one, and that
// excess falls on the launch and the first stop, where mscordbi and the DAC load and most of the engine's code is first
// compiled, not on the locals, the natural end or the close. Refuted if a warm session's phases are not steady (the warm
// sessions do not settle), or if the excess falls mostly on the later phases. Its size is recorded, not predicted.
//
// RESULT(0) when every session completed (Break stop, locals, ProcessExited, closed) and every worker ended by itself;
// 1 otherwise; 2 when a build failed.
//
// Usage (from poc/drhook-engine):  dotnet run --no-cache 113-worker-startup.cs [runs] [sessions per worker]
//   dotnet run --no-cache 113-worker-startup.cs 10 5

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;

int runs = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 10;
int sessionsPerWorker = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
if (runs < 2 || sessionsPerWorker < 2) { Console.WriteLine("runs and sessions per worker must each be at least 2"); return 1; }

string? targetDll = Build(Path.GetFullPath("106-worker-target.cs"), "Debug");
string? debugWorker = Build(Path.GetFullPath("106-worker.cs"), "Debug");
string? releaseWorker = Build(Path.GetFullPath("106-worker.cs"), "Release");
if (targetDll is null || debugWorker is null || releaseWorker is null) { Console.WriteLine("FAIL(2): a build failed"); return 2; }
if (debugWorker == releaseWorker) { Console.WriteLine($"FAIL(2): the Debug and Release workers share one output, {debugWorker}"); return 2; }

Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, supervisor {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"worker Debug: {debugWorker}");
Console.WriteLine($"worker Release: {releaseWorker}");
Console.WriteLine($"target: {targetDll}");
Console.WriteLine($"{runs} runs, {sessionsPerWorker} sessions per worker");

string[] configurations = ["Debug", "Release"];
var workers = configurations.ToDictionary(c => c, _ => new List<WorkerTimes>());
bool everyWorkerCompleted = true;
for (int run = 1; run <= runs; run++)
{
    foreach (string configuration in configurations)
    {
        string workerDll = configuration == "Debug" ? debugWorker : releaseWorker;
        WorkerTimes? times = WorkerTimes.Measure(configuration, workerDll, run, sessionsPerWorker, targetDll);
        if (times is null) everyWorkerCompleted = false;
        else workers[configuration].Add(times);
    }
}

string[] phases = ["launch", "break", "locals", "end", "close", "total"];
foreach (string configuration in configurations)
{
    List<WorkerTimes> all = workers[configuration];
    List<WorkerTimes> warmCaches = all.Where(w => w.Run > 1).ToList();
    Console.WriteLine();
    Console.WriteLine($"== {configuration}: {warmCaches.Count} workers after run 1 (median [min–max] ms)");
    Console.WriteLine($"   ready      {Distribution.Spread(warmCaches.Select(w => w.ReadyMs))}");
    Console.WriteLine($"   exit       {Distribution.Spread(warmCaches.Select(w => w.ExitMs))}");
    foreach (string phase in phases)
    {
        string first = Distribution.Spread(warmCaches.Select(w => w.Sessions[0].Phase(phase)));
        string warm = Distribution.Spread(warmCaches.SelectMany(w => w.Sessions.Skip(1)).Select(s => s.Phase(phase)));
        Console.WriteLine($"   {phase,-8}   first {first,-22} warm {warm}");
    }
    Console.WriteLine($"   start-up   {Distribution.Spread(warmCaches.Select(w => w.StartUpMs))}   (ready + first − median of the worker's warm sessions)");
    foreach (WorkerTimes cold in all.Where(w => w.Run == 1))
        Console.WriteLine($"   run 1      ready {cold.ReadyMs:0}, first {cold.Sessions[0].TotalMs:0}, warm median {Distribution.Median(cold.Sessions.Skip(1).Select(s => s.TotalMs)):0}, start-up {cold.StartUpMs:0}");
}

Console.WriteLine();
Console.WriteLine(everyWorkerCompleted
    ? "RESULT(0): every session completed and every worker ended by itself"
    : "RESULT(1): a session did not complete or a worker did not end by itself (see above)");
return everyWorkerCompleted ? 0 : 1;

static string? Build(string csPath, string configuration)
{
    var psi = new ProcessStartInfo("dotnet", $"build \"{csPath}\" -c {configuration}")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(output); return null; }
    foreach (string line in output.Split('\n'))
    {
        int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow < 0) continue;
        string path = line[(arrow + 3)..].Trim();
        if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path) && Path.GetFileNameWithoutExtension(path) == Path.GetFileNameWithoutExtension(csPath)) return path;
    }
    return null;
}

// Measured times, as the summary gives them: the median, and the median with the range.
static class Distribution
{
    public static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return double.NaN;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    public static string Spread(IEnumerable<double> values)
    {
        double[] all = values.ToArray();
        if (all.Length == 0) return "none";
        return $"{Distribution.Median(all):0} [{all.Min():0}–{all.Max():0}]";
    }
}

// One session's phases, each from its command written to its answer read.
sealed record SessionTimes(double LaunchMs, double BreakMs, double LocalsMs, double EndMs, double CloseMs)
{
    public double TotalMs => LaunchMs + BreakMs + LocalsMs + EndMs + CloseMs;

    public double Phase(string name) => name switch
    {
        "launch" => LaunchMs,
        "break" => BreakMs,
        "locals" => LocalsMs,
        "end" => EndMs,
        "close" => CloseMs,
        "total" => TotalMs,
        _ => throw new ArgumentException(name),
    };
}

// One fresh worker: its start, its sessions in order, and its own end.
sealed record WorkerTimes(string Configuration, int Run, double ReadyMs, IReadOnlyList<SessionTimes> Sessions, double ExitMs)
{
    // What a worker per session adds to a session: the worker's start, and its first session's excess over a session in
    // a process that has run one before (this worker's own later sessions).
    public double StartUpMs => ReadyMs + Sessions[0].TotalMs - Distribution.Median(Sessions.Skip(1).Select(s => s.TotalMs));

    // Starts a worker, runs its sessions and ends it. Null when a session or the worker's end did not complete; what
    // went wrong is printed.
    public static WorkerTimes? Measure(string configuration, string workerDll, int run, int sessions, string targetDll)
    {
        string name = $"{configuration} run {run}";
        var clock = Stopwatch.StartNew();
        Worker worker = Worker.Start(workerDll);
        Answer ready = worker.Await("ready", TimeSpan.FromSeconds(30));
        double readyMs = clock.Elapsed.TotalMilliseconds;
        if (ready.Event != "ready") { Console.WriteLine($"{name}: the worker was not ready ({ready}); it is ended"); worker.End(); return null; }
        Console.WriteLine($"{name}: worker {worker.Process.Id} ready in {readyMs:0} ms ({ready.Body?["runtime"]})");

        var measured = new List<SessionTimes>();
        for (int s = 1; s <= sessions; s++)
        {
            string beat = Path.Combine(Path.GetTempPath(), $"probe113-beat-{Environment.ProcessId}-{configuration}-{run}-{s}");
            Answer launch = worker.Exchange(new JsonObject { ["cmd"] = "launch", ["args"] = new JsonArray(targetDll, beat, "1") }, "started", TimeSpan.FromSeconds(30));
            if (launch.Event != "started") { Console.WriteLine($"{name} session {s}: launch answered {launch}; the worker is ended"); worker.End(); return null; }
            int targetPid = (int)launch.Body!["pid"]!;
            Answer stop = worker.Exchange(new JsonObject { ["cmd"] = "wait-break", ["ms"] = 30_000 }, "stop", TimeSpan.FromSeconds(35));
            if (stop.Event != "stop" || stop.Body?["reason"]?.ToString() != "Break") { Console.WriteLine($"{name} session {s}: wait-break answered {stop}; the session is closed"); worker.CloseAndEnd(targetPid); return null; }
            Answer locals = worker.Exchange(new JsonObject { ["cmd"] = "locals" }, "locals", TimeSpan.FromSeconds(30));
            if (locals.Event != "locals" || locals.Body?["values"]?["label"]?.ToString() != "probe106") { Console.WriteLine($"{name} session {s}: locals answered {locals}; the session is closed"); worker.CloseAndEnd(targetPid); return null; }
            Answer end = worker.Exchange(new JsonObject { ["cmd"] = "run-to-end", ["detect"] = false, ["exitedMs"] = 2000 }, "end", TimeSpan.FromSeconds(40));
            if (end.Event != "end" || end.Body?["outcome"]?.ToString() != "exited") { Console.WriteLine($"{name} session {s}: run-to-end answered {end}; the session is closed"); worker.CloseAndEnd(targetPid); return null; }
            Answer closed = worker.Exchange(new JsonObject { ["cmd"] = "close" }, "closed", TimeSpan.FromSeconds(30));
            if (closed.Event != "closed") { Console.WriteLine($"{name} session {s}: close answered {closed}; the worker is ended"); worker.End(); return null; }
            string beatText = File.Exists(beat) ? File.ReadAllText(beat).Trim() : "none";
            File.Delete(beat);
            var times = new SessionTimes(launch.Ms, stop.Ms, locals.Ms, end.Ms, closed.Ms);
            measured.Add(times);
            Console.WriteLine($"  {name} session {s}: launch {times.LaunchMs:0}, break {times.BreakMs:0}, locals {times.LocalsMs:0}, end {times.EndMs:0}, close {times.CloseMs:0}, total {times.TotalMs:0} ms (target {targetPid}, beat '{beatText}')");
        }

        Answer bye = worker.Exchange(new JsonObject { ["cmd"] = "exit" }, "bye", TimeSpan.FromSeconds(5));
        var ending = Stopwatch.StartNew();
        bool endedByItself = bye.Event == "bye" && worker.Process.WaitForExit(10_000);
        double exitMs = bye.Ms + ending.Elapsed.TotalMilliseconds;
        if (!endedByItself) { Console.WriteLine($"{name}: the worker did not end by itself (exit answered {bye}); it is ended"); worker.End(); return null; }
        worker.ReportStderr(name);
        return new WorkerTimes(configuration, run, readyMs, measured, exitMs);
    }
}

// A worker's answer to a command: its event, the whole line, and how long it took.
sealed record Answer(string Event, JsonObject? Body, double Ms)
{
    public override string ToString() => Body?.ToJsonString() ?? Event;
}

// The supervisor's view of a probe 106 worker: NDJSON commands on its stdin, events from its stdout.
sealed class Worker
{
    public Process Process { get; }
    readonly BlockingCollection<JsonObject> _events = new();
    readonly List<string> _stderr = [];

    Worker(Process process) { Process = process; }

    public static Worker Start(string workerDll)
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(workerDll);
        psi.ArgumentList.Add("serve");
        var worker = new Worker(Process.Start(psi)!);
        worker.Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (worker._stderr) worker._stderr.Add(e.Data); };
        worker.Process.BeginErrorReadLine();
        new Thread(() =>
        {
            string? line;
            while ((line = worker.Process.StandardOutput.ReadLine()) is not null)
            {
                JsonObject ev;
                try { ev = JsonNode.Parse(line)!.AsObject(); }
                catch (System.Text.Json.JsonException) { ev = new JsonObject { ["ev"] = "stray", ["text"] = line }; }
                worker._events.Add(ev);
            }
            worker._events.Add(new JsonObject { ["ev"] = "eof" });
        }) { IsBackground = true, Name = "probe113-worker-reader" }.Start();
        return worker;
    }

    // Writes the command and waits for its answer, timed from the write.
    public Answer Exchange(JsonObject command, string expected, TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        try { Process.StandardInput.WriteLine(command.ToJsonString()); Process.StandardInput.Flush(); }
        catch (IOException) { } // the worker is gone; the wait below reads eof
        Answer answer = Await(expected, bound);
        return answer with { Ms = clock.Elapsed.TotalMilliseconds };
    }

    // The next event named `expected`, or an error, debugger-lost or eof event; "timeout" when the bound expires.
    // Anomalies and stray stdout lines on the way are printed: either would make a timing suspect.
    public Answer Await(string expected, TimeSpan bound)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan left = bound - waiting.Elapsed;
            if (left <= TimeSpan.Zero || !_events.TryTake(out JsonObject? ev, left)) return new Answer("timeout", null, waiting.Elapsed.TotalMilliseconds);
            string name = ev["ev"]?.ToString() ?? "";
            if (name is "stray" or "anomaly") Console.WriteLine($"    worker {Process.Id}: {ev.ToJsonString()}");
            if (name == expected || name is "error" or "debugger-lost" or "eof") return new Answer(name, ev, waiting.Elapsed.TotalMilliseconds);
        }
    }

    // A session that went wrong: Dispose ends an owned target (detach, then SIGTERM; ADR-016 6.2), then the worker
    // ends. A target Dispose left running is ended through the OS, after the worker, and that is printed.
    public void CloseAndEnd(int targetPid)
    {
        Answer closed = Exchange(new JsonObject { ["cmd"] = "close" }, "closed", TimeSpan.FromSeconds(30));
        if (closed.Event != "closed") Console.WriteLine($"    close answered {closed}");
        End();
        try
        {
            using Process target = Process.GetProcessById(targetPid);
            if (!target.HasExited && target.ProcessName == "dotnet") { target.Kill(); Console.WriteLine($"    target {targetPid} was still running; killed"); }
        }
        catch (ArgumentException) { } // gone
        catch (InvalidOperationException) { } // gone
    }

    // Ends the worker: by itself if it answers exit, otherwise through the OS.
    public void End()
    {
        Exchange(new JsonObject { ["cmd"] = "exit" }, "bye", TimeSpan.FromSeconds(5));
        if (!Process.WaitForExit(10_000))
        {
            try { Process.Kill(); } catch (InvalidOperationException) { }
            Process.WaitForExit(10_000);
            Console.WriteLine($"    worker {Process.Id} killed");
        }
        ReportStderr($"worker {Process.Id}");
    }

    public void ReportStderr(string name)
    {
        lock (_stderr) foreach (string line in _stderr.Take(5)) Console.WriteLine($"    {name} stderr: {line}");
    }
}
