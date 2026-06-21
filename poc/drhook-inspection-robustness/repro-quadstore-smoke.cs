#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook crash-reproduction DRIVER (ADR-007 — inspection at scale).
// Launches the QuadStore target, breaks in Inspect(store, result), and calls GetArguments(depth)
// — the wide depth-2 walk that dropped the engine. Distinguishes the failure mode:
//   * a managed exception is caught + printed (recoverable);
//   * a native fault (AV/SIGSEGV in mscordbi) kills THIS process — no "SURVIVED" line prints.
// Run with DOTNET_DbgEnableMiniDump=1 to capture the native stack on a crash.
//
// Usage:  dotnet repro-quadstore-smoke.cs <path-to-repro-quadstore-target.cs> [depth=2]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Repro.Run(args);

sealed class RecordingSink : IDebugEventSink
{
    private readonly object _gate = new();
    public readonly List<string> Anomalies = new();
    public int Events;
    public void OnEvent(string name) { lock (_gate) Events++; }
    public void OnAnomaly(EngineAnomaly anomaly) { lock (_gate) Anomalies.Add(anomaly.ToString() ?? "(anomaly)"); }
}

static class Repro
{
    const string ModuleSubstr = "repro-quadstore-target";
    const string FileHint = "repro-quadstore-target.cs";
    const string Marker = "QS_BP";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet repro-quadstore-smoke.cs <target.cs> [depth=2]");
            return 2;
        }
        int depth = args.Length >= 2 && int.TryParse(args[1], out int d) ? d : 2;
        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"marker '{Marker}' not found"); return 2; }
        Console.WriteLine($"break line : {FileHint}:{markerLine}   inspect depth={depth}");

        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{args[0]}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
        };
        proc.Start();

        int realPid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                Console.WriteLine($"   [target] {line}");
                Match m = Regex.Match(line, @"READY (\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                { Volatile.Write(ref realPid, pid); ready.Set(); }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is { } e) Console.Error.WriteLine($"   [target-err] {e}"); })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(120)))
        { Console.Error.WriteLine("no READY within 120s"); KillTree(proc); return 3; }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        var sink = new RecordingSink();
        DebugSession session;
        try { session = DebugSession.Attach(realPid, sink); }
        catch (Exception ex) { Console.Error.WriteLine($"Attach failed: {ex.GetType().Name}: {ex.Message}"); KillTree(proc); return 4; }
        Console.WriteLine("attached   : DebugSession established");

        int code = Drive(session, markerLine, depth, sink);

        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { }
        return code;
    }

    static int Drive(DebugSession session, int markerLine, int depth, RecordingSink sink)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break)
        { Console.Error.WriteLine($"no setup stop: {(setup is null ? "timeout" : setup.Reason.ToString())}"); return 5; }

        if (session.SetBreakpointAtLine(FileHint, markerLine) == 0)
        { Console.Error.WriteLine($"SetBreakpointAtLine failed for {FileHint}:{markerLine}"); return 6; }
        session.Resume();

        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(30));
        if (hit is null || hit.Reason != StopReason.Breakpoint)
        { Console.Error.WriteLine($"expected Breakpoint hit, got {(hit is null ? "timeout" : hit.Reason.ToString())}"); return 7; }
        Console.WriteLine($"stopped    : at {FileHint}:{markerLine} (Inspect frame: store, result)");

        Console.WriteLine($">>> calling GetArguments(depth={depth}) — the wide walk. If this is the bug, the process dies HERE.");
        Console.Out.Flush();
        var sw = Stopwatch.StartNew();
        try
        {
            var a = session.GetArguments(depth);
            sw.Stop();
            Console.WriteLine($"<<< SURVIVED GetArguments(depth={depth}): {a.Count} args in {sw.ElapsedMilliseconds}ms");
            for (int i = 0; i < a.Count; i++)
                Console.WriteLine($"      arg[{i}] elementType=0x{a[i].ElementType:X2} children={(a[i].Fields?.Count.ToString() ?? "null")}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"<<< MANAGED EXCEPTION from GetArguments(depth={depth}): {ex.GetType().Name}: {ex.Message}");
        }

        // Lazy deep navigation: walk arg1 (the QuadStore) down many levels, ONE level per call.
        // This is the path that REPLACES the eager deep walk that SIGSEGV'd in coreclr's unwinder —
        // it must reach arbitrary depth WITHOUT faulting.
        Console.WriteLine(">>> lazy deep navigation of arg1 (the QuadStore) via ExpandArgument — must NOT crash at any depth.");
        Console.Out.Flush();
        var path = new List<string>();
        for (int lvl = 0; lvl < 8; lvl++)
        {
            IReadOnlyList<FieldValue> kids = session.ExpandArgument(1, path);
            Console.WriteLine($"      arg1/{string.Join("/", path)} -> {kids.Count} children");
            FieldValue next = default;
            foreach (FieldValue k in kids) { if (k.HasChildren) { next = k; break; } }
            if (next.Name is null) break;
            path.Add(next.Name);
        }
        Console.WriteLine($"<<< SURVIVED lazy deep navigation ({path.Count} levels deep)");

        Console.WriteLine($"anomalies  : {sink.Anomalies.Count}");
        foreach (var an in sink.Anomalies) Console.WriteLine($"   - {an}");

        try { session.Resume(); } catch { }
        return 0;
    }

    static int FindMarkerLine(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(Marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
    }
}
