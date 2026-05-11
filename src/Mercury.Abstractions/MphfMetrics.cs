using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// One-shot start event emitted by <c>BBHashBuilder.Build</c> immediately after
/// preconditions are validated. Discloses the construction tuning (gamma, MaxLevels,
/// MaxDenseKeys, base seed) so downstream analysis can correlate convergence shape
/// with parameters. ADR-039 / cycle 10 Phase 3 instrumentation.
/// </summary>
public readonly record struct MphfBuildStartedEvent(
    DateTimeOffset Timestamp,
    long AtomCount,
    double Gamma,
    int MaxLevels,
    int MaxDenseKeys,
    ulong BaseSeed);

/// <summary>
/// Per-level completion event. Captures the BBHash iterative-phase convergence
/// shape: how many keys entered the level, the bit-vector size at γ, how many
/// were placed (popcount of the level bit-vector), and how many were bumped
/// to the next level. Per-level wall-clock isolates pathological levels.
/// </summary>
/// <remarks>
/// At γ=2.0 the expected placement ratio is ~50 % per level (≈ 1 − e<sup>−1/γ</sup>).
/// Observed ratios meaningfully below that indicate hash-distribution problems
/// for the input set; consistently above is just variance.
/// </remarks>
public readonly record struct MphfLevelCompletedEvent(
    DateTimeOffset Timestamp,
    int LevelIndex,
    long RemainingAtEntry,
    long BitCount,
    long Placed,
    long Bumped,
    TimeSpan LevelDuration);

/// <summary>
/// Dense-fallback engagement event. Emitted only when the iterative phase leaves
/// keys un-placed at <c>MaxLevels</c> and the dense final level absorbs them.
/// ADR-039 / 1.7.55: the dense fallback makes convergence deterministic at any N.
/// Empty case (the common case at γ=2.0 with MaxLevels=40) yields no emission.
/// </summary>
public readonly record struct MphfDenseFallbackEvent(
    DateTimeOffset Timestamp,
    int DenseKeysCount,
    int LevelsUsed);

/// <summary>
/// One-shot end-of-build summary. Combines construction wall-clock, total
/// write-to-disk wall-clock, level count actually used, dense key count, and
/// the resulting file sizes. Pairs with <see cref="MphfBuildStartedEvent"/> to
/// give the full construction picture in two records plus per-level detail.
/// </summary>
public readonly record struct MphfBuildCompletedEvent(
    DateTimeOffset Timestamp,
    long AtomCount,
    int LevelCount,
    int DenseKeysCount,
    long MphfBytes,
    long IdxBytes,
    TimeSpan BuildDuration,
    TimeSpan TotalDuration);
