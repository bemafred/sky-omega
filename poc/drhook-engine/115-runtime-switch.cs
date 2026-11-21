#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:project ../../src/DrHook.Mcp/DrHook.Mcp.csproj
//
// DrHook.Engine probe 115 — finding 86's lock, with a debugger worker per session (ADR-016 6.7) ======================
//
// Finding 86: a debugger process loads the debug components (mscordbi and the DAC) of the first target's runtime
// version, process-wide and for good, so a later target on another version fails (DebugSession now refuses it up
// front, GuardRuntimeLock). ADR-016 6.7 decided a worker per session, and gave as one reason: "Finding 86's lock goes
// away". This checks that, with the supervisor and the worker as drhook-mcp now runs them (built 2026-11-21).
//
// Construction: the target (115-runtime-switch-target.cs) is built for .NET 9 and for .NET 10. A file-based app's two
// builds share one output directory, so each build's output is copied to a directory of its own before the next, and
// each copy's runtimeconfig.json must name its framework (the first run, without the copy, ran .NET 10 twice). Then:
//   - in this process (the control): DebugSession.Launch of the .NET 9 target, to its entry-module hold, Abandon; then
//     the same for the .NET 10 target, in the same process;
//   - through the supervisor: drhook_launch of the .NET 9 target to its breakpoint, drhook_kill; then drhook_launch of
//     the .NET 10 target, drhook_kill. Each launch starts a fresh worker.
// The control runs last, so that it does not lock this process before the supervisor's turn (the supervisor's
// sessions load nothing here).
//
// Hypothesis: through the supervisor both launches stop at the breakpoint, each locked to its own target's version
// (lockedRuntime .NET 9, then .NET 10); in this process the second launch is refused with finding 86's message.
// Refuted if a launch through the supervisor fails, or reports another lockedRuntime, or if the control's second launch
// is not refused (then the control does not show the lock, and the probe shows nothing).
//
// RESULT(0) when the hypothesis holds; 1 when it is refuted; 2 when a build failed or the .NET 9 runtime is missing.
//
// Usage (from poc/drhook-engine, in a clone that no running drhook-mcp runs from, since it builds src/DrHook.Mcp):
//   dotnet run --no-cache 115-runtime-switch.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Mcp;

string targetSource = Path.GetFullPath("115-runtime-switch-target.cs");
string? net9 = Build(targetSource, "net9.0");
string? net10 = Build(targetSource, "net10.0");
if (net9 is null || net10 is null) { Console.WriteLine("FAIL(2): a build of the target failed"); return 2; }
int stopLine = Array.FindIndex(File.ReadAllLines(targetSource), l => l.Contains("PROBE115_STOP", StringComparison.Ordinal)) + 1;
string dotnet = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
    OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"targets: {net9}\n         {net10}");
Console.WriteLine($"breakpoint: {targetSource}:{stopLine}");

bool holds = true;

