#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
//
// DrHook.Engine probe 119 — does a detach close one of a Windows target's own handles? (ADR-016 6.6, finding 118) =====
//
// Question: when the debugger detaches from a Windows target, does mscordbi close a handle the target owns?
//
// Why: after a borrowed detach, about 1 in 100 busy Windows targets was held, or crashed with "Internal CLR error"
// (0x80131506), or failed a write to a file it had just opened with "the handle is invalid" (finding 118). Read in
// dotnet/runtime release/10.0, every detach closes one handle value in the target twice:
//   - at attach, mscordbi duplicates its own process handle into the target (SYNCHRONIZE only) and keeps the value in
//     its copy of the debugger control block (LocalEventChannel::Init, RemoteHANDLE::DuplicateToRemoteProcess);
//   - at the detach, the target's runtime closes it (DB_IPCE_DETACH_FROM_PROCESS -> DebuggerRCThread::RightSideDetach
//     -> CloseIPCHandles), before it replies and lets its threads run;
//   - then Detach ends with CordbProcess::Neuter, which deletes the event channel, which closes the same value in the
//     target again from its stale copy (LocalEventChannel::Delete -> RemoteHANDLE::CloseInRemoteProcess:
//     DuplicateHandle with DUPLICATE_CLOSE_SOURCE, which closes the source "regardless of any error status").
// Windows hands a freed handle value out again soon, so a handle the target makes in between can be the one closed.
//
// Hypothesis (H): in a detach, when the target makes new handles at once, one of them has the value of the debugger's
// process handle it held, and it is invalid 300 ms later, closed by no code of the target's. Refuted by sessions in
// which no new handle is invalid, or one with another value is.
//
// Construction: N sessions (--sessions, default 40), each against a fresh target (119-detach-closes-a-handle-target.cs),
// borrowed (DebugSession.Attach). The target finds, while it is debugged, which of its handle values name this
// process (CompareObjectHandles against a handle of its own to it), and says so in a marker file; the probe waits for
// the marker, then disposes the session (synchronize, detach). The moment the target sees the debugger gone it makes 64
// events and records their values; 300 ms later it records which are invalid, and what the debugger's handle value is.
//
// Windows only: Linux and macOS have no such handle (the runtime talks to the debugger over the transport there).
// The probe is self-contained, as each probe here is: its record must not change when another probe's code does.
//
// Output: a report on stdout; with --out <file>, the facts as JSON.
// Result codes: 0 no session lost a handle; 1 a session lost one, a finding about mscordbi, not a fault of the probe;
// 2 the probe could not run (not Windows, bad arguments, the target build failed).
//
// Usage (from poc/drhook-engine):
//   dotnet run --no-cache 119-detach-closes-a-handle-smoke.cs [--sessions N] [--out facts.json]

using System;
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

int sessions = 40;
string? outFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--sessions" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int n) && n > 0) { sessions = n; i++; }
    else if (args[i] == "--out" && i + 1 < args.Length) outFile = Path.GetFullPath(args[++i]);
    else { Console.WriteLine($"FAIL(2): unknown argument '{args[i]}'. Usage: 119-detach-closes-a-handle-smoke.cs [--sessions N] [--out facts.json]"); return 2; }
}
if (!OperatingSystem.IsWindows()) { Console.WriteLine("FAIL(2): probe 119 is Windows only"); return 2; }

