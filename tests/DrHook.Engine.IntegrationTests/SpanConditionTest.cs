// Layer 1 — INTEGRATION TEST for ADR-013 D3 (span-aware expression evaluation), promoting probe
// span-length-condition-smoke.cs.
//
// Validates that a breakpoint CONDITION on a ReadOnlySpan<char> argument compiles AND evaluates at a
// real stop — `value.Length > 5` — instead of faulting. A span is a ref struct: it cannot be passed as
// a func-eval receiver (the runtime cannot box it as the getter's `this`), so the pre-D3 path produced a
// conditionError. D3 reads `value.Length` directly from the span's `_length` field. Two assertions:
//   (1) the conditional breakpoint GATES correctly — it skips the calls where value.Length <= 5 and
//       surfaces a clean Breakpoint stop on the first call where value.Length > 5 (no ConditionError,
//       no fault log). The target cycles the span length 2..7, so the first true hit has length 6.
//   (2) the substrate primitive: TryEvalMemberCall("value", "Length") resolves to 6 at that stop via
//       the field-read path (not func-eval).
//
// Launch + the entry-module hold-gate stop the target pre-main — no Debugger.Break in the target. The
// target is a build-dependency sibling artifact, so its PDB always matches its source (a line breakpoint
// at the SPAN_HERE marker is safe). macOS/arm64 today (ADR-007 Phase 9 = cross-platform).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class SpanConditionTest
{
    private const string EntryModule = "DrHookSnapshotTarget";
    private const string MarkerToken = "SPAN_HERE";

    [TestMethod]
    public void SpanLengthCondition_CompilesEvaluatesAndGates_WithoutFuncEval()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        int markerLine = FindMarker(IntegrationTargetPaths.SnapshotTargetSource(), MarkerToken);
        Assert.IsTrue(markerLine > 0, $"'{MarkerToken}' marker not found in the snapshot target source.");

        var console = new BoundedConsoleSink(512);
        var logs = new BoundedLogSink(512);
        var anomalies = new BoundedAnomalySink(256);
        var sink = new CompositeEventSink(anomalies, logs, console);

        DebugSession session = DebugSession.Launch(ResolveDotnet(), new[] { targetDll }, workingDirectory: null, sink: sink, entryModule: EntryModule);
        int pid = session.ProcessId;
        try
        {
            StopInfo? hold = session.WaitForStop(TimeSpan.FromSeconds(20));
            Assert.IsNotNull(hold, "No hold-gate stop within 20s.");
            Assert.AreEqual(StopReason.EntryModuleLoaded, hold!.Reason, "First stop should be the entry-module hold-gate.");

            // A conditional breakpoint whose condition reads `.Length` on a ReadOnlySpan<char> ARGUMENT.
            // Pre-D3 this faulted (conditionError) because the getter can't be func-eval'd on a ref struct.
            BreakpointPolicy policy = session.Compile(new BreakpointPolicySpec(Condition: "value.Length > 5", Suspend: SuspendPolicy.All));
            int bpId = session.SetBreakpointAtLine(EntryModule, "Program.cs", markerLine, policy);
            Assert.AreNotEqual(0, bpId, $"Failed to bind a conditional line breakpoint at Program.cs:{markerLine}.");
            session.Resume();

            // The condition is evaluated on EVERY hit; lengths 2..5 evaluate FALSE (the breakpoint
            // auto-resumes), and the first call with length 6 evaluates TRUE and surfaces. A surfaced
            // Breakpoint stop therefore proves the condition compiled, gated the length<=5 calls without
            // fault, and fired on length>5 — all via the direct span field read.
            StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(20));
            Assert.IsNotNull(hit, "No conditional breakpoint stop within 20s.");
            Assert.AreEqual(StopReason.Breakpoint, hit!.Reason,
                $"Expected a Breakpoint stop (condition `value.Length > 5` evaluated against the span argument). " +
                $"A ConditionError would mean the span member did not resolve. Got {hit.Reason}.");

            // No condition fault was logged on any of the gated (length<=5) hits.
            Assert.IsFalse(logs.Peek().Records.Any(r => r.IsFault),
                "A condition fault was logged — span member access did not evaluate cleanly on every hit.");

            // The substrate primitive: read `value.Length` directly off the span at this stop. The first
            // length>5 call has span length 6 (the target cycles 2,3,4,5,6,7,...), so this resolves to 6.
            EvalStatus status = session.TryEvalMemberCall("value", "Length", TimeSpan.FromSeconds(10), out ArgumentValue length);
            Assert.AreEqual(EvalStatus.Completed, status, $"TryEvalMemberCall(value.Length) should complete on a span receiver; got {status}.");
            Assert.IsTrue(Equals(length.RawValue, 6),
                $"value.Length at the first `> 5` stop should be 6 (the gated lengths 2..5 were skipped); got {length.RawValue ?? "(null)"}.");
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
