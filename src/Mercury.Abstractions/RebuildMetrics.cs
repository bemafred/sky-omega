using System;
using System.Collections.Generic;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Per-phase timing emitted as each secondary index (GPOS, GOSP, TGSP, Trigram) is
/// rebuilt from the primary GSPO scan. Reference-profile rebuilds currently emit no
/// phase records (they are no-op); a single <see cref="RebuildMetrics"/> summary
/// still fires so listeners can count rebuild invocations uniformly.
/// </summary>
public readonly record struct RebuildPhaseMetrics(
    DateTimeOffset Timestamp,
    string IndexName,
    long EntriesProcessed,
    TimeSpan Elapsed);

/// <summary>
/// Final record of a <c>QuadStore.RebuildSecondaryIndexes</c> invocation. Holds the
/// profile it ran against, the per-phase records emitted during the run, and the
/// total wall time measured end-to-end.
/// </summary>
public sealed record class RebuildMetrics(
    DateTimeOffset Timestamp,
    StoreProfile Profile,
    TimeSpan TotalElapsed,
    IReadOnlyList<RebuildPhaseMetrics> Phases,
    bool WasNoOp);