string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
string host = Path.Combine(Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..")), "dotnet.exe");
if (!File.Exists(host)) host = "dotnet";
string? dll = BuildTarget(host, Path.GetFullPath("119-detach-closes-a-handle-target.cs"));
if (dll is null) { Console.WriteLine("FAIL(2): could not build 119-detach-closes-a-handle-target.cs"); return 2; }

var environment = new JsonObject
{
    ["machine"] = Environment.MachineName,
    ["os"] = RuntimeInformation.OSDescription,
    ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
    ["cores"] = Environment.ProcessorCount,
    ["probeRuntime"] = RuntimeInformation.FrameworkDescription,
    ["debuggerPid"] = Environment.ProcessId,
    ["sessions"] = sessions,
    ["commit"] = Run("git", "rev-parse", "HEAD").Trim(),
};
Console.WriteLine("environment:");
foreach (var (key, value) in environment) Console.WriteLine($"  {key}: {value?.ToJsonString().Trim('"')}");

var facts = new JsonArray();
int lostTheDebuggersValue = 0, lostAnother = 0, lostNone = 0, unread = 0;
for (int k = 1; k <= sessions; k++)
{
    var s = new JsonObject { ["session"] = k };
    string resultFile = Path.Combine(Path.GetTempPath(), $"probe119-result-{Environment.ProcessId}-{k}.json");
    string scanned = Path.Combine(Path.GetTempPath(), $"probe119-scanned-{Environment.ProcessId}-{k}");
    File.Delete(resultFile);
    File.Delete(scanned);

    var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true };
    foreach (string a in new[] { dll, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), resultFile, scanned }) start.ArgumentList.Add(a);
    using Process target = Process.Start(start)!;
    target.StandardOutput.ReadLine();
    s["pid"] = target.Id;

    var anomalies = new List<string>();
    var sink = new Sink(anomalies);
    var clock = Stopwatch.StartNew();
    try
    {
        using (DebugSession session = DebugSession.Attach(target.Id, sink))
        {
            s["attachMs"] = clock.ElapsedMilliseconds;
            clock.Restart();
            while (!File.Exists(scanned) && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
            s["scannedSeen"] = File.Exists(scanned);
            clock.Restart();
        }
        s["disposeMs"] = clock.ElapsedMilliseconds;
    }
    catch (Exception e) { s["error"] = $"{e.GetType().Name}: {e.Message}"; }
    s["anomalies"] = new JsonArray(anomalies.Select(a => (JsonNode?)a).ToArray());

    bool ended = target.WaitForExit(10_000);
    s["ended"] = ended;
    s["exitCode"] = ended ? target.ExitCode : null;
    if (!ended) { target.Kill(); target.WaitForExit(5000); }

    JsonNode? result = File.Exists(resultFile) ? JsonNode.Parse(File.ReadAllText(resultFile)) : null;
    s["target"] = result;
    File.Delete(resultFile);
    File.Delete(scanned);

    string verdict;
    if (result is null) { unread++; verdict = "no result from the target"; }
    else
    {
        var debuggerValues = result["debuggerHandlesWhileAttached"]!.AsArray().Select(h => h!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var invalid = result["invalidEvents"]!.AsArray().Select(e => e!["handle"]!.GetValue<string>()).ToList();
        if (invalid.Count == 0) { lostNone++; verdict = "no new handle lost"; }
        else if (invalid.Count == 1 && debuggerValues.Contains(invalid[0])) { lostTheDebuggersValue++; verdict = $"lost one new handle, {invalid[0]}, the value of the debugger's handle"; }
        else { lostAnother++; verdict = $"lost {invalid.Count} new handle(s): {string.Join(" ", invalid)}; the debugger's handle was {string.Join(" ", debuggerValues)}"; }
    }
    s["verdict"] = verdict;
    facts.Add(s);
    Console.WriteLine($"  {k,3} pid {s["pid"]}, attach {s["attachMs"]} ms, dispose {s["disposeMs"]} ms, debugger's handle {result?["debuggerHandlesWhileAttached"]?.ToJsonString() ?? "?"}, first new {result?["eventHandles"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "?"}: {verdict}{(anomalies.Count > 0 ? "  anomalies: " + string.Join("; ", anomalies) : "")}");
}

Console.WriteLine("summary:");
Console.WriteLine($"  sessions {sessions}: lost the debugger's value {lostTheDebuggersValue}, lost another {lostAnother}, lost none {lostNone}, no result {unread}");
if (outFile is not null)
{
    var report = new JsonObject { ["probe"] = "119-detach-closes-a-handle", ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["environment"] = environment, ["sessions"] = facts };
    File.WriteAllText(outFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"facts written to {outFile}");
}
bool lost = lostTheDebuggersValue + lostAnother > 0;
Console.WriteLine(lost
    ? "RESULT(1): a detach closed a handle of the target's own (see above); a finding about mscordbi, not a fault of the probe"
    : "RESULT(0): no detach closed a handle of the target's own");
return lost ? 1 : 0;

static string Run(string file, params string[] arguments)
{
    try
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return stdout;
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

// Keeps the debugger's anomalies; acts on nothing.
sealed class Sink(List<string> anomalies) : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnAnomaly(EngineAnomaly a) { lock (anomalies) anomalies.Add($"{a.Kind} in {a.Operation}: {a.Observed}"); }
}
