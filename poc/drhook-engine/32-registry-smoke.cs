#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 32 — BREAKPOINT REGISTRY: List / RemoveBreakpoint(id) / ClearBreakpoints
// =============================================================================================
//
// Second Phase 3 substrate gap from the "what's left" assessment. SetBreakpoint and
// SetBreakpointAtLine now return an int id (0 = failed); ListBreakpoints returns typed
// BreakpointInfo records (LineBreakpointInfo / FunctionBreakpointInfo subtypes — pattern-matchable);
// RemoveBreakpoint(id) deactivates + releases one entry; ClearBreakpoints removes all. Backs
// drhook_step_breakpoint_list / _remove / _clear in the MCP rewrite.
//
// Pure registry-semantics probe — no breakpoint is ever HIT; the target just loops. Sets THREE
// breakpoints (function entry + two source lines), lists, removes the middle one by id, lists
// again (confirms the right entries remain with their ids intact), clears, lists empty.
//
// Falsification: 2 usage/markers; 3 no READY; 4 attach; 5 no setup Break; 6 any Set returned 0;
//   7 list-after-set wrong (count/shape/ids); 8 Remove failed or list-after-remove wrong;
//   9 Clear count wrong or list-after-clear non-empty; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 32-registry-smoke.cs <path-to-32-registry-target.cs>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Registry32.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Registry32
{
    const string ModuleSubstr = "32-registry-target";
    const string FileHint = "32-registry-target.cs";
    const string TypeName = "Worker";
    const string MethodName = "Step";
    const string MarkerA = "BREAK_A";
    const string MarkerB = "BREAK_B";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 32-registry-smoke.cs <path-to-32-registry-target.cs>");
            return 2;
        }

        int lineA = FindMarker(args[0], MarkerA);
        int lineB = FindMarker(args[0], MarkerB);
        if (lineA < 0 || lineB < 0) { Console.Error.WriteLine($"FALSIFIED (usage): markers not found (A={lineA} B={lineB})."); return 2; }
        if (lineA == lineB) { Console.Error.WriteLine($"FALSIFIED (usage): markers resolved to the SAME line {lineA} — check the target's header comment doesn't contain BREAK_A/BREAK_B."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : 3 breakpoints (function {TypeName}.{MethodName} + line:{lineA} + line:{lineB}), list/remove/clear");

        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{args[0]}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();

        int realPid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                Match m = Regex.Match(line, @"READY (\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                {
                    Volatile.Write(ref realPid, pid);
                    ready.Set();
                }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(90)))
        {
            Console.Error.WriteLine("FALSIFIED (target): no READY sentinel within 90s.");
            KillTree(proc);
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        DebugSession session;
        try { session = DebugSession.Attach(realPid, new NullSink()); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        int code = Drive(session, lineA, lineB);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session, int lineA, int lineB)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }

        // --- Set 3 breakpoints ------------------------------------------------------------------
        int idFn = session.SetBreakpoint(ModuleSubstr, TypeName, MethodName);
        int idA  = session.SetBreakpointAtLine(ModuleSubstr, FileHint, lineA);
        int idB  = session.SetBreakpointAtLine(ModuleSubstr, FileHint, lineB);
        Console.WriteLine($"set        : function id={idFn}, line A id={idA}, line B id={idB}");
        if (idFn == 0 || idA == 0 || idB == 0)
        {
            Console.Error.WriteLine($"FALSIFIED: at least one Set returned 0 — Fn={idFn} A={idA} B={idB}.");
            return 6;
        }
        // ids must be distinct and monotonic.
        if (!(idA > idFn && idB > idA))
        {
            Console.Error.WriteLine($"FALSIFIED: ids not monotonic: {idFn}, {idA}, {idB}.");
            return 6;
        }

        // --- List after Set --------------------------------------------------------------------
        IReadOnlyList<BreakpointInfo> list1 = session.ListBreakpoints();
        Console.WriteLine($"list       : {list1.Count} entries");
        foreach (BreakpointInfo bp in list1) Console.WriteLine($"             - {Describe(bp)}");

        if (list1.Count != 3
            || list1[0] is not FunctionBreakpointInfo f1 || f1.Id != idFn || f1.TypeName != TypeName || f1.MethodName != MethodName
            || list1[1] is not LineBreakpointInfo l1 || l1.Id != idA || l1.Line != lineA
            || list1[2] is not LineBreakpointInfo l2 || l2.Id != idB || l2.Line != lineB)
        {
            Console.Error.WriteLine("FALSIFIED: list-after-set didn't match (count/order/subtype/id/descriptor).");
            return 7;
        }

        // --- Remove the middle one (line A) ----------------------------------------------------
        bool removed = session.RemoveBreakpoint(idA);
        IReadOnlyList<BreakpointInfo> list2 = session.ListBreakpoints();
        Console.WriteLine($"remove({idA}) -> {removed}; list now {list2.Count} entries");
        foreach (BreakpointInfo bp in list2) Console.WriteLine($"             - {Describe(bp)}");

        if (!removed || list2.Count != 2
            || list2.Any(bp => bp.Id == idA)
            || list2[0] is not FunctionBreakpointInfo f2 || f2.Id != idFn
            || list2[1] is not LineBreakpointInfo l3 || l3.Id != idB || l3.Line != lineB)
        {
            Console.Error.WriteLine("FALSIFIED: list-after-remove wrong (removed flag / count / surviving ids).");
            return 8;
        }
        // Removing the same id again must return false (idempotent in the "no-op" direction).
        if (session.RemoveBreakpoint(idA))
        {
            Console.Error.WriteLine("FALSIFIED: second Remove of a missing id returned true.");
            return 8;
        }

        // --- Clear ----------------------------------------------------------------------------
        int cleared = session.ClearBreakpoints();
        IReadOnlyList<BreakpointInfo> list3 = session.ListBreakpoints();
        Console.WriteLine($"clear      -> {cleared} removed; list now {list3.Count} entries");

        if (cleared != 2 || list3.Count != 0)
        {
            Console.Error.WriteLine($"FALSIFIED: clear returned {cleared} (expected 2) and/or list count {list3.Count} (expected 0).");
            return 9;
        }

        // The session is in a usable state — re-set one more to prove the registry survives clear.
        int idAfter = session.SetBreakpointAtLine(ModuleSubstr, FileHint, lineA);
        if (idAfter == 0 || idAfter <= idB)
        {
            Console.Error.WriteLine($"FALSIFIED: id allocator broken after clear (next id was {idAfter}, expected > {idB}).");
            return 9;
        }
        Console.WriteLine($"post-clear : Set returned id={idAfter} (monotonic across clear — registry is still healthy)");

        Console.WriteLine($"\nPROBE 32 PASSED — registry (Set/List/Remove/Clear) round-trips correctly; ids are positive, distinct, and monotonic; subtypes carry the right descriptors.");
        return 0;
    }

    static string Describe(BreakpointInfo bp) => bp switch
    {
        LineBreakpointInfo l => $"id={l.Id} Line {l.FilePath}:{l.Line}",
        FunctionBreakpointInfo f => $"id={f.Id} Function {f.TypeName}.{f.MethodName}",
        _ => $"id={bp.Id} (unknown subtype)"
    };

    static int FindMarker(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"32-registry-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 32 fixture — breakpoint registry (List/Remove/Clear + ids)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
