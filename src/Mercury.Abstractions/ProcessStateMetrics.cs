using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Garbage-collection event captured via <c>GC.RegisterForFullGCNotification</c> /
/// <c>GC.GetGCMemoryInfo()</c>. ADR-035 Phase 7a.2 (Category G).
/// </summary>
public readonly record struct GcEvent(
    DateTimeOffset Timestamp,
    int Generation,
    TimeSpan PauseDuration,
    long HeapSizeAfterBytes);

/// <summary>
/// Large-object-heap allocation delta over the most recent interval, computed from
/// <c>GC.GetTotalAllocatedBytes</c> snapshots. Validates zero-GC discipline; spots
/// regressions when LOH growth correlates with operation slowdown. ADR-035 Phase 7a.2.
/// </summary>
public readonly record struct LohDeltaEvent(
    DateTimeOffset Timestamp,
    long DeltaBytes,
    long TotalAllocatedBytes);

/// <summary>
/// Resident-set / working-set sample. Periodic state record emitted by the timer when
/// <c>--metrics-out</c> is enabled. ADR-035 Phase 7a.2.
/// </summary>
public readonly record struct RssState(
    DateTimeOffset Timestamp,
    long WorkingSetBytes,
    long PrivateMemoryBytes);

/// <summary>
/// Disk-free sample for the store path. Mercury already enforces <c>--min-free-space</c>;
/// emitting periodic samples lets operators correlate disk pressure with slowdown. ADR-035 Phase 7a.2.
/// </summary>
public readonly record struct DiskFreeState(
    DateTimeOffset Timestamp,
    string Path,
    long FreeBytes,
    long TotalBytes);
