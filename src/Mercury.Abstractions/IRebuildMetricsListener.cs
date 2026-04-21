namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Sink for <see cref="RebuildPhaseMetrics"/> and <see cref="RebuildMetrics"/> emitted
/// by <c>QuadStore.RebuildSecondaryIndexes</c>. Called synchronously under the store's
/// write lock; implementations must be thread-safe and cheap for the same reasons
/// <see cref="IQueryMetricsListener"/> lays out.
/// </summary>
public interface IRebuildMetricsListener
{
    void OnRebuildPhase(in RebuildPhaseMetrics phase);
    void OnRebuildComplete(RebuildMetrics summary);
}
