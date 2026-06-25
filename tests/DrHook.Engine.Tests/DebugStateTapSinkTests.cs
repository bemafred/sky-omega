// ADR-012 Phase 1: the delta-stream tap. A new consumer is "just another IDebugEventSink" — added to the
// existing CompositeEventSink it observes the WHOLE event stream (all four channels, including OnEvent, which
// no per-channel sink buffers) without disturbing the per-channel bounded sinks. In-process, deterministic.

using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class DebugStateTapSinkTests
{
    [Fact]
    public void Tap_CapturesAllFourChannels_AsOneOrderedStream()
    {
        var tap = new DebugStateTapSink(16);
        tap.OnEvent("Breakpoint");
        tap.OnLog(new LogRecord(DateTimeOffset.UnixEpoch, "log1"));
        tap.OnConsoleOutput(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "out"));
        tap.OnAnomaly(new EngineAnomaly(DateTimeOffset.UnixEpoch, AnomalyKind.DepthClamped, "t", "op", "obs", "exp"));

        DebugStateDeltaResult r = tap.Peek();
        Assert.Equal(
            new[] { DebugStateDeltaKind.Event, DebugStateDeltaKind.Log, DebugStateDeltaKind.Console, DebugStateDeltaKind.Anomaly },
            r.Deltas.Select(d => d.Kind).ToArray());
        Assert.Equal("Breakpoint", r.Deltas[0].EventName);
        Assert.Equal("log1", r.Deltas[1].Log!.Message);
        Assert.Equal("out", r.Deltas[2].Console!.Text);
        Assert.Equal(AnomalyKind.DepthClamped, r.Deltas[3].Anomaly!.Kind);
    }

    [Fact]
    public void Tap_IsBounded_DropsOldest_AndCounts()
    {
        var tap = new DebugStateTapSink(2);
        tap.OnEvent("e1"); tap.OnEvent("e2"); tap.OnEvent("e3"); // e1 dropped

        DebugStateDeltaResult r = tap.Peek();
        Assert.Equal(new[] { "e2", "e3" }, r.Deltas.Select(d => d.EventName).ToArray());
        Assert.Equal(1, r.Dropped);
    }

    [Fact]
    public void Tap_NullPayloads_AreIgnored_NotThrown()
    {
        var tap = new DebugStateTapSink(4);
        tap.OnEvent(null!);
        tap.OnLog(null!);
        tap.OnAnomaly(null!);          // the anomaly channel must NEVER throw (WE-OA-1 / finding 60)
        tap.OnConsoleOutput(null!);
        Assert.Equal(0, tap.Count);
    }

    [Fact]
    public void CompositeFanOut_TapSeesTheUnion_PerChannelSinksUndisturbed()
    {
        var console = new BoundedConsoleSink(8);
        var logs = new BoundedLogSink(8);
        var anomalies = new BoundedAnomalySink(8);
        var tap = new DebugStateTapSink(16);
        var composite = new CompositeEventSink(anomalies, logs, console, tap);

        composite.OnEvent("LoadModule");
        composite.OnLog(new LogRecord(DateTimeOffset.UnixEpoch, "L"));
        composite.OnConsoleOutput(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "C"));
        composite.OnAnomaly(new EngineAnomaly(DateTimeOffset.UnixEpoch, AnomalyKind.DepthClamped, "t", "op", "obs", "exp"));

        // Per-channel sinks: each got exactly its own channel, unchanged by the tap's presence.
        Assert.Equal(new[] { "L" }, logs.Peek().Records.Select(r => r.Message).ToArray());
        Assert.Equal(new[] { "C" }, console.Peek().Records.Select(r => r.Text).ToArray());
        Assert.Single(anomalies.Peek().Anomalies);

        // The tap saw the UNION — including OnEvent, which no per-channel sink buffers.
        DebugStateDeltaResult t = tap.Peek();
        Assert.Equal(4, t.Deltas.Count);
        Assert.Contains(t.Deltas, d => d.Kind == DebugStateDeltaKind.Event && d.EventName == "LoadModule");
    }
}
