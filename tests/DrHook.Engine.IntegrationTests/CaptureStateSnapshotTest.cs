// Layer 1 — INTEGRATION TEST promoting probe debugstate-snapshot-smoke.cs (ADR-012 Phase 1).
//
// Validates DebugSession.CaptureState assembles ONE self-contained DebugStateSnapshot at a real
// breakpoint stop: session / lifecycle + execution position (stop, a >=2-frame call stack topped by
// Worker.Compute, named locals + arguments) + the armed breakpoint + the three stream tails read
// NON-destructively via Peek. Also asserts a DebugStateTapSink observes the unified delta stream as a
// peer consumer on the same CompositeEventSink (a new view is "just another IDebugEventSink").
//
// Launch + the entry-module hold-gate stop the target pre-main — no Debugger.Break in the target. The
// target is a build-dependency sibling artifact, so its PDB always matches its source (a line breakpoint
// at the SNAPSHOT_HERE marker is safe). macOS/arm64 today (ADR-007 Phase 9 = cross-platform).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class CaptureStateSnapshotTest
{
    private const string EntryModule = "DrHookSnapshotTarget";
    private const string MarkerToken = "SNAPSHOT_HERE";

    [TestMethod]
    public void CaptureState_AtBreakpoint_AssemblesSelfContainedSnapshot()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        int markerLine = FindMarker(IntegrationTargetPaths.SnapshotTargetSource(), MarkerToken);
        Assert.IsTrue(markerLine > 0, $"'{MarkerToken}' marker not found in the snapshot target source.");

        // The host composes the per-channel bounded sinks + a DebugStateTapSink behind ONE CompositeEventSink.
        var console = new BoundedConsoleSink(512);
        var logs = new BoundedLogSink(512);
        var anomalies = new BoundedAnomalySink(256);
        var tap = new DebugStateTapSink(1024);
        var sink = new CompositeEventSink(anomalies, logs, console, tap);

        DebugSession session = DebugSession.Launch(ResolveDotnet(), new[] { targetDll }, workingDirectory: null, sink: sink, entryModule: EntryModule);
        int pid = session.ProcessId;
        try
        {
            StopInfo? hold = session.WaitForStop(TimeSpan.FromSeconds(20));
            Assert.IsNotNull(hold, "No hold-gate stop within 20s.");
            Assert.AreEqual(StopReason.EntryModuleLoaded, hold!.Reason, "First stop should be the entry-module hold-gate.");

            int bpId = session.SetBreakpointAtLine(EntryModule, "Program.cs", markerLine);
            Assert.AreNotEqual(0, bpId, $"Failed to bind a line breakpoint at Program.cs:{markerLine}.");
            session.Resume();

            StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(20));
            Assert.IsNotNull(hit, "No breakpoint stop within 20s.");
            Assert.AreEqual(StopReason.Breakpoint, hit!.Reason, "Expected a Breakpoint stop.");

            // ── THE TEST: one CaptureState call yields a self-contained snapshot ──
            DebugStateSnapshot snap = session.CaptureState(hit, console.Peek(), logs.Peek(), anomalies.Peek());

            // session / lifecycle — identity + disposition with no prior context
            Assert.AreEqual(pid, snap.Session.ProcessId);
            Assert.IsTrue(snap.Session.OwnsTarget, "Launch yields an Owned session.");
            Assert.AreEqual(ExecutionState.Stopped, snap.Session.Execution);
            Assert.IsFalse(snap.Session.IsDetached);
            Assert.IsFalse(snap.Session.IsDisposed);

            // execution position — a >=2-frame stack topped by Worker.Compute, with named locals + args
            Assert.AreEqual(StopReason.Breakpoint, snap.Position.Stop!.Reason);
            Assert.IsTrue(snap.Position.CallStack.Count >= 2, $"Expected >=2 frames; got {snap.Position.CallStack.Count}.");
            Assert.IsTrue(snap.Position.TopFrame!.Contains("Compute", StringComparison.Ordinal), $"Top frame should be Worker.Compute; got '{snap.Position.TopFrame}'.");

            // ...and the top frame's STRUCTURED source location resolves live from the target's PDB
            // (ADR-012 Phase-2 enrichment): the FULL source path + the exact stopped line, not just the
            // abbreviated display string. The line must equal the bound breakpoint line (asserted below).
            FrameLocation top = snap.Position.CallStack[0];
            Assert.IsNotNull(top.File, "Top frame should carry a resolved source file path (PDB present).");
            Assert.IsTrue(top.File!.EndsWith("Program.cs", StringComparison.Ordinal), $"Top frame file should be Program.cs; got '{top.File}'.");
            Assert.AreEqual(markerLine, top.Line, $"Top frame line should be the SNAPSHOT_HERE marker line {markerLine}; got {top.Line?.ToString() ?? "(null)"}.");

            LocalValue doubled = snap.Position.Locals.FirstOrDefault(l => l.Name == "doubled");
            LocalValue contribution = snap.Position.Locals.FirstOrDefault(l => l.Name == "contribution");
            Assert.IsTrue(Equals(doubled.RawValue, 2), $"local doubled (beat=1: n*2) should be 2; got {doubled.RawValue ?? "(null)"}.");
            Assert.IsTrue(Equals(contribution.RawValue, 6L), $"local contribution (doubled + \"tick\".Length) should be 6; got {contribution.RawValue ?? "(null)"}.");

            ArgumentValue n = snap.Position.Arguments.FirstOrDefault(a => a.Name == "n");
            Assert.IsTrue(Equals(n.RawValue, 1), $"arg n (first beat) should be 1; got {n.RawValue ?? "(null)"}.");
            Assert.IsTrue(snap.Position.Arguments.Any(a => a.Name == "label"), "arg label should be present.");

            // the receiver `this` resolves its runtime type name live from the target's metadata
            // (substrate completeness, 2026-06-27) — a non-null object, expandable, typed Worker.
            ArgumentValue self = snap.Position.Arguments.FirstOrDefault(a => a.Name == "this");
            Assert.AreEqual("Worker", self.TypeName, $"arg this should report its runtime type Worker; got '{self.TypeName ?? "(null)"}'.");
            Assert.IsTrue(self.HasChildren, "this (a non-null object) should be expandable.");

            // the armed breakpoint travels in the snapshot, at its line
            BreakpointStatus bp = snap.Breakpoints.Single(b => b.Info.Id == bpId);
            Assert.AreEqual(markerLine, ((LineBreakpointInfo)bp.Info).Line);

            // the tap observed the unified delta stream as a peer consumer (lifecycle events fired)
            Assert.IsTrue(tap.Peek().Deltas.Any(d => d.Kind == DebugStateDeltaKind.Event), "The tap should have captured lifecycle deltas.");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    private static int FindMarker(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    private static string ResolveDotnet()
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
