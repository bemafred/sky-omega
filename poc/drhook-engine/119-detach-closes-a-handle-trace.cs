#!/usr/bin/env -S dotnet
#:package Microsoft.Diagnostics.Tracing.TraceEvent@4.0.0
#:property TargetFramework=net10.0
#:property PublishAot=false
//
// DrHook.Engine probe 119, traced — which code makes the second close of a target's handle at a detach? (finding 119) =====
//
// Question: probe 119 showed that a Windows target, at a borrowed detach, loses a new handle that has the value of the
// debugger's process handle it held (30 of 40, finding 119). Read in dotnet/runtime release/10.0, two closes of that value
// are made: the target's runtime closes it (DebuggerRCThread::RightSideDetach -> CloseIPCHandles -> RemoteHANDLE::Close),
// and then mscordbi closes it again from its own copy of the value (CordbProcess::Neuter -> LocalEventChannel::Delete ->
// RemoteHANDLE::CloseInRemoteProcess: DuplicateHandle out of the target, DUPLICATE_CLOSE_SOURCE). The second close is read
// in the source; probe 119 does not see who makes it. Which process, thread and code does?
//
// Hypothesis (H): in every session in which the target lost a new handle, the events on the debugger's handle value in the
// target's handle table are, in this order:
//   1. a thread of the debugger's process duplicates a handle into the target, with that value (the attach);
//   2. a thread of the target closes that value, the object being the one of step 1 (the target's runtime, the first close);
//   3. a thread of the target creates a handle with that value again (the handle it then loses);
//   4. a thread of the debugger's process duplicates that value out of the target, the object being the one of step 3,
//      with a frame of mscordbi.dll in its stack (the second close).
// Refuted by a lost handle without all four steps, by a step 4 made by a thread of another process or without an
// mscordbi.dll frame, or by a close of the step-3 handle by a thread of the target.
//
// Construction: builds probe 119 and its target first, so that the trace holds no build; starts wpr's built-in Handle
// profile in file mode (every handle create, close and duplicate in the system, each with its stack; image loads;
// processes and threads); runs probe 119 (119-detach-closes-a-handle-smoke.cs, --sessions N) as a child process; stops the
// trace; reads it with TraceEvent's TraceLog. For each session it takes the handle events on the debugger's handle value
// in that target's handle table during the target's life, with their stacks, and function names where Microsoft's symbol
// server has the module's symbols.
//
// Windows only, from an elevated prompt (wpr). The trace, probe 119's own facts and the symbols stay in the directory of
// the --out file (the current directory without --out).
// Output: a report on stdout; with --out <file>, the facts as JSON.
// Result codes: 0 H holds in every session that lost a handle; 1 H is refuted in a session; 2 the probe could not run, or
// no session lost a handle (H untested).
//
// Usage (from poc/drhook-engine, elevated):
//   dotnet run --no-cache 119-detach-closes-a-handle-trace.cs [--sessions N] [--out facts.json]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

int sessions = 20;
string? outFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--sessions" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int n) && n > 0) { sessions = n; i++; }
    else if (args[i] == "--out" && i + 1 < args.Length) outFile = Path.GetFullPath(args[++i]);
    else { Console.WriteLine($"FAIL(2): unknown argument '{args[i]}'. Usage: 119-detach-closes-a-handle-trace.cs [--sessions N] [--out facts.json]"); return 2; }
}
if (!OperatingSystem.IsWindows()) { Console.WriteLine("FAIL(2): probe 119, traced, is Windows only"); return 2; }

string directory = outFile is null ? Environment.CurrentDirectory : Path.GetDirectoryName(outFile)!;
string etl = Path.Combine(directory, "probe119-handles.etl");
string smokeFacts = Path.Combine(directory, "probe119-smoke.json");
string symbolCache = Path.Combine(directory, "symbols");

