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
/// call — directly measures the parser-blocking cost (sibling limit:
/// <c>spill-blocks-parser.md</c>) and lets us decide on pipelined-spill from data
/// rather than projection.
/// </summary>
public readonly record struct SpillEvent(
    DateTimeOffset Timestamp,
    int ChunkIndex,
    int RecordCount,
    long BytesWritten,
    TimeSpan SortDuration,
    TimeSpan WriteDuration);

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
    TimeSpan Duration);
