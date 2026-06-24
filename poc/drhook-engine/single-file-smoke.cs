#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe — SOURCE BREAKPOINT in a managed PublishSingleFile app
// ==========================================================================
//
// Substrate claim: DrHook can set + hit a source breakpoint in a managed single-file deployment
// (PublishSingleFile=true, NOT NativeAOT). The app's assembly loads from the bundle, so ICorDebug
// reports its module path as a bare NAME with no on-disk PE — SymbolReader.TryOpen fails. The fix:
// DebugSession falls back to the sidecar <imageDir>/<module>.pdb next to the apphost (where
// DebugType=portable drops it), via SymbolReader.TryOpenPdb. Local names resolve from that PDB.
// Argument NAMES come from the assembly Param table (inside the bundle, not the PDB) — read from the
// LOADED module's metadata via ICorDebug IMetaDataImport (MetadataResolver.ArgumentNames).
//
// Construction: publish single-file-target as a framework-dependent single file, launch the apphost
// directly (it starts CoreCLR — ICorDebug attaches), take the Debugger.Break setup stop, arm a source
// breakpoint at SF_BREAK, resume, and assert it hits with the local 'doubled' == 14 from the sidecar PDB.
//
// Falsification: 2 publish/marker problem; 4 Launch threw; 5 no setup stop; 6 breakpoint did not bind;
//   7 did not hit / wrong reason; 8 local 'doubled'==14 not resolved from the sidecar PDB; 0 PASS.
//
// Usage:  dotnet run --no-cache single-file-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkyOmega.DrHook.Engine;

return SingleFile.Run();

sealed class SilentSink : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnLog(LogRecord record) { }
}

static class SingleFile
{
    const string Proj   = "single-file-target/single-file-target.csproj";
    const string Source = "single-file-target/Program.cs";
    const string PubDir = "single-file-target/pub";
    const string Apphost = "sfprobe";

    public static int Run()
    {
        string source = Path.GetFullPath(Source);
        if (!File.Exists(source) || !File.Exists(Path.GetFullPath(Proj)))
        { Console.Error.WriteLine("FALSIFIED (missing target). Run from poc/drhook-engine."); return 2; }

        int sfBreak = MarkerLine(source, "SF_BREAK");
        if (sfBreak < 0) { Console.Error.WriteLine("FALSIFIED (marker not found)."); return 2; }

        string rid = RuntimeInformation.RuntimeIdentifier;
        Console.WriteLine($"publishing single-file ({rid}) ...");
        if (!Publish(rid)) { Console.Error.WriteLine("FALSIFIED (publish failed)."); return 2; }
        string apphost = Path.GetFullPath(Path.Combine(PubDir, Apphost));
        if (!File.Exists(apphost)) { Console.Error.WriteLine($"FALSIFIED (no apphost): {apphost}"); return 2; }
        Console.WriteLine($"apphost    : {apphost}");
        Console.WriteLine($"sidecar pdb: {(File.Exists(Path.ChangeExtension(apphost, ".pdb")) ? "present" : "ABSENT")}");

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
        { Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup?.Reason.ToString() ?? "timeout")} — ICorDebug did not attach to the bundle."); return 5; }
        Console.WriteLine("setup stop : Debugger.Break (ICorDebug attached to the single-file bundle)");

        int bp = session.SetBreakpointAtLine(source, sfBreak);
        if (bp == 0) { Console.Error.WriteLine("FALSIFIED (binding): source breakpoint did not bind in the single-file app."); return 6; }
        Console.WriteLine($"bound      : bp id={bp} at Program.cs:{sfBreak} (via the sidecar-PDB fallback)");

        session.Resume();
        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (hit is null || hit.Reason != StopReason.Breakpoint)
        { Console.Error.WriteLine($"FALSIFIED (hit): {(hit?.Reason.ToString() ?? "timeout")} — breakpoint never fired."); return 7; }

        List<LocalValue> locals = session.GetLocals().ToList();
        List<ArgumentValue> args = session.GetArguments().ToList();
        Console.WriteLine($"HIT        : args=[{string.Join(", ", args.Select(a => $"{a.Name}={a.RawValue}"))}]  locals=[{string.Join(", ", locals.Select(l => $"{l.Name}={l.RawValue}"))}]");

        if (!locals.Any(l => l.Name == "doubled" && l.RawValue is int v && v == 14))
        { Console.Error.WriteLine("FALSIFIED: local 'doubled'==14 not resolved from the sidecar PDB."); return 8; }
        if (!args.Any(a => a.Name == "seed" && a.RawValue is int s && s == 7))
        { Console.Error.WriteLine($"FALSIFIED: argument 'seed'==7 not resolved from the loaded-module metadata; got [{string.Join(", ", args.Select(a => a.Name))}]."); return 8; }

        Console.WriteLine("\nPROBE PASSED — managed single-file: source breakpoint BOUND + HIT; local 'doubled'=14 from the sidecar PDB; argument 'seed'=7 from the loaded-module metadata (IMetaDataImport). Full single-file inspection works.");
        return 0;
    }

    static bool Publish(string rid)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"publish \"{Path.GetFullPath(Proj)}\" -c Debug -r {rid} --self-contained false " +
            $"-p:PublishSingleFile=true -p:PublishAot=false -p:DebugType=portable -o \"{Path.GetFullPath(PubDir)}\"")
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
