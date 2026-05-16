using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// One-shot configuration event emitted at the start of a bulk-load (or comparable
/// long-running run). Captures every load-bearing tuning + dispatch decision so a
/// future operator reading the JSONL can reconstruct what was actually running —
/// not what was assumed. Catches dispatch bugs in the second the run begins.
/// <para>
/// Surfaced as load-bearing by the 2026-05-03 Hash/Sorted dispatch incident
/// (cycles 1-3 silently used HashAtomStore for a Reference profile because no
/// startup banner declared the active dispatch).
/// </para>
/// </summary>
public readonly record struct RunConfigurationEvent(
    DateTimeOffset Timestamp,
    string Profile,
    string AtomStoreImplementation,
    long ChunkBufferBytes,
    long ResolveSorterChunkSize,
    bool DiskBackedAssignedIds,
    long MergeFileStreamPoolHardCap,
    long MergeFileStreamBufferSize,
    long? UserPoolSizeOverride,
    string StorePath,
    string? SourceFilePath,
    long? Limit);

/// <summary>
/// Live state of the <c>BoundedFileStreamPool</c> during merge. Streaming pool stats
/// (vs the single end-of-merge line) so eviction storms, capacity saturation, and
/// hit-rate degradation are observable in flight rather than after the fact.
/// </summary>
public readonly record struct MergePoolState(
    DateTimeOffset Timestamp,
    int OpenCount,
    int PeakOpenCount,
    int MaxOpen,
    long Hits,
    long Misses);

/// <summary>
/// Per-chunk spill in <c>SortedAtomBulkBuilder</c>. One record per <c>SpillOneChunk</c>
/// call — directly measures the inherent spill cost (sort + write per chunk).
/// <para>
/// <c>QueueDepthAtHandoff</c> is the load-bearing pipelined-spill tuning measurement
/// (ADR-037 Decision 8): the depth of the worker handoff queue observed by the parser
/// immediately before the parser's <c>Add</c> call. Steady-state 0 → bound-1 queue
/// is sufficient and parser is never blocked. Values > 0 indicate the worker is behind
/// the parser and the bound should be increased.
/// </para>
/// </summary>
public readonly record struct SpillEvent(
    DateTimeOffset Timestamp,
    int ChunkIndex,
    int RecordCount,
    long BytesWritten,
    TimeSpan SortDuration,
    TimeSpan WriteDuration,
    int QueueDepthAtHandoff);

/// <summary>
/// Periodic progress emission from <c>MergeAndWrite</c> during k-way merge over
/// the spilled atom-occurrence chunks. Closes the merge-phase opacity gap surfaced
/// by the cycle 6 21.3 B run (8+ h of unobservable merge progress).
/// <para>
/// Emitted every <c>EmissionRecordInterval</c> records (typically 100 M) plus a
/// time-based heartbeat for liveness. Carries enough state to compute mid-merge
/// throughput, pool hit-rate trend, and resolver back-pressure.
/// </para>
/// </summary>
public readonly record struct MergeProgressEvent(
    DateTimeOffset Timestamp,
    long RecordsProcessed,
    long AtomsEmitted,
    long ResolverRecordsSpilled,
    int CurrentPoolOpenCount,
    long CurrentPoolHits,
    long CurrentPoolMisses,
    long DataBytesWritten);

/// <summary>
/// One-shot end-of-merge summary — the pool stats line previously emitted to stdout
/// only. Promoted to a structured event so the JSONL contains the full merge picture
/// without grep'ing stdout.
/// </summary>
public readonly record struct MergeCompletedEvent(
    DateTimeOffset Timestamp,
    int ChunkCount,
    int PoolMaxOpen,
    int PoolPeakOpen,
    long PoolHits,
    long PoolMisses,
    long TotalGets,
    long AtomsEmitted,
    long DataBytes,
    TimeSpan Duration,
    int ChunksDeleted,
    long ChunkBytesReclaimed);

/// <summary>
/// One-shot end-of-bulk-builder summary emitted at <c>SortedAtomBulkBuilder.Finalize</c>,
/// just before <c>MergeAndWrite</c> begins. Carries the cumulative pipelined-spill
/// measurements that aren't visible from per-spill <see cref="SpillEvent"/> alone:
/// <c>ParserBlockedOnSpill</c> is the load-bearing claim from ADR-037 — the wall-time
/// the parser thread spent waiting in <c>BlockingCollection.Add</c> for the worker to
/// drain a slot. With pipelining successful, this drops to ≈ 0 vs the sequential
/// version where it equaled <c>Σ(SortDuration + WriteDuration)</c>.
/// </summary>
public readonly record struct BulkBuilderCompletedEvent(
    DateTimeOffset Timestamp,
    long TripleCount,
    long AtomOccurrenceCount,
    int SpillCount,
    TimeSpan ParserBlockedOnSpill,
    TimeSpan TotalParserWallClock);

