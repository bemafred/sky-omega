#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 33 — DebugSession.Launch (attach BEFORE main runs) ======================
//
// Third Phase 3 substrate gap. Launch a .NET process under debug control so we are attached
// before any managed code runs — the target's Debugger.Break() in the first line of Main is the
// first stop we receive. Powered by dbgshim's RegisterForRuntimeStartup flow (spawn suspended →
// register a static callback → resume → await the callback delivering ICorDebug*). Backs
// drhook_step_run + drhook_step_test in the MCP rewrite.
//
// Probe target is a precompiled csproj (33-launch-target/Launch.csproj) — file-based apps go
// through dotnet's compile-then-exec, which would attach to the WRONG process. The target's
// Program.cs has Debugger.Break followed by a loop with a PROBE_BREAK marker; the probe launches
// dotnet Launch.dll, expects a Break stop (proves attach-before-main), sets a line breakpoint at
// the marker, resumes, expects a Breakpoint stop with the local `v` in scope.
//
// Falsification: 2 target not built; 3 Launch failed; 4 first stop != Break; 5 SetBreakpointAtLine;
//   6 second stop != Breakpoint; 7 local `v` missing/wrong; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 33-launch-smoke.cs

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Launch33.Run();

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Launch33
{
    const string TargetDir = "33-launch-target";
    const string TargetDll = "Launch.dll";
    const string MarkerToken = "PROBE_BREAK";

    public static int Run()
    {
        string probeDir = AppContext.BaseDirectory;
        // The script-app's BaseDirectory is buried in ~/Library/Application Support/dotnet/runfile/…
        // — resolve the target via this file's runtime path instead. Walk up to find the poc dir.
        string scriptDir = FindPocDir() ?? Environment.CurrentDirectory;
        string targetProj = Path.Combine(scriptDir, TargetDir);
        string targetDll = Path.Combine(targetProj, "bin", "Debug", "net10.0", TargetDll);
        string targetSource = Path.Combine(targetProj, "Program.cs");

        if (!File.Exists(targetDll))
        {
            Console.Error.WriteLine($"FALSIFIED (target not built): expected {targetDll} — run `dotnet build` in {targetProj} first.");
            return 2;
        }
        int markerLine = FindMarker(targetSource, MarkerToken);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED: '{MarkerToken}' not found in {targetSource}."); return 2; }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"target     : dotnet {targetDll}");
        Console.WriteLine($"plan       : Launch -> expect Break (Debugger.Break before main loop) -> set bp at Program.cs:{markerLine} -> resume -> expect Breakpoint with local v in scope.");

        string dotnetPath = ResolveDotnet();
        DebugSession session;
        try
        {
            session = DebugSession.Launch(dotnetPath, new[] { targetDll }, workingDirectory: null, sink: new NullSink());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
        Console.WriteLine($"launched   : pid={session.ProcessId} (debugger attached before main)");

        int code = Drive(session, markerLine);

        // Cleanup: kill the launched target BEFORE Dispose. Dispose's Detach path leaves a
        // currently-stopped process synchronized indefinitely (no implicit Continue), and we
        // own the lifecycle for a Launched session. PID-based kill is the pragmatic answer
        // here; engine-side Terminate-on-dispose for Launched sessions is a follow-on.
        int pid = session.ProcessId;
        try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ }
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        try { session.Dispose(); } catch { /* best effort */ }
        WriteFixture(pid, code, scriptDir);
        return code;
    }

    static int Drive(DebugSession session, int markerLine)
    {
        StopInfo? stop1 = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (stop1 is null || stop1.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (first stop): {(stop1 is null ? "timeout" : stop1.Reason.ToString())}, expected Break (Debugger.Break before the loop).");
            return 4;
        }
        Console.WriteLine($"stop 1     : {stop1.Reason}  (proves attach-before-main: Debugger.Break fired)");

        int id = session.SetBreakpointAtLine("Launch", "Program.cs", markerLine);
        if (id == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): Program.cs:{markerLine}.");
            return 5;
        }
        Console.WriteLine($"breakpoint : Program.cs:{markerLine} id={id}");
        session.Resume();

        StopInfo? stop2 = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (stop2 is null || stop2.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED (second stop): {(stop2 is null ? "timeout" : stop2.Reason.ToString())}, expected Breakpoint.");
            return 6;
        }

        long? v = session.GetLocals().FirstOrDefault(l => l.Name == "v").RawValue;
        Console.WriteLine($"stop 2     : {stop2.Reason}  v={v?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"}");
        if (v is null)
        {
            Console.Error.WriteLine("FALSIFIED: local 'v' not present at the breakpoint stop.");
            return 7;
        }

        Console.WriteLine("\nPROBE 33 PASSED — DebugSession.Launch attaches before main; a Debugger.Break and a subsequent line breakpoint both surface as stops, with locals readable at the breakpoint.");
        return 0;
    }

    static string ResolveDotnet()
    {
        // `dotnet` from PATH usually works; if a child dotnet host is needed, the absolute path
        // is more robust. Use the running runtime's dotnet host when available.
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        // runtimeDir = .../shared/Microsoft.NETCore.App/<ver>/ -> go up to the dotnet root.
        string? dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (dotnetRoot is not null)
        {
            string candidate = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    static string? FindPocDir()
    {
        // We're typically run from the poc/drhook-engine/ working dir. Resolve relative to the
        // CWD; the smoke is invoked as `dotnet 33-launch-smoke.cs`.
        string cwd = Environment.CurrentDirectory;
        return Directory.Exists(Path.Combine(cwd, TargetDir)) ? cwd : null;
    }

    static int FindMarker(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    static void WriteFixture(int pid, int code, string scriptDir)
    {
        string dir = Path.Combine(scriptDir, "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"33-launch-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 33 fixture — DebugSession.Launch (attach before main)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"launched-pid     = {pid}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
