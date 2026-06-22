#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// ADR-014 finale driver (the dogfood). Attaches to the real Mercury target, breaks in
// BindingTable.EnsureStringCapacity, and runs the EXACT inspection that aborted the engine before the
// D1 fix: GetArguments(1) expands `this` (BYREF → the real BindingTable ref struct) one level, then
// drills this._stringBuffer for its Length. Expect: no engine crash; every field read or opaque.
//
// Usage: dotnet run --no-cache --file adr014-finale-smoke.cs -- adr014-finale-target.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Finale.Run(args);

sealed class NullSink : IDebugEventSink { public void OnEvent(string name) { } }

static class Finale
{
    const string ModuleHint = "Mercury.dll";  // unique to SkyOmega.Mercury.dll (not .Abstractions/.Runtime)
    const string TypeName = "SkyOmega.Mercury.Sparql.Types.BindingTable";
    const string Method = "EnsureStringCapacity";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0])) { Console.Error.WriteLine("usage: ... -- <finale-target.cs>"); return 2; }

        using Process proc = new() { StartInfo = new ProcessStartInfo("dotnet", $"run --no-cache --file \"{args[0]}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        proc.Start();
        int pid = -1; ManualResetEventSlim ready = new(false);
        new Thread(() => { string? l; while ((l = proc.StandardOutput.ReadLine()) is not null) { var m = Regex.Match(l, @"READY (\d+)"); if (m.Success) { Volatile.Write(ref pid, int.Parse(m.Groups[1].Value)); ready.Set(); } } }) { IsBackground = true }.Start();
        new Thread(() => { while (proc.StandardError.ReadLine() is { } e) Console.Error.WriteLine($"   [target-err] {e}"); }) { IsBackground = true }.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(120))) { Console.Error.WriteLine("no READY within 120s"); Kill(proc); return 3; }
        pid = Volatile.Read(ref pid);
        Console.WriteLine($"target pid : {pid}");

        DebugSession session;
        try { session = DebugSession.Attach(pid, new NullSink()); }
        catch (Exception ex) { Console.Error.WriteLine($"attach failed: {ex.GetType().Name}: {ex.Message}"); Kill(proc); return 4; }

        int code = Drive(session);
        Kill(proc); Thread.Sleep(300); try { session.Dispose(); } catch { }
        return code;
    }

    static int Drive(DebugSession s)
    {
        if (s.WaitForStop(TimeSpan.FromSeconds(15)) is not { Reason: StopReason.Break }) { Console.Error.WriteLine("no setup stop"); return 5; }
        if (s.SetBreakpoint(ModuleHint, TypeName, Method) == 0) { Console.Error.WriteLine($"SetBreakpoint failed for {TypeName}.{Method}"); return 6; }
        s.Resume();
        if (s.WaitForStop(TimeSpan.FromSeconds(20)) is not { Reason: StopReason.Breakpoint }) { Console.Error.WriteLine("no breakpoint hit"); return 7; }

        Console.WriteLine(">>> BEGIN GetArguments(1) — expand `this` (real BindingTable) one level");
        Console.Out.Flush();
        var a = s.GetArguments(1);
        Console.WriteLine($"<<< OK GetArguments(1): {a.Count} args (no engine crash)");
        for (int i = 0; i < a.Count; i++)
        {
            Console.WriteLine($"   arg[{i}] 0x{a[i].ElementType:X2} raw={a[i].RawValue?.ToString() ?? "(null)"} fields={(a[i].Fields?.Count ?? 0)}");
            if (a[i].Fields is { } fs) foreach (var f in fs) Console.WriteLine($"      {f.Name} : 0x{f.ElementType:X2} raw={f.RawValue?.ToString() ?? "(null)"} hasChildren={f.HasChildren}");
        }

        Console.WriteLine(">>> BEGIN GetLocals(1) — additional/target (the finale's named ints)");
        Console.Out.Flush();
        var locals = s.GetLocals(1);
        Console.WriteLine($"<<< OK GetLocals(1): {locals.Count} locals");
        foreach (var l in locals) Console.WriteLine($"   {l.Name} : 0x{l.ElementType:X2} raw={l.RawValue?.ToString() ?? "(null)"}");

        Console.WriteLine(">>> BEGIN ExpandArgument(0,[_stringBuffer]) — read this._stringBuffer.Length");
        Console.Out.Flush();
        var sb = s.ExpandArgument(0, new[] { "_stringBuffer" });
        Console.WriteLine($"<<< OK ExpandArgument: {sb.Count} children of this._stringBuffer");
        foreach (var f in sb) Console.WriteLine($"   {f.Name} : 0x{f.ElementType:X2} raw={f.RawValue?.ToString() ?? "(null)"} hasChildren={f.HasChildren}");

        s.Resume();
        return 0;
    }

    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
}
