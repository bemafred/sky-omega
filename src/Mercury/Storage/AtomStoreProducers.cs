using System;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// State producers for atom-store metrics (ADR-035 Phase 7a.3 — Category B). Each factory
/// returns a closure that captures previous-tick state and emits one record per tick.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HashAtomStore"/> is internal; producers live alongside it in
/// <c>SkyOmega.Mercury.Storage</c>. The QuadStore exposes
/// <c>RegisterAtomStateProducers(JsonlMetricsListener)</c> as the public-facing API; CLI
/// consumers go through that, not these factories directly.
/// </para>
/// <para>
/// Discrete events (<see cref="AtomRehashEvent"/>, <see cref="AtomFileGrowthEvent"/>) are
/// emitted synchronously from inside <see cref="HashAtomStore"/>; this class supplies only
/// the periodic state-class records (rate, load factor, probe-distance percentiles).
/// </para>
/// </remarks>
internal static class AtomStoreProducers
{
    /// <summary>
    /// InternRate sampler: emits cumulative <see cref="HashAtomStore.AtomCount"/> plus the
    /// per-second rate over the most recent interval.
    /// </summary>
    public static JsonlMetricsListener.StateProducer InternRate(HashAtomStore store)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        long lastCount = store.AtomCount;
        var lastTimestamp = DateTimeOffset.UtcNow;
        return listener =>
        {
            var now = DateTimeOffset.UtcNow;
            long current = store.AtomCount;
            var dt = (now - lastTimestamp).TotalSeconds;
            var rate = dt > 0 ? (current - lastCount) / dt : 0;
            listener.OnAtomInternRate(new AtomInternRate(
                Timestamp: now,
                CumulativeIntern: current,
                RatePerSecond: rate));
            lastCount = current;
            lastTimestamp = now;
        };
    }

    /// <summary>
    /// LoadFactor sampler: emits current atom-count, bucket-count, and load-factor ratio.
    /// </summary>
    public static JsonlMetricsListener.StateProducer LoadFactor(HashAtomStore store)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        return listener =>
        {
            long atoms = store.AtomCount;
            long buckets = store.BucketCount;
            double lf = buckets > 0 ? (double)atoms / buckets : 0;
            listener.OnAtomLoadFactor(new AtomLoadFactor(
                Timestamp: DateTimeOffset.UtcNow,
                AtomCount: atoms,
                BucketCount: buckets,
                LoadFactor: lf));
        };
    }

    /// <summary>
    /// ProbeDistance sampler: emits p50/p95/p99/p999/max + sample count from the store's
    /// probe-distance histogram, then resets the histogram for the next window.
    /// </summary>
    /// <remarks>
    /// Reset gives "percentiles of the last interval" — what operators want for trend
    /// detection. Lifetime percentiles glose over phase transitions (peak-to-steady-state
    /// drift) that are exactly the signal Phase 7c rounds need to validate.
    /// Concurrency: <see cref="LatencyHistogram.Reset"/> is not atomic with concurrent
    /// <see cref="LatencyHistogram.Record"/> calls; samples taken during the reset can
    /// be lost. Acceptable for state-class records — not exact accounting.
    /// </remarks>
    public static JsonlMetricsListener.StateProducer ProbeDistance(HashAtomStore store)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        return listener =>
        {
            var histogram = store.ProbeDistanceHistogram;
            if (histogram is null || histogram.Count == 0) return;

            var p50 = histogram.Percentile(50);
            var p95 = histogram.Percentile(95);
            var p99 = histogram.Percentile(99);
            var p999 = histogram.Percentile(99.9);
            var max = histogram.Percentile(100);
            var samples = histogram.Count;

            listener.OnAtomProbeDistance(new AtomProbeDistance(
                Timestamp: DateTimeOffset.UtcNow,
                P50: p50,
                P95: p95,
                P99: p99,
                P999: p999,
                Max: max,
                SampleCount: samples));

            histogram.Reset();
        };
    }

    /// <summary>Register all three Category B state producers on the listener.</summary>
    public static void RegisterAll(JsonlMetricsListener listener, HashAtomStore store)
    {
        if (listener is null) throw new ArgumentNullException(nameof(listener));
        if (store is null) throw new ArgumentNullException(nameof(store));
        listener.RegisterStateProducer(InternRate(store));
        listener.RegisterStateProducer(LoadFactor(store));
        listener.RegisterStateProducer(ProbeDistance(store));
    }
}
