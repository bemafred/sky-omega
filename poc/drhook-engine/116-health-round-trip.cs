#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:project ../../src/DrHook.Mcp/DrHook.Mcp.csproj
//
// DrHook.Engine probe 116 — how long a debugger worker takes to answer a health request (ADR-016 6.8) ===============
//
// drhook-mcp asks a worker running a request how it is every 5 s, and suspects it when the answer has not come within
// 5 s (Supervisor.HealthBound). ADR-016's design: "That bound is checked against the first measured round trips before
// it is kept." The worker's stdin thread answers at once; this measures how long "at once" is, from drhook-mcp's side
// of the pipe, in the phases a worker is in.
//
// Construction: the probe starts drhook-worker as drhook-mcp does (the dotnet host of this runtime, `exec
// worker/drhook-worker.dll`, from the worker drhook-mcp's build carries) and speaks WorkerProtocol to it directly. A
// health request is sent every 20 ms, 150 per phase, each timed from the write to the answer read:
//   1. idle: no request running;
//   2. a launch: drhook_launch of the snapshot target to its breakpoint, repeated until 150 requests were sent during
//      launches (each launch is ended with drhook_kill, the request after it); each launch must answer `status:
//      launched` with `stoppedReason: breakpoint`, and the launches are counted and timed;
//   3. waiting for the target: drhook_continue with no breakpoint left, while the target runs its loop (about 30 s).
//      Then the worker is asked to exit, the continue still waiting, and the time to its exit is reported.
// Each answer's phase is counted, so the phases are what the worker said they were.
//
// Hypothesis: every round trip is under 500 ms, a tenth of the bound, in every phase. Refuted by a round trip of
// 500 ms or more, or a health request with no answer within 5 s.
//
// RESULT(0) when it holds; 1 when it is refuted; 2 when the worker or the target could not be started.
//
// Usage (from poc/drhook-engine, in a clone that no running drhook-mcp runs from, since it builds src/DrHook.Mcp; the
// snapshot target is built from tests/DrHook.Engine.IntegrationTargets.Snapshot):
//   dotnet run --no-cache 116-health-round-trip.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using SkyOmega.DrHook.Engine.Worker;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const int PerPhase = 150;
TimeSpan Bound = TimeSpan.FromMilliseconds(500);

string root = Path.GetFullPath(Path.Combine("..", ".."));
string targetProject = Path.Combine(root, "tests", "DrHook.Engine.IntegrationTargets.Snapshot", "DrHook.Engine.IntegrationTargets.Snapshot.csproj");
string targetSource = Path.Combine(root, "tests", "DrHook.Engine.IntegrationTargets.Snapshot", "Program.cs");
string? targetDll = BuildTarget(targetProject);
if (targetDll is null) { Console.WriteLine("FAIL(2): the snapshot target did not build"); return 2; }
int line = Array.FindIndex(File.ReadAllLines(targetSource), l => l.Contains("SNAPSHOT_HERE", StringComparison.Ordinal)) + 1;
string dotnet = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
    OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

var start = new ProcessStartInfo(dotnet)
{
    RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
};
start.ArgumentList.Add("exec");
start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "worker", WorkerProtocol.WorkerAssembly));
using Process worker = Process.Start(start)!;
var answers = new BlockingCollection<JsonNode>();
var healthAnswered = new ConcurrentDictionary<long, (long At, string Phase)>();
var reader = new Thread(() =>
{
    string? l;
    while ((l = worker.StandardOutput.ReadLine()) is not null)
    {
        long at = Stopwatch.GetTimestamp();
        JsonNode m = JsonNode.Parse(l)!;
        if (m["health"] is JsonObject h) healthAnswered[(long)m["id"]!] = (at, (string)h["phase"]!);
        else if (m["event"] is null || (string?)m["event"] == "ready") answers.Add(m);
    }
    answers.CompleteAdding();
}) { IsBackground = true };
reader.Start();
object stdin = new();
void Send(string message) { lock (stdin) { worker.StandardInput.Write(message); worker.StandardInput.Write('\n'); worker.StandardInput.Flush(); } }
if (!answers.TryTake(out JsonNode? ready, 30_000)) { Console.WriteLine("FAIL(2): the worker was not ready"); return 2; }
Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"worker: pid {ready["pid"]}, {ready["runtime"]}, protocol {ready["protocol"]}");

long nextId = 1000;
var sent = new List<(long Id, long At, string Measuring)>();
void Health(string measuring)
{
    long id = Interlocked.Increment(ref nextId);
    long at = Stopwatch.GetTimestamp();
    lock (sent) sent.Add((id, at, measuring));
    Send(WorkerProtocol.Health(id));
}
long requestId = 0;
JsonNode Call(string tool, JsonObject args, Action? meanwhile = null)
{
    long id = ++requestId;
    Send(WorkerProtocol.Tool(id, tool, args));
    while (true)
    {
        meanwhile?.Invoke();
        if (answers.TryTake(out JsonNode? a, 20)) return a;
    }
}

// 1. idle
for (int i = 0; i < PerPhase; i++) { Health("idle"); Thread.Sleep(20); }

