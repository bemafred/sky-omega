#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// ADR-014 fault localizer. Attaches to adr014-faultrepro-target, breaks in Mimic.Touch (so `this`
// is BYREF → the BindingTable-shaped ref struct), and runs ONE inspection step per invocation. The
// abort is an access violation escalated to a CSE fail-fast (crash report 040558) — uncatchable
// in-process — so each shape is probed in its OWN process: the step that dies (BEGIN printed, no OK)
// is the culprit. Steps:
//   args0       GetArguments(0)            bare ReadValue on this(byref)+additional(int), no field walk
//   args1       GetArguments(1)            expand `this` one level — the real drhook_locals path
//   expand_all  ExpandArgument(0, [])      read ALL fields of `this` at once
//   f:<name>    ExpandArgument(0,[name])   read ONE field (e.g. f:_bindings, f:_stringBuffer, f:_n)
//
// Usage: dotnet --no-cache adr014-faultrepro-smoke.cs <target.cs> <type> <step>
//   <type> = NormalBox | PlainRef | Mimic   (a function breakpoint on <type>.Touch)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Repro.Run(args);

sealed class NullSink : IDebugEventSink { public void OnEvent(string name) { } }

static class Repro
{
    const string ModuleHint = "adr014-faultrepro-target";

    public static int Run(string[] args)
    {
        if (args.Length < 3 || !File.Exists(args[0])) { Console.Error.WriteLine("usage: dotnet adr014-faultrepro-smoke.cs <target.cs> <type> <step>"); return 2; }
        string typeName = args[1];
        string step = args[2];

        // Spawn the target with --no-cache so its PDB line-map matches the CURRENT source (a stale
        // file-based-app cache silently breaks SetBreakpointAtLine — the bp never binds).
        using Process proc = new() { StartInfo = new ProcessStartInfo("dotnet", $"run --no-cache --file \"{args[0]}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        proc.Start();
        int pid = -1; ManualResetEventSlim ready = new(false);
        new Thread(() => { string? l; while ((l = proc.StandardOutput.ReadLine()) is not null) { var m = Regex.Match(l, @"READY (\d+)"); if (m.Success) { Volatile.Write(ref pid, int.Parse(m.Groups[1].Value)); ready.Set(); } } }) { IsBackground = true }.Start();
        new Thread(() => { while (proc.StandardError.ReadLine() is { } e) Console.Error.WriteLine($"   [target-err] {e}"); }) { IsBackground = true }.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(90))) { Console.Error.WriteLine("no READY within 90s"); Kill(proc); return 3; }
        pid = Volatile.Read(ref pid);
        Console.WriteLine($"target pid : {pid}  type: {typeName}  step: {step}");

        DebugSession session;
        try { session = DebugSession.Attach(pid, new NullSink()); }
        catch (Exception ex) { Console.Error.WriteLine($"attach failed: {ex.GetType().Name}: {ex.Message}"); Kill(proc); return 4; }

        int code = Drive(session, typeName, step);
        Kill(proc); Thread.Sleep(300); try { session.Dispose(); } catch { }
        return code;
    }

    static int Drive(DebugSession s, string typeName, string step)
    {
        if (s.WaitForStop(TimeSpan.FromSeconds(10)) is not { Reason: StopReason.Break }) { Console.Error.WriteLine("no setup stop"); return 5; }
        if (s.SetBreakpoint(ModuleHint, typeName, "Touch") == 0) { Console.Error.WriteLine($"SetBreakpoint failed for {typeName}.Touch"); return 6; }
        s.Resume();
        if (s.WaitForStop(TimeSpan.FromSeconds(10)) is not { Reason: StopReason.Breakpoint }) { Console.Error.WriteLine("no breakpoint hit"); return 7; }

        // BEGIN is flushed BEFORE the (possibly aborting) native call. If the engine dies, BEGIN is on
        // disk with no matching OK — that localizes the faulting shape.
        Begin(step);
        switch (step)
        {
            case "args0":
            {
                var a = s.GetArguments(0);
                Ok(step, a.Count + " args");
                for (int i = 0; i < a.Count; i++) Console.WriteLine($"     arg[{i}] elementType=0x{a[i].ElementType:X2} hasChildren={a[i].HasChildren}");
                break;
            }
            case "args1":
            {
                var a = s.GetArguments(1);
                Ok(step, a.Count + " args (this expanded one level)");
                for (int i = 0; i < a.Count; i++)
                {
                    Console.WriteLine($"     arg[{i}] elementType=0x{a[i].ElementType:X2} hasChildren={a[i].HasChildren} fields={(a[i].Fields?.Count ?? 0)}");
                    if (a[i].Fields is { } fs) foreach (var f in fs) PrintField(f);
                }
                break;
            }
            case "expand_all":
            {
                var fs = s.ExpandArgument(0, Array.Empty<string>());
                Ok(step, fs.Count + " fields of this");
                foreach (var f in fs) PrintField(f);
                break;
            }
            default:
            {
                if (!step.StartsWith("f:", StringComparison.Ordinal)) { Console.Error.WriteLine($"unknown step '{step}'"); return 9; }
                string field = step.Substring(2);
                var fs = s.ExpandArgument(0, new[] { field });
                Ok(step, fs.Count + $" children of this.{field}");
                foreach (var f in fs) PrintField(f);
                break;
            }
        }

        s.Resume();
        return 0;
    }

    static void Begin(string step) { Console.WriteLine($">>> BEGIN {step}"); Console.Out.Flush(); }
    static void Ok(string step, string detail) { Console.WriteLine($"<<< OK {step} : {detail}"); Console.Out.Flush(); }
    static void PrintField(FieldValue f) => Console.WriteLine($"       {f.Name} : 0x{f.ElementType:X2} raw={f.RawValue?.ToString() ?? "(null)"} str={f.StringValue ?? "(null)"} hasChildren={f.HasChildren}");

    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
}
