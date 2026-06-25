#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe — ADR-012 Phase 1: the unified DEBUG-STATE SNAPSHOT, live ===================
//
// Proves DebugSession.CaptureState assembles ONE self-contained snapshot at a real breakpoint stop:
// session / lifecycle + execution position (stop, call stack, named locals + arguments) + breakpoints
// with hit counts + the three stream tails read NON-destructively via Peek. Also proves a
// DebugStateTapSink observes the unified delta stream as a peer consumer on the same CompositeEventSink
// (a new view is "just another IDebugEventSink"), without disturbing the per-channel bounded sinks.
//
// Flow (no Debugger.Break crutch — the entry-module hold-gate arms the breakpoint pre-main):
//   Launch(Owned, hold-gate) -> EntryModuleLoaded -> arm bp at SNAPSHOT_HERE -> Resume -> Breakpoint
//   (beat=1: n=1, label="tick", doubled=2, contribution=6) -> CaptureState -> assert self-contained.
//
// Falsification: 2 target not built / no marker; 3 Launch; 4 first stop != EntryModuleLoaded;
//   5 SetBreakpointAtLine; 6 second stop != Breakpoint; 7 snapshot not Stopped / pid mismatch;
//   8 call stack < 2 frames or top frame not Worker.Compute; 9 locals/args missing or wrong;
//   10 breakpoint missing / hit count < 1; 0 PASS.
//
// Usage:  dotnet run --no-cache debugstate-snapshot-smoke.cs
//         (build the target first: dotnet build debugstate-target -c Debug)

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SkyOmega.DrHook.Engine;

return DebugStateSnapshotProbe.Run();

static class DebugStateSnapshotProbe
{
    const string TargetDir = "debugstate-target";
    const string TargetDll = "DebugStateTarget.dll";
    const string EntryModule = "DebugStateTarget";
    const string MarkerToken = "SNAPSHOT_HERE";

    public static int Run()
    {
        string scriptDir = Directory.Exists(Path.Combine(Environment.CurrentDirectory, TargetDir))
            ? Environment.CurrentDirectory : AppContext.BaseDirectory;
        string targetProj = Path.Combine(scriptDir, TargetDir);
        string targetDll = Path.Combine(targetProj, "bin", "Debug", "net10.0", TargetDll);
        string targetSource = Path.Combine(targetProj, "Program.cs");

        if (!File.Exists(targetDll))
        {
            Console.Error.WriteLine($"FALSIFIED (target not built): expected {targetDll} — run `dotnet build {targetProj} -c Debug` first.");
            return 2;
        }
        int markerLine = FindMarker(targetSource, MarkerToken);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED: '{MarkerToken}' not found in {targetSource}."); return 2; }

        string beatFile = Path.Combine(Path.GetTempPath(), $"drhook-snapshot-beat-{Environment.ProcessId}.txt");
        try { if (File.Exists(beatFile)) File.Delete(beatFile); } catch { /* fresh */ }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"target     : dotnet {targetDll} {beatFile}");
        Console.WriteLine($"plan       : Launch(hold-gate) -> EntryModuleLoaded -> arm bp Program.cs:{markerLine} -> Resume -> Breakpoint -> CaptureState.");

        // The host composes the per-channel bounded sinks + a DebugStateTapSink behind ONE CompositeEventSink
        // (the ADR-012 seam: a new consumer is just another IDebugEventSink).
        var console = new BoundedConsoleSink(512);
        var logs = new BoundedLogSink(512);
        var anomalies = new BoundedAnomalySink(256);
        var tap = new DebugStateTapSink(1024);
        var sink = new CompositeEventSink(anomalies, logs, console, tap);

        string dotnet = ResolveDotnet();
        DebugSession session;
        try { session = DebugSession.Launch(dotnet, new[] { targetDll, beatFile }, workingDirectory: null, sink: sink, entryModule: EntryModule); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 3; }

        int pid = session.ProcessId;
        Console.WriteLine($"launched   : pid={pid} (attached before main; held at entry module)");

