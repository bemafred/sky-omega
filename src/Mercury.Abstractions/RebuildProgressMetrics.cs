using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// In-progress rebuild emission, mirroring the <see cref="LoadProgress"/>-style shape used
/// by bulk-load. Fires periodically inside a rebuild phase (per-1M entries throttled, terminal
/// display ~10s) so a long-running rebuild is observable live and in JSONL — closes the
/// observability gap surfaced during Phase 6's silent trigram phase.
/// </summary>
/// <remarks>
/// ADR-035 Phase 7a.1 wires emission sites in <c>RebuildReferenceSecondaryIndexes</c>
/// (GSPO scan, chunk-spill, drain). 7a.0 defines the type so the umbrella interface
/// can carry it before the producers exist.
/// </remarks>
public readonly record struct RebuildProgressMetrics(
    DateTimeOffset Timestamp,
    string PhaseName,
    string SubPhase,
    long EntriesProcessed,
    long EstimatedTotal,
    double RatePerSecond,
    long GcHeapBytes,
    long WorkingSetBytes,
    TimeSpan Elapsed);