// 2. launches
int duringLaunch = 0;
var launchMs = new List<double>();
while (duringLaunch < PerPhase)
{
    JsonObject launch = new()
    {
        ["program"] = dotnet, ["args"] = new JsonArray("exec", targetDll), ["cwd"] = null, ["sourceFile"] = targetSource,
        ["line"] = line, ["hypothesis"] = "probe 116", ["env"] = null,
    };
    long launchedAt = Stopwatch.GetTimestamp();
    JsonNode answer = Call("drhook_launch", launch, () => { Health("launch"); duringLaunch++; });
    launchMs.Add(Stopwatch.GetElapsedTime(launchedAt).TotalMilliseconds);
    JsonNode launched = JsonNode.Parse((string)answer["result"]!)!;
    if ((string?)launched["status"] != "launched" || (string?)launched["stoppedReason"] != "breakpoint")
    { Console.WriteLine($"FAIL(2): a launch did not stop at its breakpoint: {answer}"); return 2; }
    Call("drhook_kill", new JsonObject { ["hypothesis"] = "probe 116 ends the launch" });
}

// 3. waiting for the target
JsonObject last = new()
{
    ["program"] = dotnet, ["args"] = new JsonArray("exec", targetDll), ["cwd"] = null, ["sourceFile"] = targetSource,
    ["line"] = line, ["hypothesis"] = "probe 116", ["env"] = null,
};
JsonNode lastLaunched = JsonNode.Parse((string)Call("drhook_launch", last)["result"]!)!;
if ((string?)lastLaunched["status"] != "launched" || (string?)lastLaunched["stoppedReason"] != "breakpoint")
{ Console.WriteLine($"FAIL(2): the last launch did not stop at its breakpoint: {lastLaunched}"); return 2; }
Call("drhook_break_clear", new JsonObject());
long continueId = ++requestId;
Send(WorkerProtocol.Tool(continueId, "drhook_continue", new JsonObject { ["hypothesis"] = "no stop comes", ["waitForBreakpoint"] = true }));
Thread.Sleep(500);
for (int i = 0; i < PerPhase; i++) { Health("waiting"); Thread.Sleep(20); }
// The continue still waits. The worker's end is run after the request running (WorkerLife.End), so it waits for the
// continue's stop, the target's end or the worker's EndBound (30 s), whichever comes first. The time is reported.
long exitAskedAt = Stopwatch.GetTimestamp();
Send(WorkerProtocol.Exit());
bool exited = worker.WaitForExit(60_000);
double exitMs = Stopwatch.GetElapsedTime(exitAskedAt).TotalMilliseconds;
Thread.Sleep(200);

bool holds = true;
Console.WriteLine();
Console.WriteLine($"health round trips, {PerPhase} per phase or more, median [min–max] ms, and the phases the worker named:");
foreach (string measuring in new[] { "idle", "launch", "waiting" })
{
    var trips = new List<double>();
    var named = new Dictionary<string, int>();
    int unanswered = 0;
    foreach ((long id, long at, string m) in sent.Where(x => x.Measuring == measuring))
    {
        if (!healthAnswered.TryGetValue(id, out var answer)) { unanswered++; continue; }
        trips.Add(Stopwatch.GetElapsedTime(at, answer.At).TotalMilliseconds);
        named[answer.Phase] = named.GetValueOrDefault(answer.Phase) + 1;
    }
    trips.Sort();
    double median = trips.Count == 0 ? double.NaN : trips[trips.Count / 2];
    Console.WriteLine($"  {measuring,-8} n={trips.Count,4}  {median:0.00} [{trips.FirstOrDefault():0.00}–{trips.LastOrDefault():0.00}]" +
                      $"  unanswered {unanswered}  phases: {string.Join(", ", named.Select(p => $"{p.Key} {p.Value}"))}");
    if (unanswered > 0 || trips.Count == 0 || trips[^1] >= Bound.TotalMilliseconds) holds = false;
}
launchMs.Sort();
Console.WriteLine($"  launches: {launchMs.Count}, each stopped at its breakpoint, {launchMs[launchMs.Count / 2]:0} ms median [{launchMs[0]:0}–{launchMs[^1]:0}] from the request to its answer");
Console.WriteLine(exited
    ? $"  the worker exited {exitMs:0} ms after its exit request, sent {PerPhase} health requests into the continue's wait"
    : "  the worker had not exited 60 s after its exit request");
Console.WriteLine();
Console.WriteLine(holds
    ? $"RESULT(0): every health request was answered, every round trip under {Bound.TotalMilliseconds:0} ms"
    : $"RESULT(1): a health request went unanswered, or a round trip reached {Bound.TotalMilliseconds:0} ms");
return holds ? 0 : 1;

static string? BuildTarget(string project)
{
    var psi = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Debug") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(output); return null; }
    foreach (string l in output.Split('\n'))
    {
        int arrow = l.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow >= 0 && l.Trim().EndsWith(".dll", StringComparison.Ordinal)) return l[(arrow + 3)..].Trim();
    }
    return null;
}
