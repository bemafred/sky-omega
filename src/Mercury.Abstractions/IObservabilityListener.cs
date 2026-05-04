using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Umbrella observability surface for Mercury. Single registration point for cross-cutting
/// observers (JSONL exporters, Grafana ingest, dashboard adapters). Every event has a
/// default no-op implementation, so listeners override only what they care about.
/// </summary>
/// <remarks>
/// <para>
/// ADR-035 Decision 1. Existing <see cref="IQueryMetricsListener"/> and
/// <see cref="IRebuildMetricsListener"/> remain as standalone subset specializations for
/// callers that want a narrow surface. <c>JsonlMetricsListener</c> implements all three.
/// </para>
/// <para>
/// Listeners are invoked synchronously on the producer thread. Implementations must be
/// thread-safe and cheap on the hot path. For state-class records that should not block
/// hot threads, see the bounded-channel path on <c>JsonlMetricsListener</c>.
/// </para>
/// <para>
/// All events carry a <see cref="DateTimeOffset"/> timestamp. Schema version is enforced
/// by the JSONL writer (Decision 4); listener implementations that emit elsewhere are
/// responsible for their own versioning.
/// </para>
/// </remarks>
public interface IObservabilityListener
{
    void OnQueryMetrics(in QueryMetrics metrics) { }

    void OnRebuildPhase(in RebuildPhaseMetrics phase) { }

    void OnRebuildProgress(in RebuildProgressMetrics progress) { }

    void OnRebuildComplete(RebuildMetrics summary) { }

    void OnLoadProgress(in LoadProgressMetrics progress) { }

    void OnGcEvent(in GcEvent ev) { }

    void OnLohDelta(in LohDeltaEvent ev) { }

    void OnRssState(in RssState state) { }

    void OnDiskFreeState(in DiskFreeState state) { }

    void OnAtomInternRate(in AtomInternRate rate) { }

    void OnAtomLoadFactor(in AtomLoadFactor lf) { }

    void OnAtomProbeDistance(in AtomProbeDistance dist) { }

    void OnAtomRehash(in AtomRehashEvent ev) { }

    void OnAtomFileGrowth(in AtomFileGrowthEvent ev) { }

    /// <summary>
    /// Run configuration banner emitted once at the start of a bulk-load (or comparable
    /// long-running operation). Discloses every load-bearing tuning + dispatch decision.
    /// </summary>
    void OnRunConfiguration(in RunConfigurationEvent ev) { }

    /// <summary>Live <see cref="BoundedFileStreamPool"/> state during merge.</summary>
    void OnMergePoolState(in MergePoolState state) { }

    /// <summary>
    /// Per-chunk spill in <c>SortedAtomBulkBuilder.AddOneAtomOccurrence</c>. Measures
    /// parser-blocking cost (sort + write duration per spill).
    /// </summary>
    void OnSpill(in SpillEvent ev) { }

    /// <summary>
    /// Periodic mid-merge progress emission from <c>MergeAndWrite</c>. Closes the
    /// merge-phase opacity gap surfaced by the cycle 6 21.3 B run.
    /// </summary>
    void OnMergeProgress(in MergeProgressEvent ev) { }

    /// <summary>One-shot end-of-merge summary with full pool stats and totals.</summary>
    void OnMergeCompleted(in MergeCompletedEvent ev) { }

    /// <summary>Scope correlation: emitted when a <c>MetricsScope</c> is opened.</summary>
    void OnScopeEnter(long scopeId, long parentScopeId, string name, DateTimeOffset timestamp) { }

    /// <summary>Scope correlation: emitted when a <c>MetricsScope</c> is disposed.</summary>
    void OnScopeExit(long scopeId, TimeSpan duration, DateTimeOffset timestamp) { }
}