Console.WriteLine();
Console.WriteLine("== through the supervisor: a worker per session");
using (var supervisor = new Supervisor(Path.Combine(AppContext.BaseDirectory, "worker"),
           Path.Combine(Path.GetTempPath(), $"probe115-journal-{Environment.ProcessId}.ndjson")))
{
    foreach ((string dll, string expected) in new[] { (net9, ".NET 9"), (net10, ".NET 10") })
    {
        var launchArgs = new JsonObject
        {
            ["program"] = dotnet, ["args"] = new JsonArray("exec", dll), ["cwd"] = null,
            ["sourceFile"] = targetSource, ["line"] = stopLine, ["hypothesis"] = $"stops at the loop, on {expected}", ["env"] = null,
        };
        var clock = Stopwatch.StartNew();
        string launch = supervisor.CallAsync("drhook_launch", launchArgs, CancellationToken.None).GetAwaiter().GetResult().Result!;
        long launchMs = clock.ElapsedMilliseconds;
        using JsonDocument doc = JsonDocument.Parse(launch);
        JsonElement root = doc.RootElement;
        string status = root.GetProperty("status").GetString()!;
        string? locked = root.TryGetProperty("lockedRuntime", out JsonElement l) ? l.GetString() : null;
        string? reason = root.TryGetProperty("stoppedReason", out JsonElement r) ? r.GetString() : null;
        Console.WriteLine($"  {expected}: status {status}, stoppedReason {reason}, lockedRuntime {locked}, {launchMs} ms");
        if (status != "launched" || reason != "breakpoint" || locked != expected)
        {
            holds = false;
            Console.WriteLine($"    REFUTED: {launch}");
        }
        string killed = supervisor.CallAsync("drhook_kill", new JsonObject { ["hypothesis"] = "end the probe's session" }, CancellationToken.None)
            .GetAwaiter().GetResult().Result!;
        using JsonDocument k = JsonDocument.Parse(killed);
        Console.WriteLine($"    kill: {k.RootElement.GetProperty("status").GetString()}, killedWhile {k.RootElement.GetProperty("killedWhile").GetString()}");
    }
}

Console.WriteLine();
Console.WriteLine("== the control: both targets in this process");
var sink = new NoRecords();
foreach ((string dll, string expected) in new[] { (net9, ".NET 9"), (net10, ".NET 10") })
{
    try
    {
        DebugSession session = DebugSession.Launch(dotnet, new[] { "exec", dll }, workingDirectory: null, sink: sink,
            entryModule: Path.GetFileNameWithoutExtension(dll));
        StopInfo? hold = session.WaitForStop(TimeSpan.FromSeconds(20));
        Console.WriteLine($"  {expected}: launched, {hold?.Reason}, this process locked to .NET {DebugSession.ProcessRuntimeMajor}");
        session.Abandon();
        if (expected == ".NET 10") { holds = false; Console.WriteLine("    REFUTED: the control's second launch was not refused, so it does not show the lock"); }
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("locked to .NET", StringComparison.Ordinal))
    {
        Console.WriteLine($"  {expected}: refused, as finding 86 predicts: {ex.Message[..Math.Min(ex.Message.Length, 120)]}...");
        if (expected == ".NET 9") holds = false;
    }
}

foreach (string dll in new[] { net9, net10 })
    try { Directory.Delete(Path.GetDirectoryName(dll)!, recursive: true); } catch (IOException) { /* best effort */ }

Console.WriteLine();
Console.WriteLine(holds
    ? "RESULT(0): through the supervisor each session's worker locked to its own target's version, and both launches stopped; in one process the second was refused"
    : "RESULT(1): refuted (see above)");
return holds ? 0 : 1;

// Build the target for a target framework, copy the output to a directory of its own, and return the copy's DLL; null
// when the build failed or the copy's runtimeconfig.json does not name that framework.
static string? Build(string csPath, string targetFramework)
{
    var psi = new ProcessStartInfo("dotnet", $"build \"{csPath}\" -c Debug -p:TargetFramework={targetFramework}")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(output); return null; }
    foreach (string line in output.Split('\n'))
    {
        int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow < 0) continue;
        string path = line[(arrow + 3)..].Trim();
        if (!path.EndsWith(".dll", StringComparison.Ordinal) || !File.Exists(path)) continue;
        string copy = Path.Combine(Path.GetTempPath(), $"probe115-{targetFramework}-{Environment.ProcessId}");
        Directory.CreateDirectory(copy);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), overwrite: true);
        string dll = Path.Combine(copy, Path.GetFileName(path));
        string config = File.ReadAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"));
        if (!config.Contains($"\"tfm\": \"{targetFramework}\"", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"the build for {targetFramework} produced another framework: {config}");
            return null;
        }
        return dll;
    }
    return null;
}

sealed class NoRecords : IDebugEventSink
{
    public void OnEvent(string name) { }
}