string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
string host = Path.Combine(Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..")), "dotnet.exe");
if (!File.Exists(host)) host = "dotnet";
string? smokeDll = Build(host, Path.GetFullPath("119-detach-closes-a-handle-smoke.cs"));
string? targetDll = Build(host, Path.GetFullPath("119-detach-closes-a-handle-target.cs"));
if (smokeDll is null || targetDll is null) { Console.WriteLine("FAIL(2): could not build probe 119 or its target"); return 2; }

var environment = new JsonObject
{
    ["machine"] = Environment.MachineName,
    ["os"] = RuntimeInformation.OSDescription,
    ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
    ["cores"] = Environment.ProcessorCount,
    ["probeRuntime"] = RuntimeInformation.FrameworkDescription,
    ["sessions"] = sessions,
    ["commit"] = Run("git", "rev-parse", "HEAD").Output.Trim(),
    ["wpr"] = Run("wpr", "-help").Output.Split('\n').FirstOrDefault(l => l.Contains("Version", StringComparison.OrdinalIgnoreCase))?.Trim(),
};
Console.WriteLine("environment:");
foreach (var (key, value) in environment) Console.WriteLine($"  {key}: {value?.ToJsonString().Trim('"')}");

File.Delete(etl);
File.Delete(smokeFacts);
var (startCode, startOutput) = Run("wpr", "-start", "Handle", "-filemode");
if (startCode != 0) { Console.WriteLine($"FAIL(2): wpr -start Handle -filemode exited {startCode}: {startOutput.Trim()}"); return 2; }
int debuggerPid;
int smokeExit;
try
{
    var psi = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
    foreach (string a in new[] { smokeDll, "--sessions", sessions.ToString(CultureInfo.InvariantCulture), "--out", smokeFacts }) psi.ArgumentList.Add(a);
    using Process smoke = Process.Start(psi)!;
    debuggerPid = smoke.Id;
    smoke.WaitForExit();
    smokeExit = smoke.ExitCode;
}
finally
{
    // The trace is stopped whatever probe 119 did, so that no system-wide trace outlives this probe.
    var (stopCode, stopOutput) = Run("wpr", "-stop", etl, "-skipPdbGen");
    Console.WriteLine($"wpr -stop: exit {stopCode}{(stopCode != 0 ? ": " + stopOutput.Trim() : "")}");
}
environment["debuggerPid"] = debuggerPid;
environment["probe119Exit"] = smokeExit;
if (!File.Exists(etl) || !File.Exists(smokeFacts)) { Console.WriteLine($"FAIL(2): no trace ({etl}) or no facts from probe 119 ({smokeFacts})"); return 2; }
environment["trace"] = etl;
environment["traceBytes"] = new FileInfo(etl).Length;
using (FileStream stream = File.OpenRead(etl)) environment["traceSha256"] = Convert.ToHexStringLower(SHA256.HashData(stream));

// Probe 119's sessions: each target's pid, the debugger's handle value it held, and what it lost.
JsonNode probe119 = JsonNode.Parse(File.ReadAllText(smokeFacts))!;
var targets = new List<(int Session, int Pid, int? Value, List<int> Lost)>();
foreach (JsonNode? s in probe119["sessions"]!.AsArray())
{
    JsonNode? target = s!["target"];
    var held = target?["debuggerHandlesWhileAttached"]?.AsArray().Select(h => Convert.ToInt32(h!.GetValue<string>()[2..], 16)).ToList() ?? [];
    var lost = target?["invalidEvents"]?.AsArray().Select(e => Convert.ToInt32(e!["handle"]!.GetValue<string>()[2..], 16)).ToList() ?? [];
    targets.Add((s["session"]!.GetValue<int>(), s["pid"]!.GetValue<int>(), held.Count == 1 ? held[0] : null, lost));
}
var valueByTarget = targets.Where(t => t.Value is not null).ToDictionary(t => t.Pid, t => t.Value!.Value);

using TraceLog log = TraceLog.OpenOrConvert(etl);
var symbolLog = new StringWriter();
var symbolModules = new JsonArray();
using (var reader = new SymbolReader(symbolLog, $"SRV*{symbolCache}*https://msdl.microsoft.com/download/symbols"))
{
    foreach (TraceModuleFile module in log.ModuleFiles)
    {
        string name = Path.GetFileNameWithoutExtension(module.FilePath);
        if (!new[] { "mscordbi", "KernelBase", "ntdll", "coreclr" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
        try { log.CodeAddresses.LookupSymbolsForModule(reader, module); symbolModules.Add(module.FilePath); }
        catch (Exception e) { symbolModules.Add($"{module.FilePath}: not resolved, {e.GetType().Name}: {e.Message}"); }
    }
}
environment["symbolsLookedUp"] = symbolModules;

// Each handle event on a target's debugger-handle value, in that target's handle table: created or closed there, or
// duplicated into or out of it.
var eventsByTarget = targets.ToDictionary(t => t.Pid, _ => new List<JsonObject>());
TraceLogEventSource source = log.Events.GetSource();
source.Kernel.ObjectCreateHandle += h => CollectHandle(h, "create");
source.Kernel.ObjectCloseHandle += h => CollectHandle(h, "close");
source.Kernel.ObjectDuplicateHandle += d =>
{
    bool outOfTarget = valueByTarget.TryGetValue(d.SourceProcessID, out int vs) && vs == d.SourceHandle;
    bool intoTarget = valueByTarget.TryGetValue(d.TargetProcessID, out int vt) && vt == d.TargetHandle;
    if (!outOfTarget && !intoTarget) return;
    Collect(d, outOfTarget ? "duplicate-out" : "duplicate-in", outOfTarget ? d.SourceProcessID : d.TargetProcessID, new JsonObject
    {
        ["object"] = $"0x{d.Object:X}", ["objectType"] = d.ObjectType, ["type"] = d.ObjectTypeName,
        ["sourcePid"] = d.SourceProcessID, ["sourceHandle"] = $"0x{d.SourceHandle:X}",
        ["targetPid"] = d.TargetProcessID, ["targetHandle"] = $"0x{d.TargetHandle:X}",
    });
};
source.Process();

void CollectHandle(ObjectHandleTraceData h, string kind)
{
    bool inTarget = valueByTarget.TryGetValue(h.ProcessID, out int v) && v == h.Handle;
    bool inDebugger = h.ProcessID == debuggerPid && valueByTarget.ContainsValue(h.Handle);
    if (!inTarget && !inDebugger) return;
    Collect(h, kind, h.ProcessID, new JsonObject { ["handle"] = $"0x{h.Handle:X}", ["object"] = $"0x{h.Object:X}", ["objectType"] = h.ObjectType, ["type"] = h.ObjectTypeName });
}

void Collect(TraceEvent e, string kind, int tablePid, JsonObject facts)
{
    var stack = new JsonArray();
    for (TraceCallStack? frame = e.CallStack(); frame is not null && stack.Count < 48; frame = frame.Caller)
    {
        TraceCodeAddress code = frame.CodeAddress;
        string method = code.FullMethodName;
        stack.Add(method.Length > 0 ? $"{code.ModuleName}!{method}" : $"{(code.ModuleName.Length > 0 ? code.ModuleName : "?")}!0x{code.Address:X}");
    }
    var record = new JsonObject
    {
        ["ms"] = Math.Round(e.TimeStampRelativeMSec, 3),
        ["kind"] = kind,
        ["headerPid"] = e.ProcessID,
        ["thread"] = e.ThreadID,
        ["threadOwnerPid"] = e.Thread()?.Process?.ProcessID,
    };
    foreach (var (key, value) in facts) record[key] = value?.DeepClone();
    record["stack"] = stack;
    // A debugger-side event belongs to the target whose life it falls in; the targets ran one after another.
    int owner = tablePid == debuggerPid
        ? log.Processes.Where(p => valueByTarget.ContainsKey(p.ProcessID) && p.StartTimeRelativeMsec <= e.TimeStampRelativeMSec && e.TimeStampRelativeMSec <= p.EndTimeRelativeMsec).Select(p => p.ProcessID).FirstOrDefault()
        : tablePid;
    if (eventsByTarget.TryGetValue(owner, out var list)) list.Add(record);
}
File.WriteAllText(Path.Combine(directory, "probe119-symbols.log"), symbolLog.ToString());

Console.WriteLine("sessions (the debugger's handle value V in the target's handle table):");
var sessionFacts = new JsonArray();
int lostSessions = 0, holds = 0, refuted = 0;
foreach (var (session, pid, value, lost) in targets)
{
    var events = eventsByTarget[pid].OrderBy(r => r["ms"]!.GetValue<double>()).ToList();
    string Who(JsonObject r) => r["threadOwnerPid"]?.GetValue<int>() == debuggerPid ? "debugger" : r["threadOwnerPid"]?.GetValue<int>() == pid ? "target" : $"pid {r["threadOwnerPid"]}";
    bool OwnedBy(JsonObject r, int p) => r["threadOwnerPid"]?.GetValue<int>() == p;

    JsonObject? attach = events.FirstOrDefault(r => r["kind"]!.GetValue<string>() == "duplicate-in" && OwnedBy(r, debuggerPid));
    string? processObject = attach?["object"]!.GetValue<string>();
    double attachMs = attach?["ms"]!.GetValue<double>() ?? double.MaxValue;
    JsonObject? firstClose = events.FirstOrDefault(r => r["kind"]!.GetValue<string>() == "close" && r["headerPid"]!.GetValue<int>() == pid && OwnedBy(r, pid) && r["object"]!.GetValue<string>() == processObject && r["ms"]!.GetValue<double>() > attachMs);
    double firstCloseMs = firstClose?["ms"]!.GetValue<double>() ?? double.MaxValue;
    JsonObject? reuse = events.FirstOrDefault(r => r["kind"]!.GetValue<string>() == "create" && r["headerPid"]!.GetValue<int>() == pid && OwnedBy(r, pid) && r["ms"]!.GetValue<double>() > firstCloseMs);
    string? reusedObject = reuse?["object"]!.GetValue<string>();
    double reuseMs = reuse?["ms"]!.GetValue<double>() ?? double.MaxValue;
    JsonObject? secondClose = events.FirstOrDefault(r => r["kind"]!.GetValue<string>() == "duplicate-out" && r["ms"]!.GetValue<double>() > reuseMs);
    bool secondCloseByDebugger = secondClose is not null && OwnedBy(secondClose, debuggerPid);
    bool secondCloseInMscordbi = secondClose?["stack"]!.AsArray().Any(f => f!.GetValue<string>().StartsWith("mscordbi", StringComparison.OrdinalIgnoreCase)) == true;
    bool secondCloseOnReused = secondClose is not null && secondClose["object"]!.GetValue<string>() == reusedObject;
    bool targetClosedReused = reusedObject is not null && events.Any(r => r["kind"]!.GetValue<string>() == "close" && r["headerPid"]!.GetValue<int>() == pid && OwnedBy(r, pid) && r["object"]!.GetValue<string>() == reusedObject);

    bool wasLost = lost.Count > 0;
    string verdict;
    if (!wasLost) verdict = "lost nothing (H does not apply)";
    else
    {
        lostSessions++;
        bool h = value is not null && lost.Count == 1 && lost[0] == value && attach is not null && firstClose is not null && reuse is not null
                 && secondCloseByDebugger && secondCloseInMscordbi && secondCloseOnReused && !targetClosedReused;
        if (h) { holds++; verdict = "H holds"; }
        else { refuted++; verdict = "H REFUTED"; }
    }
    string Step(JsonObject? r, string name) => r is null ? $"{name}: none" : $"{name} {r["ms"]} ms by {Who(r)} thread {r["thread"]} ({r["type"]} {r["object"]})";
    Console.WriteLine($"  {session,3} pid {pid}, V {(value is null ? "?" : $"0x{value:X}")}, lost [{string.Join(" ", lost.Select(l => $"0x{l:X}"))}]: {verdict}");
    Console.WriteLine($"       {Step(attach, "1 attach")}; {Step(firstClose, "2 first close")}; {Step(reuse, "3 reuse")}; {Step(secondClose, "4 second close")}");
    if (secondClose is not null)
        Console.WriteLine($"       4's user-mode stack: {string.Join(" <- ", secondClose["stack"]!.AsArray().Select(f => f!.GetValue<string>()).Where(f => !f.StartsWith("ntoskrnl", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("?!", StringComparison.Ordinal)).Take(8))}");
    sessionFacts.Add(new JsonObject
    {
        ["session"] = session, ["pid"] = pid, ["debuggerHandle"] = value is null ? null : $"0x{value:X}",
        ["lost"] = new JsonArray(lost.Select(l => (JsonNode?)$"0x{l:X}").ToArray()),
        ["verdict"] = verdict,
        ["steps"] = new JsonObject
        {
            ["attach"] = attach?["ms"]?.DeepClone(), ["firstClose"] = firstClose?["ms"]?.DeepClone(), ["reuse"] = reuse?["ms"]?.DeepClone(), ["secondClose"] = secondClose?["ms"]?.DeepClone(),
            ["secondCloseByDebugger"] = secondCloseByDebugger, ["secondCloseInMscordbi"] = secondCloseInMscordbi, ["secondCloseOnReused"] = secondCloseOnReused, ["targetClosedReused"] = targetClosedReused,
        },
        ["events"] = new JsonArray(events.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
    });
}

Console.WriteLine("summary:");
Console.WriteLine($"  sessions {targets.Count}: lost a handle {lostSessions}, H holds {holds}, H refuted {refuted}");
Console.WriteLine($"  symbol reader's log: {Path.Combine(directory, "probe119-symbols.log")}; a frame without a name shows module!address");
if (outFile is not null)
{
    var report = new JsonObject { ["probe"] = "119-detach-closes-a-handle-trace", ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["environment"] = environment, ["sessions"] = sessionFacts };
    File.WriteAllText(outFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"facts written to {outFile}");
}
if (lostSessions == 0) { Console.WriteLine("RESULT(2): no session lost a handle; H untested"); return 2; }
Console.WriteLine(refuted > 0
    ? "RESULT(1): H refuted in a session (see above)"
    : "RESULT(0): in every session that lost a handle, the second close was a DuplicateHandle out of the target by the debugger's thread, from mscordbi.dll");
return refuted > 0 ? 1 : 0;

static (int Code, string Output) Run(string file, params string[] arguments)
{
    try
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
    catch (Exception e) { return (-1, $"(not run: {e.GetType().Name}: {e.Message})"); }
}

static string? Build(string host, string csPath)
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
