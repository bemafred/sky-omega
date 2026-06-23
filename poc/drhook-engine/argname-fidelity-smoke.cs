#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe — ARGUMENT-NAME FIDELITY + this/field inspection in a file-based app
// ========================================================================================
//
// Substrate claims:
//  (1) DebugSession.GetArguments resolves each frame argument's REAL source name from the method's
//      PE metadata (MethodMetadata.ArgumentNames): a STATIC method's argument 0 is its first declared
//      parameter (NOT "this"); an instance method's argument 0 IS "this", then the parameters.
//  (2) "this" and an object ARGUMENT are inspectable — their fields read at depth 1 and via the lazy
//      ExpandArgument path (which backs drhook_expand). This is read by ARGUMENT INDEX in the engine;
//      the value/field reads are unchanged by the naming fix (naming is additive).
//  Pre-fix the MCP layer named arguments positionally (index 0 => "this", else "argN"), so EVERY
//  static method — every top-level-program local function and static helper — mislabelled argument 0
//  as "this". This probe also PINS that source breakpoints BIND + HIT in a file-based-app target.
//
// Construction: build the file-based target to a DLL (build-first — `dotnet run` delays the attach
// window, DRHOOK.md), launch under DebugSession, take the Debugger.Break setup stop, arm source
// breakpoints at STATIC_MARK + INSTANCE_MARK, and at each hit assert argument names; at the instance
// stop also inspect this[Box]._base and d[Delta].{By,Times} via depth-1 GetArguments and ExpandArgument.
//
// Falsification: 2 missing target / markers / build failure; 4 Launch threw; 5 no setup stop;
//   6 a source breakpoint did not bind (id == 0); 7 wrong argument names / wrong stop;
//   8 this/object-argument field inspection wrong; 0 PASS.
//
// Usage:  dotnet run --no-cache argname-fidelity-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkyOmega.DrHook.Engine;

return ArgNameFidelity.Run();

sealed class SilentSink : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnLog(LogRecord record) { }
}

static class ArgNameFidelity
{
    const string TargetRel = "argname-fidelity-target.cs";

