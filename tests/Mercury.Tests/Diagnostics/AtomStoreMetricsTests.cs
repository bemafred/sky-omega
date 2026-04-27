using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

/// <summary>
/// ADR-035 Phase 7a.3 (Category B): atom-store observability. Discrete events (rehash,
/// file growth) and periodic state samplers (intern rate, load factor, probe-distance
/// percentiles).
/// </summary>
public class AtomStoreMetricsTests : IDisposable
{
    private readonly string _testDir;

    public AtomStoreMetricsTests()
    {
        var tempPath = TempPath.Test("atom_metrics");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private sealed class CapturingListener : IObservabilityListener
    {
        public readonly List<AtomInternRate> Rates = new();
        public readonly List<AtomLoadFactor> LoadFactors = new();
        public readonly List<AtomProbeDistance> ProbeDistances = new();
        public readonly List<AtomRehashEvent> Rehashes = new();
        public readonly List<AtomFileGrowthEvent> FileGrowths = new();
        private readonly object _gate = new();

        public void OnAtomInternRate(in AtomInternRate r) { lock (_gate) Rates.Add(r); }
        public void OnAtomLoadFactor(in AtomLoadFactor lf) { lock (_gate) LoadFactors.Add(lf); }
        public void OnAtomProbeDistance(in AtomProbeDistance d) { lock (_gate) ProbeDistances.Add(d); }
        public void OnAtomRehash(in AtomRehashEvent e) { lock (_gate) Rehashes.Add(e); }
        public void OnAtomFileGrowth(in AtomFileGrowthEvent e) { lock (_gate) FileGrowths.Add(e); }
    }

    [Fact]
    public void OnAtomRehash_FiresWhenLowInitialCapacityForcesGrowth()
    {
        var dir = Path.Combine(_testDir, "rehash");
        Directory.CreateDirectory(dir);
        // 16 buckets × 75% load = rehash on 13th atom. Force the override past the
        // bulk-mode floor so the test reliably triggers rehash.
        var opts = new StorageOptions
        {
            AtomHashTableInitialCapacity = 16,
            ForceAtomHashCapacity = true,
        };
        using var store = new QuadStore(dir, null, null, opts);
        var listener = new CapturingListener();
        store.ObservabilityListener = listener;

        // 30 distinct atoms — at least one rehash is guaranteed.
        for (int i = 0; i < 30; i++)
            store.AddCurrent("s" + i, "p", "o" + i);

        Assert.NotEmpty(listener.Rehashes);
        var first = listener.Rehashes[0];
        Assert.True(first.NewBucketCount > first.OldBucketCount,
            $"new bucket count {first.NewBucketCount} should exceed old {first.OldBucketCount}");
        Assert.True(first.Duration > TimeSpan.Zero);
    }

    [Fact]
    public void OnAtomFileGrowth_FiresWhenDataFileExtends()
    {
        var dir = Path.Combine(_testDir, "growth");
        Directory.CreateDirectory(dir);
        // Tiny initial data file forces growth on the first big atom.
        var opts = new StorageOptions { AtomDataInitialSizeBytes = 4096 };
        using var store = new QuadStore(dir, null, null, opts);
        var listener = new CapturingListener();
        store.ObservabilityListener = listener;

        // Add atoms whose total size exceeds the initial data file capacity.
        var bigLiteral = "\"" + new string('x', 8192) + "\"";
        store.AddCurrent("s1", "p", bigLiteral);
        store.AddCurrent("s2", "p", bigLiteral);

        Assert.NotEmpty(listener.FileGrowths);
        var growth = listener.FileGrowths[0];
        Assert.True(growth.NewLengthBytes > growth.OldLengthBytes);
        Assert.EndsWith(".atoms", growth.FilePath);
    }

    [Fact]
    public void InternRate_TracksDeltaBetweenTicks()
    {
        var dir = Path.Combine(_testDir, "intern_rate");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        var listener = new CapturingListener();
        store.ObservabilityListener = listener;

        var producer = AtomStoreProducers.InternRate(store.Atoms);

        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        // First tick — captures baseline.
        producer(jsonl);

        // Add 100 atoms.
        for (int i = 0; i < 100; i++)
            store.AddCurrent("s" + i, "p", "o" + i);

        // Wait a tick so dt > 0 in the rate calculation.
        Thread.Sleep(10);
        producer(jsonl);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        int rateRecords = 0;
        long lastCumulative = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.Contains("\"phase\":\"atom_intern_rate\"")) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            lastCumulative = doc.RootElement.GetProperty("cumulative_intern").GetInt64();
            rateRecords++;
        }
        Assert.Equal(2, rateRecords);
        // Each AddCurrent atomizes ~3 strings (s, p, o); 100 distinct quads ≈ 201 atoms.
        Assert.True(lastCumulative >= 100,
            $"cumulative {lastCumulative} should reflect at least the 100 added quads");
    }

    [Fact]
    public void LoadFactor_ReflectsAtomToBucketRatio()
    {
        var dir = Path.Combine(_testDir, "lf");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        var listener = new CapturingListener();
        store.ObservabilityListener = listener;

        for (int i = 0; i < 50; i++) store.AddCurrent("s" + i, "p", "o" + i);

        var producer = AtomStoreProducers.LoadFactor(store.Atoms);
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);
        producer(jsonl);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var line = reader.ReadLine();
        Assert.NotNull(line);
        Assert.Contains("\"phase\":\"atom_load_factor\"", line!);
        using var doc = System.Text.Json.JsonDocument.Parse(line!);
        Assert.True(doc.RootElement.GetProperty("atom_count").GetInt64() > 0);
        Assert.True(doc.RootElement.GetProperty("bucket_count").GetInt64() > 0);
        var lf = doc.RootElement.GetProperty("load_factor").GetDouble();
        Assert.True(lf >= 0 && lf <= 1.0, $"load factor {lf} out of [0,1]");
    }

    [Fact]
    public void ProbeDistance_WindowsPercentilesAndResets()
    {
        var dir = Path.Combine(_testDir, "probe");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        var listener = new CapturingListener();
        store.ObservabilityListener = listener;

        for (int i = 0; i < 50; i++) store.AddCurrent("s" + i, "p", "o" + i);
        // Also do lookups that exercise the InternUtf8 path with non-trivial probe.
        for (int i = 0; i < 50; i++) store.AddCurrent("s" + i, "p", "o" + i);

        Assert.NotNull(store.Atoms.ProbeDistanceHistogram);
        Assert.True(store.Atoms.ProbeDistanceHistogram!.Count > 0);

        var producer = AtomStoreProducers.ProbeDistance(store.Atoms);
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);
        producer(jsonl);
        jsonl.Flush();

        // After producer ran, histogram should be reset.
        Assert.Equal(0, store.Atoms.ProbeDistanceHistogram!.Count);

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var line = reader.ReadLine();
        Assert.NotNull(line);
        Assert.Contains("\"phase\":\"atom_probe_distance\"", line!);
        using var doc = System.Text.Json.JsonDocument.Parse(line!);
        Assert.True(doc.RootElement.GetProperty("samples").GetInt64() > 0);
    }

    [Fact]
    public async Task RegisterAll_FromQuadStore_EmitsAllThreeStateRecordsViaTimer()
    {
        var dir = Path.Combine(_testDir, "register_all");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true,
            stateEmissionInterval: TimeSpan.FromMilliseconds(50));
        store.ObservabilityListener = jsonl;
        store.RegisterAtomStateProducers(jsonl);

        for (int i = 0; i < 30; i++) store.AddCurrent("s" + i, "p", "o" + i);

        await Task.Delay(250);
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
        Assert.Contains("atom_intern_rate", phasesObserved);
        Assert.Contains("atom_load_factor", phasesObserved);
    }
}
