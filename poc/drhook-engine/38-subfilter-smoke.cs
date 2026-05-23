#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 38 — SUBCLASS-AWARE exception filter (chain-walk via GetBase) ============
//
// Finding 43's noted follow-on, made cheap by probe 37's GetBase chain. ExceptionInspector
// .CurrentExceptionTypeChain walks ICorDebugValue2.GetExactType + ICorDebugType.GetBase, naming
// each level via MetadataResolver.TypeNameFromToken. ExceptionFilterInfo.MatchesChain accepts
// the chain and matches against any level. DebugSession.ExceptionMatchesAnyFilter uses
// MatchesChain so a filter on a base class matches any subclass.
//
// Target throws ProbeException + OtherException alternately, both deriving directly from
// System.Exception. Two phases:
//   Phase A: filter "System.Exception" — BOTH types surface (subclass-aware).
//   Phase B: filter "ProbeException" — only ProbeException (exact match still works for
//     non-base filters).
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break;
//   6 phase A Arm; 7 phase A didn't see both types in budget;
//   8 phase B Arm; 9 phase B saw OtherException; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 38-subfilter-smoke.cs <path-to-38-subfilter-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return SubFilter38.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class SubFilter38
{
    const string ProbeType = "ProbeException";
    const string OtherType = "OtherException";
    const string BaseType  = "System.Exception";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 38-subfilter-smoke.cs <path-to-38-subfilter-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : phase A filter \"{BaseType}\" — both ProbeException + OtherException must surface; phase B filter \"{ProbeType}\" — only ProbeException.");

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
        if (setup is null || setup.Reason != StopReason.Break) { Console.Error.WriteLine("FALSIFIED (no setup stop)."); return 5; }

        // ── Phase A: subclass-aware filter on System.Exception ───────────────────────────────────
        int idA = session.ArmExceptionFilter(BaseType, ExceptionStopKind.FirstChance);
        if (idA <= 0) { Console.Error.WriteLine("FALSIFIED (phase A Arm)."); return 6; }
        Console.WriteLine($"phase A    : arm id={idA} filter=\"{BaseType}\" — both user-defined exceptions should surface");
        session.Resume();

        bool sawProbe = false, sawOther = false;
        for (int cycle = 1; cycle <= 8 && !(sawProbe && sawOther); cycle++)
        {
            StopInfo? s = session.WaitForStop(TimeSpan.FromSeconds(5));
            string? type = session.GetCurrentExceptionTypeName();
            Console.WriteLine($"   cycle {cycle}: stop={(s is null ? "null" : s.Reason.ToString())}  type={type ?? "(none)"}");
            if (s is null) break;
            if (s.Reason == StopReason.Exception)
            {
                if (type == ProbeType) sawProbe = true;
                else if (type == OtherType) sawOther = true;
            }
            session.Resume();
        }
        if (!(sawProbe && sawOther))
        {
            Console.Error.WriteLine($"FALSIFIED (phase A): subclass filter \"{BaseType}\" should surface BOTH user exceptions; sawProbe={sawProbe}, sawOther={sawOther}.");
            return 7;
        }
        Console.WriteLine($"phase A OK : saw both {ProbeType} and {OtherType} via base-class filter");
        session.RemoveExceptionFilter(idA);

        // ── Phase B: exact filter on ProbeException only ─────────────────────────────────────────
        // No session.Resume() here: phase A's last loop iteration already resumed the target.
        // A redundant Resume would pre-arm a Continue command the worker would consume on the
        // very next stop without WaitForStop seeing it — and the matching stop could go past us.
        int idB = session.ArmExceptionFilter(ProbeType, ExceptionStopKind.FirstChance);
        if (idB <= 0) { Console.Error.WriteLine("FALSIFIED (phase B Arm)."); return 8; }
        Console.WriteLine($"phase B    : arm id={idB} filter=\"{ProbeType}\" — only ProbeException should surface");

        int probeStops = 0;
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            StopInfo? s = session.WaitForStop(TimeSpan.FromSeconds(5));
            string? type = session.GetCurrentExceptionTypeName();
            Console.WriteLine($"   cycle {cycle}: stop={(s is null ? "null" : s.Reason.ToString())}  type={type ?? "(none)"}");
            if (s is null) break;
            if (s.Reason == StopReason.Exception && type != ProbeType)
            {
                Console.Error.WriteLine($"FALSIFIED (phase B): non-{ProbeType} surfaced under \"{ProbeType}\" filter — got {type}.");
                return 9;
            }
            if (s.Reason == StopReason.Exception && type == ProbeType) probeStops++;
            session.Resume();
        }
        if (probeStops == 0)
        {
            Console.Error.WriteLine("FALSIFIED (phase B): no ProbeException stop observed in 5 cycles — filter or timing failure.");
            return 9;
        }
        Console.WriteLine($"phase B OK : observed {probeStops} {ProbeType} stops (and zero non-{ProbeType})");

        Console.WriteLine($"\nPROBE 38 PASSED — subclass-aware exception filter: \"{BaseType}\" matches any subclass via chain-walk; \"{ProbeType}\" still exact-matches only itself.");
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
        string path = Path.Combine(dir, $"38-subfilter-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 38 fixture — subclass-aware exception filter (chain-walk via GetBase)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"phases           = A filter '{BaseType}' surfaces both user types; B filter '{ProbeType}' only ProbeException\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
