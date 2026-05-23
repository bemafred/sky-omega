// Unit tests for the ring-buffer sink — the default destination for logpoint output (finding 35).
// In-process, deterministic; live policy integration is covered by probes 28/30.

using System.Threading.Tasks;
using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class BoundedLogSinkTests
{
    private static LogRecord Rec(int i) => new(DateTimeOffset.UnixEpoch.AddMilliseconds(i), $"m{i}");

    [Fact]
    public void Drain_OnEmptySink_ReturnsEmptyAndZeroDropped()
    {
        var sink = new BoundedLogSink(4);
        DrainResult r = sink.Drain();
        Assert.Empty(r.Records);
        Assert.Equal(0, r.Dropped);
    }

    [Fact]
    public void Drain_BelowCapacity_ReturnsAllInOrder_NoDrops()
    {
        var sink = new BoundedLogSink(4);
        sink.OnLog(Rec(1)); sink.OnLog(Rec(2)); sink.OnLog(Rec(3));
        DrainResult r = sink.Drain();
        Assert.Equal(new[] { "m1", "m2", "m3" }, r.Records.Select(x => x.Message).ToArray());
        Assert.Equal(0, r.Dropped);
    }

    [Fact]
    public void Drain_ExactlyAtCapacity_ReturnsAll_NoDrops()
    {
        var sink = new BoundedLogSink(3);
        sink.OnLog(Rec(1)); sink.OnLog(Rec(2)); sink.OnLog(Rec(3));
        DrainResult r = sink.Drain();
        Assert.Equal(3, r.Records.Count);
        Assert.Equal(0, r.Dropped);
    }

    [Fact]
    public void Drain_OverCapacity_KeepsNewest_DropsOldest()
    {
        var sink = new BoundedLogSink(3);
        for (int i = 1; i <= 5; i++) sink.OnLog(Rec(i)); // m1, m2 dropped; m3, m4, m5 retained
        DrainResult r = sink.Drain();
        Assert.Equal(new[] { "m3", "m4", "m5" }, r.Records.Select(x => x.Message).ToArray());
        Assert.Equal(2, r.Dropped);
    }

    [Fact]
    public void Drain_ResetsDroppedCounter_BetweenDrains()
    {
        var sink = new BoundedLogSink(2);
        for (int i = 1; i <= 5; i++) sink.OnLog(Rec(i)); // 3 dropped, 2 retained
        Assert.Equal(3, sink.Drain().Dropped);
        Assert.Equal(0, sink.Drain().Dropped); // counter cleared by previous drain
    }

    [Fact]
    public void Drain_ClearsTheBuffer()
    {
        var sink = new BoundedLogSink(4);
        sink.OnLog(Rec(1)); sink.OnLog(Rec(2));
        Assert.Equal(2, sink.Drain().Records.Count);
        Assert.Empty(sink.Drain().Records);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void OnEvent_IsIgnored_NoRecordsAdded()
    {
        var sink = new BoundedLogSink(4);
        sink.OnEvent("CreateProcess");
        sink.OnEvent("LoadModule");
        Assert.Equal(0, sink.Count);
        Assert.Empty(sink.Drain().Records);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLogSink(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLogSink(-1));
    }

    [Fact]
    public async Task OnLog_IsThreadSafe_TotalAppendedEqualsKeptPlusDropped()
    {
        const int capacity = 64;
        const int producers = 8;
        const int perProducer = 1000;
        var sink = new BoundedLogSink(capacity);

        Task[] tasks = new Task[producers];
        for (int p = 0; p < producers; p++)
        {
            int start = p * perProducer;
            tasks[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++) sink.OnLog(Rec(start + i));
            });
        }
        await Task.WhenAll(tasks);

        DrainResult r = sink.Drain();
        Assert.Equal(producers * perProducer, r.Records.Count + (int)r.Dropped);
        Assert.True(r.Records.Count <= capacity);
    }
}
