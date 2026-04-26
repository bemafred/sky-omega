using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Listener-side projection of streaming load progress. Mirrors the existing user-facing
/// <c>LoadProgress</c> callback shape (in <c>RdfEngine</c>) but in a value-typed form
/// suitable for the umbrella <see cref="IObservabilityListener"/> contract.
/// Emitted per chunk-flush during bulk-load (every 100K triples), terminal-throttled to ~10s.
/// </summary>
public readonly record struct LoadProgressMetrics(
    DateTimeOffset Timestamp,
    long TriplesLoaded,
    TimeSpan Elapsed,
    double TriplesPerSecond,
    double RecentTriplesPerSecond,
    long GcHeapBytes,
    long WorkingSetBytes);
