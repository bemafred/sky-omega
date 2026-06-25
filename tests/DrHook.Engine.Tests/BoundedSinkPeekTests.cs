// ADR-012 Phase 1: a debug-state snapshot must read the stream buffers WITHOUT consuming them — the drain
// tools (drhook_drain_console/_log/_anomalies) still own those records. Peek mirrors Drain but leaves the
// buffer and the dropped counter intact, so a snapshot and a later drain both see the same records.

using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class BoundedSinkPeekTests
{
    [Fact]
    public void ConsoleSink_Peek_IsNonDestructive_AndRepeatable()
    {
        var sink = new BoundedConsoleSink(4);
        sink.OnConsoleOutput(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "a"));
        sink.OnConsoleOutput(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stderr, "b"));

        // Two Peeks in a row return the same records; the buffer is untouched.
        Assert.Equal(new[] { "a", "b" }, sink.Peek().Records.Select(r => r.Text).ToArray());
        Assert.Equal(new[] { "a", "b" }, sink.Peek().Records.Select(r => r.Text).ToArray());
        Assert.Equal(2, sink.Count);

        // A subsequent Drain still sees them (Peek consumed nothing), then the buffer is empty.
        Assert.Equal(new[] { "a", "b" }, sink.Drain().Records.Select(r => r.Text).ToArray());
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void ConsoleSink_Peek_ReportsDropped_WithoutResettingTheCounter()
    {
        var sink = new BoundedConsoleSink(2);
        for (int i = 0; i < 5; i++)
            sink.OnConsoleOutput(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, $"c{i}")); // 3 dropped

        Assert.Equal(3, sink.Peek().Dropped);
        Assert.Equal(3, sink.Peek().Dropped); // Peek does not reset the dropped counter
        Assert.Equal(3, sink.Drain().Dropped); // Drain reports it, then resets
        Assert.Equal(0, sink.Peek().Dropped);
    }

    [Fact]
    public void LogSink_Peek_IsNonDestructive()
    {
        var sink = new BoundedLogSink(4);
        sink.OnLog(new LogRecord(DateTimeOffset.UnixEpoch, "m1"));
        sink.OnLog(new LogRecord(DateTimeOffset.UnixEpoch, "m2"));

        Assert.Equal(new[] { "m1", "m2" }, sink.Peek().Records.Select(r => r.Message).ToArray());
        Assert.Equal(2, sink.Count);
        Assert.Equal(2, sink.Drain().Records.Count); // still there after Peek
    }

    [Fact]
    public void AnomalySink_Peek_IsNonDestructive()
    {
        var sink = new BoundedAnomalySink(4);
        sink.OnAnomaly(new EngineAnomaly(DateTimeOffset.UnixEpoch, AnomalyKind.DepthClamped, "t", "op", "obs", "exp"));

        Assert.Single(sink.Peek().Anomalies);
        Assert.Equal(1, sink.Count);
        Assert.Single(sink.Drain().Anomalies);
        Assert.Equal(0, sink.Count);
    }
}
