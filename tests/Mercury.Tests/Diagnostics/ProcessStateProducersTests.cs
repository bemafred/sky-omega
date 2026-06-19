using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

/// <summary>
/// ADR-035 Phase 7a.2 (Category G): process-level state producers. Each producer is a
/// closure-captured sampler invoked on the periodic timer; tests verify each produces
/// the expected event type and the closure-state behaves correctly across ticks.
/// </summary>
public class ProcessStateProducersTests
{
    private sealed class CapturingListener : IObservabilityListener
    {
        public readonly List<GcEvent> Gc = new();
        public readonly List<LohDeltaEvent> Loh = new();
        public readonly List<RssState> Rss = new();
        public readonly List<DiskFreeState> Disk = new();
        private readonly object _gate = new();

        public void OnGcEvent(in GcEvent ev) { lock (_gate) Gc.Add(ev); }
        public void OnLohDelta(in LohDeltaEvent ev) { lock (_gate) Loh.Add(ev); }
        public void OnRssState(in RssState s) { lock (_gate) Rss.Add(s); }
        public void OnDiskFreeState(in DiskFreeState s) { lock (_gate) Disk.Add(s); }
    }

    [Fact]
    public void Rss_Producer_EmitsCurrentWorkingSet()
    {
        using var buffer = new MemoryStream();
        // Drive the producer synchronously (no timer) so repeated emission is
        // deterministic instead of racing a 50 ms timer against a fixed sleep — and
        // so the reader never shares the non-thread-safe MemoryStream with a live tick.
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true,
            flushInterval: TimeSpan.Zero);
        jsonl.RegisterStateProducer(ProcessStateProducers.Rss());

        jsonl.EmitStateSample();
        jsonl.EmitStateSample();
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        int rssCount = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.Contains("\"phase\":\"rss\"")) rssCount++;
        Assert.Equal(2, rssCount);
    }

    [Fact]
    public void Loh_Producer_TracksAllocationDeltas()
    {
        var captured = new CapturingListener();
        var producer = ProcessStateProducers.Loh();

        // Wrap the capturing listener in a real JsonlMetricsListener to pass to the
        // producer (the StateProducer signature takes JsonlMetricsListener). Use a
        // synthetic in-memory listener under the hood.
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        // First tick: establishes baseline.
        producer(jsonl);

        // Allocate a noticeable chunk of memory.
        var byteArray = new byte[10_000_000];
        for (int i = 0; i < byteArray.Length; i += 4096) byteArray[i] = (byte)i;

        producer(jsonl);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        int lohRecords = 0;
        long latestDelta = 0;
        long latestTotal = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.Contains("\"phase\":\"loh_delta\"")) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            latestDelta = doc.RootElement.GetProperty("delta_bytes").GetInt64();
            latestTotal = doc.RootElement.GetProperty("total_allocated_bytes").GetInt64();
            lohRecords++;
        }
        Assert.Equal(2, lohRecords);
        Assert.True(latestDelta >= 10_000_000,
            $"second-tick delta {latestDelta} should reflect the 10MB allocation");
        Assert.True(latestTotal >= 10_000_000,
            $"second-tick total {latestTotal} should be at least the allocation size");

        // Keep the array alive across the producer call so the JIT can't elide it.
        GC.KeepAlive(byteArray);
    }

    [Fact]
    public void Gc_Producer_EmitsAfterGcAdvancesIndex()
    {
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);
        var producer = ProcessStateProducers.Gc();

        // Initial tick captures the current GC index.
        producer(jsonl);

        // Force a GC to advance the index, then tick again.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        producer(jsonl);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        int gcRecords = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.Contains("\"phase\":\"gc\"")) gcRecords++;

        // At least one record from the post-GC tick (the initial tick may or may not
        // emit depending on whether any GC ran before the test).
        Assert.True(gcRecords >= 1, $"expected ≥ 1 gc event after forced collection; saw {gcRecords}");
    }

    [Fact]
    public void DiskFree_Producer_EmitsForValidPath()
    {
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);
        var producer = ProcessStateProducers.DiskFree(Path.GetTempPath());
        producer(jsonl);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var line = reader.ReadLine();
        Assert.NotNull(line);
        Assert.Contains("\"phase\":\"disk_free\"", line!);

        using var doc = System.Text.Json.JsonDocument.Parse(line!);
        Assert.True(doc.RootElement.GetProperty("free_bytes").GetInt64() >= 0);
        Assert.True(doc.RootElement.GetProperty("total_bytes").GetInt64() > 0);
    }

    [Fact]
    public void DiskFree_Producer_SwallowsErrorsForBadPath()
    {
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);
        var producer = ProcessStateProducers.DiskFree("/path/that/definitely/does/not/exist/xyz");

        // Must not throw.
        producer(jsonl);
        jsonl.Flush();

        // No record emitted is acceptable for a bad path; emitting one with zero values
        // is also acceptable. The contract is "best-effort, never break the timer."
        // Verify only that nothing threw.
    }

    [Fact]
    public void RegisterAll_RegistersFourProducers()
    {
        using var buffer = new MemoryStream();
        // No background timers: drive the producers with one synchronous EmitStateSample
        // so the assertions depend on a guaranteed single tick rather than wall-clock
        // timing. The timer-based version raced the reader on the (non-thread-safe)
        // MemoryStream and could starve under parallel test load.
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true,
            flushInterval: TimeSpan.Zero);
        ProcessStateProducers.RegisterAll(jsonl, diskPath: Path.GetTempPath());

        // Force a GC so the gc producer's index advances past its -1 baseline and emits.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        jsonl.EmitStateSample();
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var phasesObserved = new HashSet<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("phase", out var p))
                phasesObserved.Add(p.GetString()!);
        }

        // RegisterAll wires up all four Category G producers; one sample emits each once.
        Assert.Contains("gc", phasesObserved);
        Assert.Contains("loh_delta", phasesObserved);
        Assert.Contains("rss", phasesObserved);
        Assert.Contains("disk_free", phasesObserved);
    }
}