    public static int Run()
    {
        string target = Path.GetFullPath(TargetRel);
        if (!File.Exists(target)) { Console.Error.WriteLine($"FALSIFIED (missing target): {target}. Run from poc/drhook-engine."); return 2; }

        int staticMark   = MarkerLine(target, "STATIC_MARK");
        int instanceMark = MarkerLine(target, "INSTANCE_MARK");
        if (staticMark < 0 || instanceMark < 0) { Console.Error.WriteLine("FALSIFIED (markers not found)."); return 2; }

        string? dll = BuildTarget(target);
        if (dll is null || !File.Exists(dll)) { Console.Error.WriteLine($"FALSIFIED (build): could not build {target} to a DLL."); return 2; }
        Console.WriteLine($"runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"target dll : {dll}");
        Console.WriteLine($"markers    : STATIC_MARK={staticMark}, INSTANCE_MARK={instanceMark}");

        var sink = new SilentSink();
        DebugSession session;
        try { session = DebugSession.Launch("dotnet", new[] { "exec", dll }, Path.GetDirectoryName(dll), sink); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 4; }
        Console.WriteLine($"launched   : pid {session.ProcessId}");

        int code = Drive(session, target, staticMark, instanceMark);
        try { session.Dispose(); } catch { /* Owned target torn down */ }
        return code;
    }

    static int Drive(DebugSession session, string source, int staticMark, int instanceMark)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break) { Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup?.Reason.ToString() ?? "timeout")}."); return 5; }
        Console.WriteLine("setup stop : Debugger.Break");

        int idStatic = session.SetBreakpointAtLine(source, staticMark);
        int idInst   = session.SetBreakpointAtLine(source, instanceMark);
        if (idStatic == 0 || idInst == 0) { Console.Error.WriteLine($"FALSIFIED (binding): static id={idStatic}, instance id={idInst} — a file-based source breakpoint did not bind."); return 6; }
        Console.WriteLine($"bound      : static bp id={idStatic}, instance bp id={idInst} (file-based binding OK)");

        // Stop 1: inside the STATIC method — argument 0 must be the first parameter, NOT "this".
        session.Resume();
        StopInfo? s1 = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (s1 is null || s1.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"FALSIFIED (static stop): {(s1?.Reason.ToString() ?? "timeout")}."); return 7; }
        List<string> a1 = session.GetArguments().Select(a => a.Name).ToList();
        Console.WriteLine($"static args: [{string.Join(", ", a1)}]");
        if (!a1.SequenceEqual(new[] { "seed", "factor" })) { Console.Error.WriteLine($"FALSIFIED: static-method args expected [seed, factor], got [{string.Join(", ", a1)}] — argument 0 must NOT be 'this'."); return 7; }

        // Stop 2: inside the INSTANCE method — argument 0 is "this", argument 1 is the object param 'd'.
        session.Resume();
        StopInfo? s2 = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (s2 is null || s2.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"FALSIFIED (instance stop): {(s2?.Reason.ToString() ?? "timeout")}."); return 7; }
        IReadOnlyList<ArgumentValue> args = session.GetArguments(depth: 1);
        List<string> a2 = args.Select(a => a.Name).ToList();
        Console.WriteLine($"inst args  : [{string.Join(", ", a2)}]");
        if (!a2.SequenceEqual(new[] { "this", "d" })) { Console.Error.WriteLine($"FALSIFIED: instance-method args expected [this, d], got [{string.Join(", ", a2)}]."); return 7; }

        // 'this' (Box) and its field _base, two ways: the depth-1 inline fields (what drhook_locals
        // depth=1 shows) and the lazy ExpandArgument path (what drhook_expand uses).
        ArgumentValue self = args[0];
        bool thisInline = self.Fields is { } sf && sf.Any(f => f.Name == "_base" && f.RawValue is int b1 && b1 == 100);
        IReadOnlyList<FieldValue> thisExpanded = session.ExpandArgument(0, Array.Empty<string>());
        bool thisExpand = thisExpanded.Any(f => f.Name == "_base" && f.RawValue is int b2 && b2 == 100);
        Console.WriteLine($"this[Box]  : inline _base==100 {thisInline}; expand _base==100 {thisExpand}  (fields: {string.Join(", ", thisExpanded.Select(f => $"{f.Name}={f.RawValue}"))})");
        if (!thisInline || !thisExpand) { Console.Error.WriteLine("FALSIFIED: 'this' field _base=100 not inspectable (inline and/or expand)."); return 8; }

        // The non-this OBJECT argument 'd' (Delta) and its fields By=5, Times=2 via ExpandArgument(1)
        // (engine, by index — the path the MCP expand-by-real-name fix routes to).
        IReadOnlyList<FieldValue> dExpanded = session.ExpandArgument(1, Array.Empty<string>());
        bool dOk = dExpanded.Any(f => f.Name == "By" && f.RawValue is int by && by == 5)
                && dExpanded.Any(f => f.Name == "Times" && f.RawValue is int t && t == 2);
        Console.WriteLine($"d[Delta]   : {string.Join(", ", dExpanded.Select(f => $"{f.Name}={f.RawValue}"))}");
        if (!dOk) { Console.Error.WriteLine("FALSIFIED: object argument 'd' fields By=5/Times=2 not inspectable via ExpandArgument(1)."); return 8; }

        Console.WriteLine("\nPROBE PASSED — file-based breakpoints bound+hit; real argument names (static [seed, factor] NO 'this'; instance [this, d]); 'this'[Box]._base=100 inspectable (depth-1 + expand); object argument 'd'[Delta] {By=5, Times=2} inspectable via ExpandArgument.");
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
        // The build prints "<name>.cs -> <abs path>/<name>.dll" (path may contain spaces).
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