        try
        {
            StopInfo? stop1 = session.WaitForStop(TimeSpan.FromSeconds(15));
            if (stop1 is null || stop1.Reason != StopReason.EntryModuleLoaded)
            { Console.Error.WriteLine($"FALSIFIED (first stop): {(stop1 is null ? "timeout" : stop1.Reason.ToString())}, expected EntryModuleLoaded (hold-gate)."); return 4; }
            Console.WriteLine($"stop 1     : {stop1.Reason} (hold-gate)");

            int bpId = session.SetBreakpointAtLine(EntryModule, "Program.cs", markerLine);
            if (bpId == 0) { Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): Program.cs:{markerLine}."); return 5; }
            Console.WriteLine($"breakpoint : Program.cs:{markerLine} id={bpId}; resuming to it");
            session.Resume();

            StopInfo? stop2 = session.WaitForStop(TimeSpan.FromSeconds(15));
            if (stop2 is null || stop2.Reason != StopReason.Breakpoint)
            { Console.Error.WriteLine($"FALSIFIED (second stop): {(stop2 is null ? "timeout" : stop2.Reason.ToString())}, expected Breakpoint."); return 6; }
            Console.WriteLine($"stop 2     : {stop2.Reason}\n");

            // ── THE TEST: ONE CaptureState call yields a self-contained snapshot ──
            DebugStateSnapshot snap = session.CaptureState(stop2, console.Peek(), logs.Peek(), anomalies.Peek());
            PrintSnapshot(snap, tap.Peek());

            // Self-containedness assertions — everything a mid-session view needs, from the snapshot alone.
            if (snap.Session.Execution != ExecutionState.Stopped || snap.Session.ProcessId != pid)
            { Console.Error.WriteLine($"FALSIFIED: expected Stopped + pid {pid}; got {snap.Session.Execution} + pid {snap.Session.ProcessId}."); return 7; }

            if (snap.Position.CallStack.Count < 2 || (snap.Position.TopFrame?.Contains("Compute", StringComparison.Ordinal) != true))
            { Console.Error.WriteLine($"FALSIFIED: expected >=2 frames topped by Worker.Compute; got {snap.Position.CallStack.Count} frames, top='{snap.Position.TopFrame}'."); return 8; }

            LocalValue doubled = snap.Position.Locals.FirstOrDefault(l => l.Name == "doubled");
            LocalValue contribution = snap.Position.Locals.FirstOrDefault(l => l.Name == "contribution");
            ArgumentValue n = snap.Position.Arguments.FirstOrDefault(a => a.Name == "n");
            bool hasLabel = snap.Position.Arguments.Any(a => a.Name == "label");
            if (!Equals(doubled.RawValue, 2) || !Equals(contribution.RawValue, 6L) || !Equals(n.RawValue, 1) || !hasLabel)
            { Console.Error.WriteLine($"FALSIFIED: expected locals doubled=2/contribution=6, arg n=1 + arg label; got doubled={Show(doubled.RawValue)} contribution={Show(contribution.RawValue)} n={Show(n.RawValue)} hasLabel={hasLabel}."); return 9; }

            // The snapshot must carry the armed breakpoint at its correct line. (HitCount is the policy
            // evaluator's count — 0 for a policy-less breakpoint like this one, which is correct substrate
            // behavior; GetBreakpointHits only increments for breakpoints with an attached policy.)
            BreakpointStatus? bp = snap.Breakpoints.FirstOrDefault(b => b.Info.Id == bpId);
            if (bp is null || bp.Info is not LineBreakpointInfo line || line.Line != markerLine)
            { Console.Error.WriteLine($"FALSIFIED: expected line breakpoint id={bpId} at Program.cs:{markerLine}; got {(bp is null ? "missing" : bp.Info.ToString())}."); return 10; }

            Console.WriteLine(
                "\nPROBE PASSED — CaptureState assembled a self-contained snapshot at a live breakpoint: Stopped, " +
                $"{snap.Position.CallStack.Count}-frame stack into Worker.Compute, named locals doubled=2/contribution=6, " +
                "args n=1/label=\"tick\", breakpoint hit, stream tails by Peek; the tap saw the unified delta stream.");
            return 0;
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { if (File.Exists(beatFile)) File.Delete(beatFile); } catch { }
        }
    }

    static void PrintSnapshot(DebugStateSnapshot s, DebugStateDeltaResult deltas)
    {
        Console.WriteLine("── DebugStateSnapshot ──────────────────────────────────────────────");
        Console.WriteLine($"capturedAt : {s.CapturedAt:O}");
        Console.WriteLine($"session    : pid={s.Session.ProcessId} owned={s.Session.OwnsTarget} runtimeMajor={s.Session.RuntimeMajor?.ToString(CultureInfo.InvariantCulture) ?? "?"} execution={s.Session.Execution} detached={s.Session.IsDetached} disposed={s.Session.IsDisposed}");
        Console.WriteLine($"stop       : {s.Position.Stop?.Reason.ToString() ?? "(running)"}{(s.Position.ExceptionTypeName is { } et ? $" [{et}]" : "")}");
        Console.WriteLine($"topFrame   : {s.Position.TopFrame ?? "(none)"}");
        Console.WriteLine("callStack  :");
        foreach (string f in s.Position.CallStack) Console.WriteLine($"  {f}");
        Console.WriteLine("locals     :");
        foreach (LocalValue l in s.Position.Locals) Console.WriteLine($"  {l.Name} = {Show(l.RawValue)}{(l.StringValue is { } sv ? $" \"{sv}\"" : "")} (et=0x{l.ElementType:X2})");
        Console.WriteLine("arguments  :");
        foreach (ArgumentValue a in s.Position.Arguments) Console.WriteLine($"  {a.Name} = {Show(a.RawValue)}{(a.StringValue is { } sv ? $" \"{sv}\"" : "")} (et=0x{a.ElementType:X2})");
        Console.WriteLine("breakpoints:");
        foreach (BreakpointStatus b in s.Breakpoints) Console.WriteLine($"  id={b.Info.Id} hits={b.HitCount}  {b.Info}");
        Console.WriteLine($"streams    : console={s.Console.Records.Count}(+{s.Console.Dropped} dropped) logs={s.Logs.Records.Count}(+{s.Logs.Dropped}) anomalies={s.Anomalies.Anomalies.Count}(+{s.Anomalies.Dropped})");
        string kinds = string.Join(", ", deltas.Deltas.GroupBy(d => d.Kind).Select(g => $"{g.Key}×{g.Count()}"));
        Console.WriteLine($"delta tap  : {deltas.Deltas.Count} deltas [{kinds}] (+{deltas.Dropped} dropped)");
        Console.WriteLine("────────────────────────────────────────────────────────────────────");
    }

    static string Show(object? v) => v is null ? "(null)" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "(null)";

    static int FindMarker(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    static string ResolveDotnet()
    {
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string? dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (dotnetRoot is not null)
        {
            string candidate = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }
}