/// <summary>
/// Periodic progress emission from <c>QuadStore.FinalizeSortedAtomBulkIfPresent</c> +
/// <c>DrainBulkSorter</c> during the silent GSPO drain phase. Closes the cycle 9
/// drain-phase opacity gap (cycle 8 + cycle 9 measured but unobserved). Two sub-phases:
/// <list type="bullet">
///   <item><b>"ReplayResolved"</b> — replaying resolved triples from the
///   <c>SortedAtomBulkBuilder</c> into the GSPO external sorter
///   (<c>EnumerateResolved</c> loop). Has a known total = TripleCount.</item>
///   <item><b>"AppendSorted"</b> — draining the sorter into the GSPO B+Tree via
///   <c>AppendSorted</c> (<c>TryDrainNext</c> loop). Same total as ReplayResolved.</item>
/// </list>
/// </summary>
public readonly record struct DrainProgressEvent(
    DateTimeOffset Timestamp,
    string PhaseName,
    string SubPhase,
    long EntriesProcessed,
    long? EstimatedTotal,
    double RatePerSecond,
    long GcHeapBytes,
    long WorkingSetBytes,
    TimeSpan Elapsed);

/// <summary>
/// ADR-040 Part 4: one-shot readahead-budget decision emitted at <c>MergeAndWrite</c>
/// start, before any <c>ChunkReadAheadBuffer</c> is constructed. Captures the
/// substrate's adaptive-sizing decision so a future operator can reconstruct what
/// the runtime did vs what was assumed. Substrate host-portability becomes data,
/// not docstring.
/// </summary>
/// <remarks>
/// Effective buffer size is the per-side (front or back) allocation. Real per-chunk
/// memory footprint is <b>2 ×</b> <c>EffectiveBufferSize</c>. <c>ReadAheadEnabled</c>
/// is <c>false</c> only when the budget couldn't fit the minimum at all — the
/// substrate falls back to synchronous direct-file reads.
/// </remarks>
public readonly record struct ReadAheadBudgetEvent(
    DateTimeOffset Timestamp,
    int ChunkCount,
    long AvailableMemoryBytes,
    long MaxReadAheadBytes,
    int RequestedBufferSize,
    int EffectiveBufferSize,
    long ProjectedTotalBytes,
    bool ReadAheadEnabled,
    string DecisionLog);

/// <summary>
/// Bulk-tmp cleanup outcome event emitted once per <c>MergeAndWrite</c> invocation.
/// ADR-041: closes the cycle-10-r3 incident pattern where a Finalize-time exception
/// (BBHash <c>OverflowException</c> 2026-05-10, MPHF non-convergence 2026-05-11) left
/// ~1.2 TB of intermediate chunk files orphaned on disk and required manual
/// <c>rm -rf</c> before retry.
/// <para>
/// The <c>Trigger</c> field distinguishes the path that fired cleanup so per-cycle
/// attribution stays accurate after the substrate becomes uniform across success
/// and exception paths. Values:
/// </para>
/// <list type="bullet">
///   <item><b>"merge_success"</b> — normal end-of-merge cleanup; carries the
///   measurement Cycle 9 surfaced (3.96 TB reclaimed at end-of-merge).</item>
///   <item><b>"merge_exception"</b> — cleanup fired through the exception path.
///   <c>FirstFailureMessage</c> when non-null carries the diagnostic from a cleanup
///   that itself partially failed (e.g., file locked by a sibling process).</item>
///   <item><b>"manual_rebuild"</b> — cleanup invoked from
///   <c>mercury --rebuild-mphf</c> after a successful MPHF rebuild; surfaces any
///   leftover bulk-tmp residue the operator may want to know about.</item>
/// </list>
/// </summary>
public readonly record struct BulkTmpCleanupEvent(
    DateTimeOffset Timestamp,
    string Trigger,
    int ChunksDeleted,
    long ChunkBytesReclaimed,
    TimeSpan ElapsedDuration,
    bool AnyDeleteFailures,
    string? FirstFailureMessage);
