#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// ADR-013 validation driver: confirm VALUETYPE (struct/span) + BYREF (struct-method `this`) expansion.
// Attaches to the target, breaks in Box.Inspect, and uses ExpandArgument to read the struct's fields
// (through the byref `this`) and the span's _length (the value the literal hunt couldn't reach).
//
// Usage: dotnet adr013-vt-smoke.cs <path-to-adr013-vt-target.cs>

using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Vt.Run(args);

sealed class NullSink : IDebugEventSink { public void OnEvent(string name) { } }

static class Vt
{
    const string FileHint = "adr013-vt-target.cs";
    const string Marker = "VT_BP";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0])) { Console.Error.WriteLine("usage: dotnet adr013-vt-smoke.cs <target.cs>"); return 2; }
        int markerLine = FindMarker(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"marker '{Marker}' not found"); return 2; }

        using Process proc = new() { StartInfo = new ProcessStartInfo("dotnet", $"\"{args[0]}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        proc.Start();
        int pid = -1; ManualResetEventSlim ready = new(false);
        new Thread(() => { string? l; while ((l = proc.StandardOutput.ReadLine()) is not null) { var m = Regex.Match(l, @"READY (\d+)"); if (m.Success) { Volatile.Write(ref pid, int.Parse(m.Groups[1].Value)); ready.Set(); } } }) { IsBackground = true }.Start();
        new Thread(() => { while (proc.StandardError.ReadLine() is { } e) Console.Error.WriteLine($"   [target-err] {e}"); }) { IsBackground = true }.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(90))) { Console.Error.WriteLine("no READY within 90s"); Kill(proc); return 3; }
        pid = Volatile.Read(ref pid);
        Console.WriteLine($"target pid : {pid}");

        DebugSession session;
        try { session = DebugSession.Attach(pid, new NullSink()); }
        catch (Exception ex) { Console.Error.WriteLine($"attach failed: {ex.GetType().Name}: {ex.Message}"); Kill(proc); return 4; }

        int code = Drive(session, markerLine);
        Kill(proc); Thread.Sleep(300); try { session.Dispose(); } catch { }
        return code;
    }

    static int Drive(DebugSession s, int line)
    {
        if (s.WaitForStop(TimeSpan.FromSeconds(10)) is not { Reason: StopReason.Break }) { Console.Error.WriteLine("no setup stop"); return 5; }
        if (s.SetBreakpointAtLine(FileHint, line) == 0) { Console.Error.WriteLine($"SetBreakpointAtLine failed for {FileHint}:{line}"); return 6; }
        s.Resume();
        if (s.WaitForStop(TimeSpan.FromSeconds(10)) is not { Reason: StopReason.Breakpoint }) { Console.Error.WriteLine("no breakpoint hit"); return 7; }

        var a = s.GetArguments();
        Console.WriteLine($"stopped at Box.Inspect — {a.Count} args:");
        for (int i = 0; i < a.Count; i++)
            Console.WriteLine($"  arg[{i}] elementType=0x{a[i].ElementType:X2} hasChildren={a[i].HasChildren}");

        Console.WriteLine(">>> D2: expand arg0 (this — BYREF 0x10 → deref → Box struct):");
        var thisFields = s.ExpandArgument(0, Array.Empty<string>());
        foreach (var f in thisFields)
            Console.WriteLine($"     {f.Name} : 0x{f.ElementType:X2} raw={f.RawValue?.ToString() ?? "(null)"} str={f.StringValue ?? "(null)"} hasChildren={f.HasChildren}");

        Console.WriteLine(">>> D1: expand arg1 (s — ReadOnlySpan<char> VALUETYPE 0x11):");
        var spanFields = s.ExpandArgument(1, Array.Empty<string>());
        foreach (var f in spanFields)
            Console.WriteLine($"     {f.Name} : 0x{f.ElementType:X2} raw={f.RawValue?.ToString() ?? "(null)"} hasChildren={f.HasChildren}");

        bool d1 = false; foreach (var f in spanFields) if (f.Name.Contains("length", StringComparison.OrdinalIgnoreCase) && Equals(f.RawValue, 10)) d1 = true;
        bool d2 = false; foreach (var f in thisFields) if (f.Name == "N") d2 = true;
        Console.WriteLine($"\nRESULT: D1 span._length==10 read = {d1}; D2 byref this→Box.N read = {d2}");

        s.Resume();
        return (d1 && d2) ? 0 : 8;
    }

    static int FindMarker(string p) { var ls = File.ReadAllLines(p); for (int i = 0; i < ls.Length; i++) if (ls[i].Contains(Marker, StringComparison.Ordinal)) return i + 1; return -1; }
    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
}
