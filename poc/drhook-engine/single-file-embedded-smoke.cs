#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe — SOURCE BREAKPOINT in a DebugType=embedded single-file app
// ==============================================================================
//
// Substrate claim: DrHook can set + hit a source breakpoint, and resolve local + argument names, in a
// managed single-file app published with DebugType=embedded — where the Portable PDB is EMBEDDED in
// the bundled assembly (NO sidecar .pdb, NO on-disk PE). DrHook reads the loaded module's PE image
// from target memory (ModuleImage.Read: ICorDebugModule base+size, ICorDebugProcess.ReadMemory) and
// extracts the embedded PDB (SymbolReader.TryOpenEmbeddedFromImage, PEStreamOptions.IsLoadedImage).
// Argument names come from the loaded module's metadata (IMetaDataImport), as for the portable case.
//
// Construction: publish single-file-target with -p:DebugType=embedded (assert no sidecar .pdb),
// launch the apphost, take the Debugger.Break setup stop, arm a source breakpoint at SF_BREAK, resume,
// and assert it hits with local 'doubled'==14 (from the memory-extracted embedded PDB) and argument
// 'seed'==7 (from IMetaDataImport).
//
// Falsification: 2 publish/marker problem or a sidecar appeared (not the embedded shape); 4 Launch
//   threw; 5 no setup stop; 6 breakpoint did not bind (embedded PDB not extracted from memory);
//   7 did not hit; 8 local/argument not resolved; 0 PASS.
//
// Usage:  dotnet run --no-cache single-file-embedded-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkyOmega.DrHook.Engine;

return SingleFileEmbedded.Run();

sealed class SilentSink : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnLog(LogRecord record) { }
}

static class SingleFileEmbedded
{
    const string Proj   = "single-file-target/single-file-target.csproj";
    const string Source = "single-file-target/Program.cs";
    const string PubDir = "single-file-target/pubembed";
    const string Apphost = "sfprobe";

    public static int Run()
    {
        string source = Path.GetFullPath(Source);
        if (!File.Exists(source) || !File.Exists(Path.GetFullPath(Proj)))
        { Console.Error.WriteLine("FALSIFIED (missing target). Run from poc/drhook-engine."); return 2; }

        int sfBreak = MarkerLine(source, "SF_BREAK");
        if (sfBreak < 0) { Console.Error.WriteLine("FALSIFIED (marker not found)."); return 2; }

        string rid = RuntimeInformation.RuntimeIdentifier;
        Console.WriteLine($"publishing single-file (DebugType=embedded, {rid}) ...");
        if (!Publish(rid)) { Console.Error.WriteLine("FALSIFIED (publish failed)."); return 2; }
        string apphost = Path.GetFullPath(Path.Combine(PubDir, Apphost));
        if (!File.Exists(apphost)) { Console.Error.WriteLine($"FALSIFIED (no apphost): {apphost}"); return 2; }
        if (File.Exists(Path.ChangeExtension(apphost, ".pdb")))
        { Console.Error.WriteLine("FALSIFIED: a sidecar .pdb is present — not the embedded shape."); return 2; }
        Console.WriteLine($"apphost    : {apphost}");
        Console.WriteLine("sidecar pdb: ABSENT (PDB is embedded in the bundled assembly)");

        var sink = new SilentSink();
        DebugSession session;
        try { session = DebugSession.Launch(apphost, Array.Empty<string>(), Path.GetDirectoryName(apphost), sink); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 4; }
        Console.WriteLine($"launched   : pid {session.ProcessId}");

        int code = Drive(session, source, sfBreak);
        try { session.Dispose(); } catch { }
        return code;
    }

    static int Drive(DebugSession session, string source, int sfBreak)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break)
        { Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup?.Reason.ToString() ?? "timeout")}."); return 5; }
        Console.WriteLine("setup stop : Debugger.Break (ICorDebug attached to the single-file bundle)");

        int bp = session.SetBreakpointAtLine(source, sfBreak);
        if (bp == 0) { Console.Error.WriteLine("FALSIFIED (binding): breakpoint did not bind — embedded PDB not extracted from target memory."); return 6; }
        Console.WriteLine($"bound      : bp id={bp} at Program.cs:{sfBreak} (via the memory-extracted embedded PDB)");

        session.Resume();
        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (hit is null || hit.Reason != StopReason.Breakpoint)
        { Console.Error.WriteLine($"FALSIFIED (hit): {(hit?.Reason.ToString() ?? "timeout")}."); return 7; }

        List<LocalValue> locals = session.GetLocals().ToList();
        List<ArgumentValue> args = session.GetArguments().ToList();
        Console.WriteLine($"HIT        : args=[{string.Join(", ", args.Select(a => $"{a.Name}={a.RawValue}"))}]  locals=[{string.Join(", ", locals.Select(l => $"{l.Name}={l.RawValue}"))}]");

        if (!locals.Any(l => l.Name == "doubled" && l.RawValue is int v && v == 14))
        { Console.Error.WriteLine("FALSIFIED: local 'doubled'==14 not resolved from the embedded PDB."); return 8; }
        if (!args.Any(a => a.Name == "seed" && a.RawValue is int s && s == 7))
        { Console.Error.WriteLine($"FALSIFIED: argument 'seed'==7 not resolved; got [{string.Join(", ", args.Select(a => a.Name))}]."); return 8; }

        Console.WriteLine("\nPROBE PASSED — DebugType=embedded single-file: source breakpoint BOUND + HIT from the memory-extracted embedded PDB; local 'doubled'=14 (embedded PDB) + argument 'seed'=7 (IMetaDataImport). No sidecar, no on-disk PE.");
        return 0;
    }

    static bool Publish(string rid)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"publish \"{Path.GetFullPath(Proj)}\" -c Debug -r {rid} --self-contained false " +
            $"-p:PublishSingleFile=true -p:PublishAot=false -p:DebugType=embedded -o \"{Path.GetFullPath(PubDir)}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using Process p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) { Console.Error.WriteLine(outp); Console.Error.WriteLine(err); return false; }
        return true;
    }

    static int MarkerLine(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal)) return i + 1;
        return -1;
    }
}
