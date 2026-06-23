#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe — MEMBER-ACCESS ON AN ARGUMENT RECEIVER in a condition (func-eval)
// =======================================================================================
//
// Substrate claim: a conditional breakpoint / logpoint can call a member (property getter) on an
// ARGUMENT receiver, not only a local — e.g. condition `w.Area == 200` where w is a parameter.
// DebugSession.ResolveReceiverValue resolves the receiver name to its value from a local slot OR an
// argument index (GetActiveFrameArgumentValue), then func-evals the getter on it. Pre-fix the receiver
// resolved from locals only (ResolveLocalSlot), so a member on an argument faulted (conditionError).
//
// Construction: build the file-based target to a DLL (build-first), launch under DebugSession, take
// the Debugger.Break setup stop, arm a conditional breakpoint at MEMBER_MARK with condition
// `w.Area == 200`. The target calls Describe twice — Widget(5,5) Area=25 (FALSE) then Widget(10,20)
// Area=200 (TRUE). A clean Breakpoint stop proves the getter evaluated on the argument receiver on
// BOTH calls (no fault) AND gated correctly (skipped 25, stopped on 200); inspecting w confirms it is
// the 10x20 widget.
//
// Falsification: 2 missing target/marker/build; 4 Launch threw; 5 no setup stop; 6 breakpoint did not
//   bind; 7 stop was conditionError (member-access on the argument receiver did NOT evaluate) or the
//   wrong reason; 8 stopped on the wrong call (w is not the 10x20 widget); 0 PASS.
//
// Usage:  dotnet run --no-cache eval-arg-receiver-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkyOmega.DrHook.Engine;

return EvalArgReceiver.Run();

sealed class SilentSink : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnLog(LogRecord record) { }
}

static class EvalArgReceiver
{
    const string TargetRel = "eval-arg-receiver-target.cs";

    public static int Run()
    {
        string target = Path.GetFullPath(TargetRel);
        if (!File.Exists(target)) { Console.Error.WriteLine($"FALSIFIED (missing target): {target}. Run from poc/drhook-engine."); return 2; }

        int memberMark = MarkerLine(target, "MEMBER_MARK");
        if (memberMark < 0) { Console.Error.WriteLine("FALSIFIED (marker not found)."); return 2; }

        string? dll = BuildTarget(target);
        if (dll is null || !File.Exists(dll)) { Console.Error.WriteLine($"FALSIFIED (build): could not build {target} to a DLL."); return 2; }
        Console.WriteLine($"runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"target dll : {dll}");
        Console.WriteLine($"marker     : MEMBER_MARK={memberMark}");

        var sink = new SilentSink();
        DebugSession session;
        try { session = DebugSession.Launch("dotnet", new[] { "exec", dll }, Path.GetDirectoryName(dll), sink); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 4; }
        Console.WriteLine($"launched   : pid {session.ProcessId}");

        int code = Drive(session, target, memberMark);
        try { session.Dispose(); } catch { /* Owned target torn down */ }
        return code;
    }

    static int Drive(DebugSession session, string source, int memberMark)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break) { Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup?.Reason.ToString() ?? "timeout")}."); return 5; }
        Console.WriteLine("setup stop : Debugger.Break");

        // Conditional breakpoint whose condition calls a property getter on the ARGUMENT 'w'.
        BreakpointPolicy policy = session.Compile(new BreakpointPolicySpec(Condition: "w.Area == 200", Suspend: SuspendPolicy.All));
        int bp = session.SetBreakpointAtLine(source, memberMark, policy);
        if (bp == 0) { Console.Error.WriteLine("FALSIFIED (binding): conditional breakpoint did not bind."); return 6; }
        Console.WriteLine($"bound      : conditional bp id={bp}  condition=\"w.Area == 200\" (member access on an argument receiver)");

        session.Resume();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(15));
        string reason = stop is null ? "null (timeout)" : stop.Reason.ToString();
        Console.WriteLine($"resumed    : stop={reason}");
        if (stop is null || stop.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED: expected a Breakpoint stop (the condition w.Area==200 evaluated the getter on the argument receiver and gated to the Area=200 call); got {reason}. A conditionError means member-access on the argument receiver did NOT resolve.");
            return 7;
        }

        // Confirm the gate selected the right call: w must be the 10x20 widget (Area=200), not 5x5.
        IReadOnlyList<ArgumentValue> args = session.GetArguments(depth: 1);
        ArgumentValue w = args.FirstOrDefault(a => a.Name == "w");
        IReadOnlyList<FieldValue> wFields = session.ExpandArgument(args.ToList().FindIndex(a => a.Name == "w"), Array.Empty<string>());
        int? width = wFields.Where(f => f.Name == "Width").Select(f => f.RawValue as int?).FirstOrDefault();
        int? height = wFields.Where(f => f.Name == "Height").Select(f => f.RawValue as int?).FirstOrDefault();
        Console.WriteLine($"receiver   : arg \"{w.Name}\" Widget {{ Width={width}, Height={height} }}  (Area={(width ?? 0) * (height ?? 0)})");
        if (width != 10 || height != 20)
        {
            Console.Error.WriteLine($"FALSIFIED: stopped on the wrong call — expected the 10x20 widget (Area=200), got Width={width} Height={height}.");
            return 8;
        }

        Console.WriteLine("\nPROBE PASSED — a conditional breakpoint called a property getter on an ARGUMENT receiver (w.Area): evaluated on both calls without fault and stopped exactly on the Area=200 call (w = 10x20). Member-access on an argument receiver works.");
        return 0;
    }

    static string? BuildTarget(string targetCs)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{targetCs}\" -c Debug -v:m")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using Process p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        foreach (string line in outp.Split('\n'))
        {
            int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
            if (arrow < 0) continue;
            string path = line[(arrow + 3)..].Trim();
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return path;
        }
        Console.Error.WriteLine(outp);
        Console.Error.WriteLine(err);
        return null;
    }

    static int MarkerLine(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal)) return i + 1;
        return -1;
    }
}
