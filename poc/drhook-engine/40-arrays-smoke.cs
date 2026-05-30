#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 40 — ARRAY RENDERING (ICorDebugArrayValue, SZARRAY, depth ≥ 2) ============
//
// Object inspection slice 6 — the last substrate slice. ArrayInspector.TryReadElements reads
// rank-1 array elements via GetCount@11 + GetElementAtPosition@16 (slots verified from
// cordebug.idl). Variables.GetChildren dispatches by ElementType: CLASS/OBJECT → FieldEnumerator,
// SZARRAY/ARRAY → ArrayInspector. Both inspectors recurse via the same dispatcher so arrays of
// objects and objects with array fields compose without either knowing about the other.
//
// Three phases against ONE breakpoint, all at depth ≥ 1:
//   A. int[] numbers — Fields = [{"[0]", I4, 1}, {"[1]", I4, 2}, ...]; 5 entries.
//   B. string[] names — Fields = [{"[0]", STRING, StringValue="alpha"}, ...]; 3 entries.
//   C. Item[] items at depth=2 — Fields = [{"[0]", CLASS, Fields={N=10}}, ...]; recursion works.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 no Breakpoint stop; 8 phase A wrong; 9 phase B wrong; 10 phase C wrong; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 40-arrays-smoke.cs <path-to-40-arrays-target.cs>

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

return Arrays40.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Arrays40
{
    const string ModuleSubstr = "40-arrays-target";
    const string FileHint     = "40-arrays-target.cs";
    const string Marker       = "ARRAYS_HERE";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 40-arrays-smoke.cs <path-to-40-arrays-target.cs>");
            return 2;
        }

        int markerLine = FindMarker(args[0], Marker);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : at {FileHint}:{markerLine}, validate int[] / string[] / Item[] rendering (depth=1 + depth=2 recursive through array into object).");

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

        int code = Drive(session, markerLine);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session, int markerLine)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        if (session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine) == 0)
        { Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): {FileHint}:{markerLine}."); return 6; }
        session.Resume();

        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (stop is null || stop.Reason != StopReason.Breakpoint)
        { Console.Error.WriteLine($"FALSIFIED: expected Breakpoint, got {stop?.Reason.ToString() ?? "null"}."); return 7; }

        IReadOnlyList<LocalValue> locals = session.GetLocals(depth: 1);
        LocalValue numbers = locals.FirstOrDefault(l => l.Name == "numbers");
        LocalValue names   = locals.FirstOrDefault(l => l.Name == "names");

        // ── Phase A: int[] ─────────────────────────────────────────────────────────────────────
        Console.WriteLine($"phase A    : numbers.Fields = {numbers.Fields?.Count.ToString() ?? "(null)"} entries");
        if (numbers.Fields is not null)
            foreach (FieldValue f in numbers.Fields) Console.WriteLine($"             - {f.Name}: type=0x{f.ElementType:X2} raw={f.RawValue}");
        int[] expectedNumbers = { 1, 2, 3, 5, 8 };
        if (numbers.Fields is null || numbers.Fields.Count != expectedNumbers.Length
            || !expectedNumbers.Select((v, i) => numbers.Fields[i].Name == $"[{i}]" && Equals(numbers.Fields[i].RawValue, v)).All(b => b))
        { Console.Error.WriteLine($"FALSIFIED (phase A): int[] elements wrong."); return 8; }

        // ── Phase B: string[] ──────────────────────────────────────────────────────────────────
        Console.WriteLine($"phase B    : names.Fields = {names.Fields?.Count.ToString() ?? "(null)"} entries");
        if (names.Fields is not null)
            foreach (FieldValue f in names.Fields) Console.WriteLine($"             - {f.Name}: type=0x{f.ElementType:X2} str=\"{f.StringValue ?? "(null)"}\"");
        string[] expectedNames = { "alpha", "beta", "gamma" };
        if (names.Fields is null || names.Fields.Count != expectedNames.Length
            || !expectedNames.Select((v, i) => names.Fields[i].Name == $"[{i}]" && names.Fields[i].StringValue == v).All(b => b))
        { Console.Error.WriteLine($"FALSIFIED (phase B): string[] elements wrong."); return 9; }

        // ── Phase C: Item[] at depth=2 — array → object recursion ─────────────────────────────
        IReadOnlyList<LocalValue> locals2 = session.GetLocals(depth: 2);
        LocalValue items = locals2.FirstOrDefault(l => l.Name == "items");
        Console.WriteLine($"phase C    : items.Fields = {items.Fields?.Count.ToString() ?? "(null)"} entries (depth=2)");
        int[] expectedNs = { 10, 20, 30 };
        if (items.Fields is null || items.Fields.Count != expectedNs.Length)
        { Console.Error.WriteLine($"FALSIFIED (phase C): items count wrong."); return 10; }
        for (int i = 0; i < items.Fields.Count; i++)
        {
            FieldValue item = items.Fields[i];
            FieldValue? n = item.Fields?.FirstOrDefault(f => f.Name == "N");
            Console.WriteLine($"             - {item.Name}: type=0x{item.ElementType:X2} N={(n?.RawValue is { } _n ? Convert.ToString(_n, CultureInfo.InvariantCulture) : "(missing)")}");
            if (item.Name != $"[{i}]" || item.Fields is null || n is null || !Equals(n.Value.RawValue, expectedNs[i]))
            { Console.Error.WriteLine($"FALSIFIED (phase C): items[{i}].N wrong (got {n?.RawValue})."); return 10; }
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 40 PASSED — SZARRAY rendering: int[]/string[]/Item[] all expand to indexed elements; depth=2 recurses array→object via the shared GetChildren dispatcher.");
        return 0;
    }

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
        string path = Path.Combine(dir, $"40-arrays-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 40 fixture — array rendering (SZARRAY, depth >= 2)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"phases           = A int[]; B string[]; C Item[] at depth=2 (array->object recursion)\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
