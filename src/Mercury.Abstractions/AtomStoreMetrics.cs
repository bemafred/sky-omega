using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Cumulative <c>Intern</c> count + rate over the most recent interval. Validates Round 2
/// SortedAtomStore wins (ADR-034) and tracks atom-side throughput during bulk-load.
/// ADR-035 Phase 7a.3 (Category B).
/// </summary>
public readonly record struct AtomInternRate(
    DateTimeOffset Timestamp,
    long CumulativeIntern,
    double RatePerSecond);

/// <summary>
/// Hash-table load factor sample. Detects clustering before catastrophic regressions
/// (the 1.7.16 word-wise-FNV incident). Emitted periodically as a state record.
/// ADR-035 Phase 7a.3.
/// </summary>
public readonly record struct AtomLoadFactor(
    DateTimeOffset Timestamp,
    long AtomCount,
    long BucketCount,
    double LoadFactor);

/// <summary>
/// Probe-distance histogram percentiles over the most recent interval. Carries tail-latency
/// data for atom interning under realistic workloads. Backed by <c>LatencyHistogram</c>.
/// ADR-035 Phase 7a.3.
/// </summary>
public readonly record struct AtomProbeDistance(
    DateTimeOffset Timestamp,
    long P50,
    long P95,
    long P99,
    long P999,
    long Max,
    long SampleCount);

/// <summary>
/// Discrete rehash event (ADR-028). Quantifies in production what was previously visible
/// only via wall-clock pause observation. ADR-035 Phase 7a.3.
/// </summary>
public readonly record struct AtomRehashEvent(
    DateTimeOffset Timestamp,
    long OldBucketCount,
    long NewBucketCount,
    TimeSpan Duration);

/// <summary>
/// Atom-data-file growth event. Each <c>SetLength</c> extension on the .atoms data file
/// emits one record. Correlates with bulk-load throughput dips. ADR-035 Phase 7a.3.
/// </summary>
public readonly record struct AtomFileGrowthEvent(
    DateTimeOffset Timestamp,
    string FilePath,
    long OldLengthBytes,
    long NewLengthBytes);
