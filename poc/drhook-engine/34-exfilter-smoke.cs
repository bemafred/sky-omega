#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 34 — PERSISTENT EXCEPTION FILTER (Arm/List/Remove/Clear over WaitForStop)
// =============================================================================================
//
// Fourth Phase 3 substrate gap. ArmExceptionFilter(typeName, phase) registers a filter ONCE; the
// subsequent WaitForStop calls auto-resume non-matching exception stops and only surface matching
// ones. With NO filters armed, WaitForStop behaves exactly as before (probe 26 regression already
// re-validated). Backs drhook_step_break_exception as a steady-state arm-once primitive.
//
// Target throws ProbeException + OtherException alternately. Probe runs in two phases:
//   A. With filter on ProbeException (FirstChance): WaitForStop must surface ONLY ProbeException
//      stops -- OtherException stops auto-resume invisibly. Confirmed over 3 consecutive stops.
//   B. After Remove + Clear: WaitForStop must surface BOTH types again (filter is truly gone --
//      removal restores default behavior, not just silently kept armed).
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 Arm returned 0;
//   7 list state wrong after arm; 8 phase A — surfaced a non-ProbeException stop;
//   9 Remove returned false / list non-empty after remove;
//   10 phase B — only one type observed in unfiltered window (filter still active or stuck);
//   0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 34-exfilter-smoke.cs <path-to-34-exfilter-target.cs>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return ExFilter34.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class ExFilter34
{
    const string MatchingType = "ProbeException";
    const string OtherType    = "OtherException";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 34-exfilter-smoke.cs <path-to-34-exfilter-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : arm filter on {MatchingType} (FirstChance), verify only {MatchingType} surfaces; then remove + verify both types resurface.");

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

        int code = Drive(session);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }

        // Arm the filter BEFORE resuming so the very first exception is already gated.
        int id = session.ArmExceptionFilter(MatchingType, ExceptionStopKind.FirstChance);
        if (id <= 0)
        {
            Console.Error.WriteLine("FALSIFIED: ArmExceptionFilter returned non-positive id.");
            return 6;
        }
        Console.WriteLine($"arm        : id={id}  filter={MatchingType}@FirstChance");

        IReadOnlyList<ExceptionFilterInfo> list1 = session.ListExceptionFilters();
        if (list1.Count != 1 || list1[0].Id != id || list1[0].TypeName != MatchingType || list1[0].PhaseFilter != ExceptionStopKind.FirstChance)
        {
            Console.Error.WriteLine("FALSIFIED: ListExceptionFilters didn't reflect the armed entry.");
            return 7;
        }
        session.Resume();

        // ── Phase A: with the filter armed, three consecutive WaitForStop calls must surface
        //    ONLY MatchingType — every OtherException between them must be auto-resumed silently.
        Console.WriteLine("phase A    : 3 WaitForStop cycles, all must be ProbeException …");
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            StopInfo? s = session.WaitForStop(TimeSpan.FromSeconds(10));
            string? type = session.GetCurrentExceptionTypeName();
            Console.WriteLine($"   cycle {cycle}: stop={(s is null ? "null" : s.Reason.ToString())}  type={type ?? "(none)"}");
            if (s is null || s.Reason != StopReason.Exception || type != MatchingType)
            {
                Console.Error.WriteLine($"FALSIFIED (phase A, cycle {cycle}): expected Exception/{MatchingType}, got {s?.Reason.ToString() ?? "null"}/{type ?? "(none)"}.");
                return 8;
            }
            session.Resume();
        }

        // ── Remove the filter. List must become empty; the filter must NO LONGER gate.
        if (!session.RemoveExceptionFilter(id))
        {
            Console.Error.WriteLine("FALSIFIED: RemoveExceptionFilter returned false for a known id.");
            return 9;
        }
        IReadOnlyList<ExceptionFilterInfo> list2 = session.ListExceptionFilters();
        if (list2.Count != 0)
        {
            Console.Error.WriteLine($"FALSIFIED: after Remove, ListExceptionFilters returned {list2.Count} entries (expected 0).");
            return 9;
        }
        Console.WriteLine("remove     : filter removed; list empty");

        // ── Phase B: with no filters, WaitForStop surfaces every exception. Across a small
        //    window we must observe BOTH types (the filter is truly gone, not silently kept).
        Console.WriteLine("phase B    : up to 8 WaitForStop cycles, must observe BOTH types …");
        bool sawProbe = false, sawOther = false;
        for (int cycle = 1; cycle <= 8 && !(sawProbe && sawOther); cycle++)
        {
            StopInfo? s = session.WaitForStop(TimeSpan.FromSeconds(5));
            string? type = session.GetCurrentExceptionTypeName();
            Console.WriteLine($"   cycle {cycle}: stop={(s is null ? "null" : s.Reason.ToString())}  type={type ?? "(none)"}");
            if (s is null) break;
            if (s.Reason == StopReason.Exception)
            {
                if (type == MatchingType) sawProbe = true;
                else if (type == OtherType) sawOther = true;
            }
            session.Resume();
        }
        if (!(sawProbe && sawOther))
        {
            Console.Error.WriteLine($"FALSIFIED (phase B): expected to see BOTH {MatchingType} and {OtherType} after Remove; sawProbe={sawProbe}, sawOther={sawOther}.");
            return 10;
        }

        // Sanity: Clear on an empty list returns 0.
        if (session.ClearExceptionFilters() != 0)
        {
            Console.Error.WriteLine("FALSIFIED: ClearExceptionFilters on empty list returned non-zero.");
            return 9;
        }

        Console.WriteLine("\nPROBE 34 PASSED — persistent exception filter: arm gates WaitForStop to matching exceptions only; remove restores default (both types resurface).");
        return 0;
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
        string path = Path.Combine(dir, $"34-exfilter-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 34 fixture — persistent exception filter (Arm/List/Remove/Clear)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"phases           = A 3 cycles filtered to ProbeException; B 8 cycles unfiltered observing both types\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
