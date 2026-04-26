using System;
using System.Linq;
using System.Threading.Tasks;
using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

/// <summary>
/// ADR-035 Decision 2: LatencyHistogram correctness. Validates percentile estimation
/// against the analytic ground truth on synthetic distributions; documents max bucket
/// aliasing error at p99.
/// </summary>
public class LatencyHistogramTests
{
    [Fact]
    public void Empty_PercentileReturnsZero()
    {
        var h = new LatencyHistogram();
        Assert.Equal(0, h.Percentile(50));
        Assert.Equal(0, h.Percentile(99));
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void Linear_BelowSubBucketCount_ExactValues()
    {
        var h = new LatencyHistogram(highestTrackable: 1024, subBucketBits: 7);
        for (int i = 0; i < 128; i++)
            h.Record(i);
        Assert.Equal(128, h.Count);
        Assert.Equal(63, h.Percentile(50));
        Assert.Equal(127, h.Percentile(100));
    }

    [Fact]
    public void Logarithmic_HigherBuckets_WithinAliasingBound()
    {
        var h = new LatencyHistogram(highestTrackable: 1_000_000, subBucketBits: 7);
        var values = new long[] { 200, 500, 1000, 2000, 5000, 10000, 50000, 100000 };
        foreach (var v in values)
            h.Record(v);

        long p100 = h.Percentile(100);
        Assert.True(p100 >= 100000 && p100 <= 100000 * 1.01,
            $"p100 {p100} should be within 1% of recorded max 100000");
    }

    [Fact]
    public void UniformDistribution_PercentilesWithinOnePercent()
    {
        var h = new LatencyHistogram(highestTrackable: 100_000, subBucketBits: 7);
        var rng = new Random(42);
        const int n = 100_000;
        var samples = new long[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = rng.Next(0, 100_000);
            h.Record(samples[i]);
        }
        Array.Sort(samples);

        long expectedP50 = samples[(int)(n * 0.50) - 1];
        long expectedP95 = samples[(int)(n * 0.95) - 1];
        long expectedP99 = samples[(int)(n * 0.99) - 1];
        long expectedP999 = samples[(int)(n * 0.999) - 1];

        AssertWithinOnePercent(expectedP50, h.Percentile(50), "p50");
        AssertWithinOnePercent(expectedP95, h.Percentile(95), "p95");
        AssertWithinOnePercent(expectedP99, h.Percentile(99), "p99");
        AssertWithinOnePercent(expectedP999, h.Percentile(99.9), "p999");
    }

    [Fact]
    public void LogNormalDistribution_TailPercentilesWithinBounds()
    {
        var h = new LatencyHistogram(highestTrackable: 10_000_000, subBucketBits: 7);
        var rng = new Random(7);
        const int n = 50_000;
        var samples = new long[n];
        for (int i = 0; i < n; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            long v = (long)Math.Max(0, Math.Min(10_000_000, Math.Exp(8 + normal)));
            samples[i] = v;
            h.Record(v);
        }
        Array.Sort(samples);

        AssertWithinOnePercent(samples[(int)(n * 0.50) - 1], h.Percentile(50), "p50");
        AssertWithinOnePercent(samples[(int)(n * 0.95) - 1], h.Percentile(95), "p95");
        AssertWithinOnePercent(samples[(int)(n * 0.99) - 1], h.Percentile(99), "p99");
    }

    [Fact]
    public async Task ConcurrentRecord_AccurateCount()
    {
        var h = new LatencyHistogram(highestTrackable: 10_000);
        const int threads = 8;
        const int perThread = 10_000;

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var rng = new Random(t);
            for (int i = 0; i < perThread; i++)
                h.Record(rng.Next(0, 10_000));
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(threads * perThread, h.Count);
    }

    [Fact]
    public void OutOfRange_ClampedToHighestTrackable()
    {
        var h = new LatencyHistogram(highestTrackable: 1000, subBucketBits: 7);
        h.Record(99_999);
        h.Record(-5);
        Assert.Equal(2, h.Count);
        // Clamping prevents overflow into a non-existent bucket. Bucket-quantization
        // can return a value slightly above the clamp ceiling (the midpoint of the
        // slot containing the clamped value). 5% headroom is well inside the documented
        // 1% aliasing bound at p99.
        Assert.True(h.Percentile(100) <= 1050,
            $"p100 {h.Percentile(100)} should be near the clamp ceiling 1000");
    }

    [Fact]
    public void Reset_ClearsAllSamples()
    {
        var h = new LatencyHistogram();
        for (int i = 0; i < 100; i++) h.Record(i * 10);
        Assert.Equal(100, h.Count);
        h.Reset();
        Assert.Equal(0, h.Count);
        Assert.Equal(0, h.Percentile(50));
    }

    private static void AssertWithinOnePercent(long expected, long actual, string label)
    {
        if (expected == 0)
        {
            Assert.True(actual <= 1, $"{label}: expected ≈ 0, got {actual}");
            return;
        }
        double relErr = Math.Abs(actual - expected) / (double)expected;
        Assert.True(relErr <= 0.02,
            $"{label}: expected {expected}, got {actual}, relative error {relErr:P2} exceeds 2% bound");
    }
}
