#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 39 — OBJECT FIELD WALKING (GetFieldValue@8, EnumFields@20, depth >= 2) ==
//
// Object inspection slice 5 (last substrate slice before arrays). DebugSession.GetLocals(depth)
// populates LocalValue.Fields for object locals when depth > 0. Field reading walks GetExactType
// + GetBase per Type level (same primitive as probes 37/38), enumerates fields per level via
// IMetaDataImport.EnumFields@20, names them via GetFieldProps@57, reads each value via
// ICorDebugObjectValue.GetFieldValue@8 with that level's ICorDebugClass. Recursive when
// depth > 1: nested object fields expand into their own Fields list. Backs drhook_step_vars.
//
// Two phases against ONE target/breakpoint:
//   Phase A (depth=1): the local `counter` has Fields containing Count=42 (int), Label="hello"
//     (string, via StringValue), Active=true (bool, RawValue=1), Nested (Class, Fields=null).
//   Phase B (depth=2): counter.Nested.Fields contains X=99 — nested expansion works.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 no Breakpoint stop; 8 phase A fields wrong (missing names or wrong values);
//   9 phase B Nested.Fields wrong (missing X or wrong value); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 39-fields-smoke.cs <path-to-39-fields-target.cs>

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

return Fields39.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Fields39
{
    const string ModuleSubstr = "39-fields-target";
    const string FileHint     = "39-fields-target.cs";
    const string Marker       = "FIELDS_HERE";
    const string LocalName    = "counter";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 39-fields-smoke.cs <path-to-39-fields-target.cs>");
            return 2;
        }

        int markerLine = FindMarker(args[0], Marker);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : at {FileHint}:{markerLine}, GetLocals(depth=1) populates counter.Fields; GetLocals(depth=2) populates Nested.Fields too.");

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
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): {FileHint}:{markerLine}."); return 6;
        }
        session.Resume();

        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (stop is null || stop.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED: expected Breakpoint, got {stop?.Reason.ToString() ?? "null"}."); return 7;
        }

        // ── Phase A: depth=1 — counter.Fields should contain Count / Label / Active / Nested ────
        LocalValue counter1 = session.GetLocals(depth: 1).FirstOrDefault(l => l.Name == LocalName);
        if (counter1.Fields is null)
        {
            Console.Error.WriteLine($"FALSIFIED (phase A): GetLocals(depth=1) returned no Fields for '{LocalName}'.");
            return 8;
        }
        Console.WriteLine($"phase A    : counter.Fields = {counter1.Fields.Count} entries");
        foreach (FieldValue f in counter1.Fields)
            Console.WriteLine($"             - {f.Name}: type=0x{f.ElementType:X2} raw={(f.RawValue is { } _r ? Convert.ToString(_r, CultureInfo.InvariantCulture) : "(none)")} str=\"{f.StringValue ?? "(null)"}\" nested={(f.Fields is null ? "no" : f.Fields.Count + " entries")}");

        FieldValue? fCount = counter1.Fields.FirstOrDefault(f => f.Name == "Count");
        FieldValue? fLabel = counter1.Fields.FirstOrDefault(f => f.Name == "Label");
        FieldValue? fActive = counter1.Fields.FirstOrDefault(f => f.Name == "Active");
        FieldValue? fNested = counter1.Fields.FirstOrDefault(f => f.Name == "Nested");
        if (fCount is null || !Equals(fCount.Value.RawValue, 42))
        { Console.Error.WriteLine($"FALSIFIED (phase A): Count missing or != 42 (got {fCount?.RawValue})."); return 8; }
        if (fLabel is null || fLabel.Value.StringValue != "hello")
        { Console.Error.WriteLine($"FALSIFIED (phase A): Label missing or StringValue != \"hello\" (got \"{fLabel?.StringValue ?? "(null)"}\")."); return 8; }
        if (fActive is null || !Equals(fActive.Value.RawValue, true))
        { Console.Error.WriteLine($"FALSIFIED (phase A): Active missing or != true (got {fActive?.RawValue})."); return 8; }
        if (fNested is null || fNested.Value.Fields is not null)
        { Console.Error.WriteLine($"FALSIFIED (phase A): Nested missing or its Fields was populated at depth=1 (should be null)."); return 8; }

        // ── Phase B: depth=2 — counter.Nested.Fields should contain X=99 ─────────────────────────
        LocalValue counter2 = session.GetLocals(depth: 2).FirstOrDefault(l => l.Name == LocalName);
        FieldValue? fNested2 = counter2.Fields?.FirstOrDefault(f => f.Name == "Nested");
        FieldValue? fX = fNested2?.Fields?.FirstOrDefault(f => f.Name == "X");
        Console.WriteLine($"phase B    : counter.Nested.Fields = {fNested2?.Fields?.Count.ToString() ?? "(null)"} entries; X = {(fX?.RawValue is { } _x ? Convert.ToString(_x, CultureInfo.InvariantCulture) : "(missing)")}");
        if (fNested2 is null || fNested2.Value.Fields is null || fX is null || !Equals(fX.Value.RawValue, 99))
        {
            Console.Error.WriteLine($"FALSIFIED (phase B): expected Nested.Fields with X=99, got fNested2.Fields={fNested2?.Fields?.Count.ToString() ?? "(null)"}, X={fX?.RawValue}.");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 39 PASSED — object field walking: depth=1 surfaces counter's primitive+string+bool+nested fields; depth=2 recurses into Nested.X.");
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
        string path = Path.Combine(dir, $"39-fields-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 39 fixture — object field walking (GetFieldValue@8 + EnumFields@20, depth >= 2)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"phases           = A depth=1 populates counter.Fields; B depth=2 recurses into Nested\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
