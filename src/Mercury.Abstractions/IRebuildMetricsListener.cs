namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Sink for <see cref="RebuildPhaseMetrics"/> and <see cref="RebuildMetrics"/> emitted
/// by <c>QuadStore.RebuildSecondaryIndexes</c>. Called under the store's write lock
/// for the rebuild as a whole, but with ADR-030 Phase 2 parallel rebuild in effect
/// <see cref="OnRebuildPhase"/> is fired from multiple consumer threads concurrently —
/// one per target secondary (GPOS/GOSP/TGSP/Trigram for Cognitive, GPOS/Trigram for
/// Reference). Implementations MUST be thread-safe. <see cref="OnRebuildComplete"/>
/// is called once per rebuild, from the caller thread, after all consumers finish.
/// </summary>
public interface IRebuildMetricsListener
{
    /// <summary>Called once per secondary index. May fire concurrently from multiple threads.</summary>
    void OnRebuildPhase(in RebuildPhaseMetrics phase);

    /// <summary>Called exactly once at the end of a rebuild, after all phase events have fired.</summary>
    void OnRebuildComplete(RebuildMetrics summary);
}
