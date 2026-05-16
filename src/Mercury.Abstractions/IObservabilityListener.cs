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
/// <b>Multi-thread invocation (ADR-037).</b> With pipelined spill, listener methods can
/// be invoked from either the parser thread or the bulk-builder's spill worker thread
/// concurrently. Specifically, <see cref="OnSpill"/> fires from the worker; other bulk
/// methods (<see cref="OnRunConfiguration"/>, <see cref="OnBulkBuilderCompleted"/>,
/// <see cref="OnMergeProgress"/>, <see cref="OnMergeCompleted"/>) fire from the parser
/// thread. Implementations must serialize their own state.
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

    /// <summary>
    /// One-shot end-of-bulk-builder summary emitted at <c>SortedAtomBulkBuilder.Finalize</c>,
    /// just before <c>MergeAndWrite</c> begins. ADR-037: carries
    /// <c>ParserBlockedOnSpill</c> as the load-bearing pipelined-spill measurement.
    /// </summary>
    void OnBulkBuilderCompleted(in BulkBuilderCompletedEvent ev) { }

    /// <summary>
    /// Periodic progress emission from the GSPO drain phase
    /// (<c>QuadStore.FinalizeSortedAtomBulkIfPresent</c> + <c>DrainBulkSorter</c>).
    /// Closes the silent drain-phase gap surfaced by cycle 9.
    /// </summary>
    void OnDrainProgress(in DrainProgressEvent progress) { }

    /// <summary>
    /// MPHF construction start. Discloses the tuning (gamma, MaxLevels, MaxDenseKeys,
    /// base seed) over the atom set being built. ADR-039 / cycle 10 Phase 3.
    /// </summary>
    void OnMphfBuildStarted(in MphfBuildStartedEvent ev) { }

    /// <summary>
    /// Per-level BBHash convergence: placement vs bump counts plus per-level wall-clock.
    /// Closes the MPHF-internals opacity gap surfaced by cycle 10 Phase 3.
    /// </summary>
    void OnMphfLevelCompleted(in MphfLevelCompletedEvent ev) { }

    /// <summary>
    /// Dense-fallback engagement — fires only when the iterative phase leaves keys
    /// un-placed at the configured MaxLevels. Empty case is silent
    /// (the common path at γ=2.0 with MaxLevels=40).
    /// </summary>
    void OnMphfDenseFallback(in MphfDenseFallbackEvent ev) { }

    /// <summary>One-shot end-of-build summary for MPHF construction.</summary>
    void OnMphfBuildCompleted(in MphfBuildCompletedEvent ev) { }

    /// <summary>
    /// Bulk-tmp cleanup outcome (success path or exception path). ADR-041:
    /// closes the cycle-10-r3 incident pattern where a Finalize-time exception left
    /// intermediate chunk files orphaned. <see cref="BulkTmpCleanupEvent.Trigger"/>
    /// distinguishes the path that fired.
    /// </summary>
    void OnBulkTmpCleanup(in BulkTmpCleanupEvent ev) { }

    /// <summary>
    /// ADR-040 Part 4: readahead-budget decision emitted at <c>MergeAndWrite</c> start.
    /// Captures the substrate's adaptive-sizing decision (effective buffer size,
    /// projected total, host available memory) so future analysis can verify the
    /// runtime did what was assumed.
    /// </summary>
    void OnReadAheadBudget(in ReadAheadBudgetEvent ev) { }

    /// <summary>
    /// ADR-042 Part 5: MPHF construction memory-budget check emitted at
    /// <c>BuildMphfFiles</c> start. Compares projected peak (≈ AtomCount × 4 bytes)
    /// against ProcessMemoryProbe.AvailablePhysicalBytes × MemoryFraction.
    /// Informational only — substrate proceeds regardless of <c>WithinBudget</c>.
    /// </summary>
    void OnMphfMemoryBudget(in MphfMemoryBudgetEvent ev) { }

    /// <summary>Scope correlation: emitted when a <c>MetricsScope</c> is opened.</summary>
    void OnScopeEnter(long scopeId, long parentScopeId, string name, DateTimeOffset timestamp) { }

    /// <summary>Scope correlation: emitted when a <c>MetricsScope</c> is disposed.</summary>
    void OnScopeExit(long scopeId, TimeSpan duration, DateTimeOffset timestamp) { }
}
