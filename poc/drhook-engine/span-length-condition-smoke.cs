#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe — SPAN-MEMBER ACCESS in a condition (direct field read, NOT func-eval)
// ===========================================================================================
//
// Substrate claim (ADR-013 D3): a conditional breakpoint / logpoint can read a member of a
// ReadOnlySpan<T> / Span<T> argument — e.g. `value.Length > 5`. A span is a ref struct: it cannot be
// passed as a func-eval receiver (the runtime cannot box it as the getter's `this`), so the getter call
// faults (conditionError). DebugSession.TryEvalMemberCall now reads the span's backing field directly
// (`.Length` → `_length`) via Variables.TryReadSpanMember instead of func-eval'ing the getter.
//
// Construction: build the file-based target to a DLL (build-first), launch under DebugSession, take the
// Debugger.Break setup stop, arm a conditional breakpoint at SPAN_MARK with condition `value.Length > 5`.
// The target calls Scan twice — "abc" (Length=3, FALSE) then "abcdefgh" (Length=8, TRUE). A clean
// Breakpoint stop proves the condition EVALUATED on both calls (no fault) AND gated correctly (skipped 3,
// stopped on 8); TryEvalMemberCall(value.Length) at the stop confirms it reads 8 directly.
//
// Falsification: 2 missing target/marker/build; 4 Launch threw; 5 no setup stop; 6 breakpoint did not
//   bind; 7 stop was conditionError (span member did NOT evaluate) or the wrong reason; 8 value.Length
//   did not read as 8 at the stop; 0 PASS.
//
// Usage:  dotnet run --no-cache span-length-condition-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Diagnostics;
using System.IO;
using SkyOmega.DrHook.Engine;

return SpanLengthCondition.Run();

sealed class SilentSink : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnLog(LogRecord record) { }
}

static class SpanLengthCondition
{
    const string TargetRel = "span-length-condition-target.cs";

    public static int Run()
    {
        string target = Path.GetFullPath(TargetRel);
        if (!File.Exists(target)) { Console.Error.WriteLine($"FALSIFIED (missing target): {target}. Run from poc/drhook-engine."); return 2; }

        int spanMark = MarkerLine(target, "SPAN_MARK");
        if (spanMark < 0) { Console.Error.WriteLine("FALSIFIED (marker not found)."); return 2; }

        string? dll = BuildTarget(target);
        if (dll is null || !File.Exists(dll)) { Console.Error.WriteLine($"FALSIFIED (build): could not build {target} to a DLL."); return 2; }
        Console.WriteLine($"runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"target dll : {dll}");
        Console.WriteLine($"marker     : SPAN_MARK={spanMark}");

        var sink = new SilentSink();
        DebugSession session;
        try { session = DebugSession.Launch("dotnet", new[] { "exec", dll }, Path.GetDirectoryName(dll), sink); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 4; }
        Console.WriteLine($"launched   : pid {session.ProcessId}");

        int code = Drive(session, target, spanMark);
        try { session.Dispose(); } catch { /* Owned target torn down */ }
        return code;
    }

    static int Drive(DebugSession session, string source, int spanMark)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break) { Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup?.Reason.ToString() ?? "timeout")}."); return 5; }
        Console.WriteLine("setup stop : Debugger.Break");

        // Conditional breakpoint whose condition reads `.Length` on the ReadOnlySpan<char> ARGUMENT 'value'.
        BreakpointPolicy policy = session.Compile(new BreakpointPolicySpec(Condition: "value.Length > 5", Suspend: SuspendPolicy.All));
        int bp = session.SetBreakpointAtLine(source, spanMark, policy);
        if (bp == 0) { Console.Error.WriteLine("FALSIFIED (binding): conditional breakpoint did not bind."); return 6; }
        Console.WriteLine($"bound      : conditional bp id={bp}  condition=\"value.Length > 5\" (span member access — direct field read)");

        session.Resume();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(15));
        string reason = stop is null ? "null (timeout)" : stop.Reason.ToString();
        Console.WriteLine($"resumed    : stop={reason}");
        if (stop is null || stop.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED: expected a Breakpoint stop (the condition value.Length>5 evaluated on the span argument and gated to the Length=8 call); got {reason}. A conditionError means span member access did NOT resolve (a ref struct cannot be func-eval'd — D3 reads the field directly).");
            return 7;
        }

        // Confirm the gate selected the right call: value.Length must read 8 directly at the stop.
        EvalStatus st = session.TryEvalMemberCall("value", "Length", TimeSpan.FromSeconds(10), out ArgumentValue length);
        Console.WriteLine($"member read: TryEvalMemberCall(value.Length) -> {st}, RawValue={length.RawValue ?? "(null)"}");
        if (st != EvalStatus.Completed || !Equals(length.RawValue, 8))
        {
            Console.Error.WriteLine($"FALSIFIED: value.Length should read 8 at the Length=8 call (the Length=3 call was gated out); got {st} / {length.RawValue ?? "(null)"}.");
            return 8;
        }

        Console.WriteLine("\nPROBE PASSED — a conditional breakpoint read `.Length` on a ReadOnlySpan<char> ARGUMENT (value.Length>5): evaluated on both calls without fault and stopped exactly on the Length=8 call. The span member resolved via a direct field read, not func-eval (ADR-013 D3).");
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
