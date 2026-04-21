using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Per-query timing and shape captured at <c>SparqlEngine.Query</c> boundaries. Emitted
/// through <see cref="IQueryMetricsListener"/> if a listener is attached to the store —
/// the no-listener path is zero overhead by construction (struct is never allocated).
/// </summary>
/// <remarks>
/// The fields are intentionally narrow. ADR-030 Phase 1 is scaffolding — percentile
/// aggregation, index-path attribution, and per-operator breakdowns are Phase 2+ work.
/// Listener authors compose the aggregate view they want on top of this stream.
/// </remarks>
public readonly record struct QueryMetrics(
    DateTimeOffset Timestamp,
    StoreProfile Profile,
    QueryMetricsKind Kind,
    TimeSpan ParseTime,
    TimeSpan ExecutionTime,
    long RowsReturned,
    bool Success,
    string? ErrorMessage);

/// <summary>
/// SPARQL query shape for the metrics stream. Keeps the listener's schema stable across
/// executor-internal changes. One entry per <c>SparqlEngine.Query</c> call.
/// </summary>
public enum QueryMetricsKind
{
    Select,
    Ask,
    Construct,
    Describe,
    Update
}
