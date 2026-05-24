// Unit tests for the EngineAnomaly substrate-capture infrastructure (ADR-007 Phase 1, EA-1/2/3).
// In-process, deterministic; live capture-site integration is covered by probe 41 (designed
// anomaly injection) and the Phase 8 IL-size test for the O(1)-thunk rule.

using System.Collections.Generic;
using System.Threading.Tasks;
using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class EngineAnomalyTests
{
    private static EngineAnomaly Anom(int i, AnomalyKind kind = AnomalyKind.DepthClamped) =>
        new(DateTimeOffset.UnixEpoch.AddMilliseconds(i), kind, "pump-worker", $"op{i}", $"observed-{i}", "expected");

    // ──────────────── BoundedAnomalySink — bounded ring buffer ────────────────

    [Fact]
    public void Drain_OnEmptySink_ReturnsEmptyAndZeroDropped()
    {
        var sink = new BoundedAnomalySink(4);
        AnomalyDrainResult r = sink.Drain();
        Assert.Empty(r.Anomalies);
        Assert.Equal(0, r.Dropped);
    }

    [Fact]
    public void Drain_BelowCapacity_ReturnsAllInOrder_NoDrops()
    {
        var sink = new BoundedAnomalySink(4);
        sink.OnAnomaly(Anom(1));
        sink.OnAnomaly(Anom(2));
        sink.OnAnomaly(Anom(3));
        AnomalyDrainResult r = sink.Drain();
        Assert.Equal(new[] { "op1", "op2", "op3" }, r.Anomalies.Select(x => x.Operation).ToArray());
        Assert.Equal(0, r.Dropped);
    }

    [Fact]
    public void Drain_OverCapacity_KeepsNewest_DropsOldest()
    {
        var sink = new BoundedAnomalySink(3);
        for (int i = 1; i <= 5; i++) sink.OnAnomaly(Anom(i)); // op1, op2 dropped
        AnomalyDrainResult r = sink.Drain();
        Assert.Equal(new[] { "op3", "op4", "op5" }, r.Anomalies.Select(x => x.Operation).ToArray());
        Assert.Equal(2, r.Dropped);
    }

    [Fact]
    public void Drain_ResetsDroppedCounter_BetweenDrains()
    {
        var sink = new BoundedAnomalySink(2);
        for (int i = 1; i <= 5; i++) sink.OnAnomaly(Anom(i)); // 3 dropped, 2 retained
        Assert.Equal(3, sink.Drain().Dropped);
        Assert.Equal(0, sink.Drain().Dropped); // counter cleared by previous drain
    }

    [Fact]
    public void Drain_ClearsTheBuffer()
    {
        var sink = new BoundedAnomalySink(4);
        sink.OnAnomaly(Anom(1));
        sink.OnAnomaly(Anom(2));
        Assert.Equal(2, sink.Drain().Anomalies.Count);
        Assert.Empty(sink.Drain().Anomalies);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void OnEvent_AndOnLog_AreIgnored_NoAnomaliesAdded()
    {
        var sink = new BoundedAnomalySink(4);
        sink.OnEvent("CreateProcess");
        sink.OnLog(new LogRecord(DateTimeOffset.UnixEpoch, "log"));
        Assert.Equal(0, sink.Count);
        Assert.Empty(sink.Drain().Anomalies);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAnomalySink(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAnomalySink(-1));
    }

    [Fact]
    public async Task OnAnomaly_IsThreadSafe_TotalAppendedEqualsKeptPlusDropped()
    {
        const int capacity = 64;
        const int producers = 8;
        const int perProducer = 1000;
        var sink = new BoundedAnomalySink(capacity);

        Task[] tasks = new Task[producers];
        for (int p = 0; p < producers; p++)
        {
            int start = p * perProducer;
            tasks[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++) sink.OnAnomaly(Anom(start + i));
            });
        }
        await Task.WhenAll(tasks);

        AnomalyDrainResult r = sink.Drain();
        Assert.Equal(producers * perProducer, r.Anomalies.Count + (int)r.Dropped);
        Assert.True(r.Anomalies.Count <= capacity);
    }

    // ──────────────── EngineAnomaly type — construction shapes ────────────────

    [Fact]
    public void EngineAnomaly_ConstructsWithRequiredFields_ContextIsOptional()
    {
        var a = new EngineAnomaly(DateTimeOffset.UnixEpoch, AnomalyKind.WorkerException,
            "pump-worker", "Pump", "InvalidOperationException", "loop exits cleanly");
        Assert.Equal(AnomalyKind.WorkerException, a.Kind);
        Assert.Equal("pump-worker", a.Thread);
        Assert.Null(a.Context);
    }

    [Fact]
    public void EngineAnomaly_AcceptsContextDictionary()
    {
        var ctx = new Dictionary<string, string> { ["hresult"] = "0x80131c12", ["operation"] = "Detach" };
        var a = new EngineAnomaly(DateTimeOffset.UnixEpoch, AnomalyKind.UnexpectedHResult,
            "mcp-request", "Detach", "HRESULT 0x80131c12", "S_OK", ctx);
        Assert.NotNull(a.Context);
        Assert.Equal("0x80131c12", a.Context!["hresult"]);
    }

    // ──────────────── Default IDebugEventSink.OnAnomaly — no-op for legacy sinks ────────────────

    private sealed class LegacyEventOnlySink : IDebugEventSink
    {
        public int EventCount;
        public void OnEvent(string name) => EventCount++;
        // OnLog and OnAnomaly inherit default no-op
    }

    [Fact]
    public void IDebugEventSink_DefaultOnAnomaly_IsNoOp_LegacySinksUnaffected()
    {
        // A sink predating the EA infrastructure (only OnEvent implemented) must continue to
        // compile and behave correctly. OnAnomaly silently no-ops via the interface default.
        IDebugEventSink sink = new LegacyEventOnlySink();
        var a = new EngineAnomaly(DateTimeOffset.UnixEpoch, AnomalyKind.LateCallback,
            "mscordbi", "OnCallback", "obs", "exp");
        sink.OnAnomaly(a); // no-op; must not throw
        Assert.Equal(0, ((LegacyEventOnlySink)sink).EventCount);
    }

    // ──────────────── BoundedLogSink is unchanged — anomalies don't leak into log channel ──────

    [Fact]
    public void BoundedLogSink_IgnoresAnomalies_OnlyLogsRetained()
    {
        // Reciprocal of the BoundedAnomalySink test — confirms the two sinks honor their
        // single-channel contracts and don't cross-pollute. OnAnomaly via the interface
        // default no-op (BoundedLogSink doesn't override it).
        var sink = new BoundedLogSink(4);
        IDebugEventSink asSink = sink;
        asSink.OnAnomaly(new EngineAnomaly(DateTimeOffset.UnixEpoch, AnomalyKind.DepthClamped,
            "mcp-request", "GetLocals", "depth=99", "depth<=10"));
        Assert.Equal(0, sink.Count);
        Assert.Empty(sink.Drain().Records);
    }
}
