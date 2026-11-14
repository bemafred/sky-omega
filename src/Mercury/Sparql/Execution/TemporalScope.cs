using System;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// What a query's temporal clause lets it see: the current state, the state AS OF a time, the versions valid DURING a
/// range, or ALL VERSIONS. Every store read a query makes goes through <see cref="Query"/>.
/// </summary>
internal readonly struct TemporalScope
{
    public readonly TemporalQueryMode Mode;
    public readonly DateTimeOffset AsOfTime;
    public readonly DateTimeOffset RangeStart;
    public readonly DateTimeOffset RangeEnd;

    public TemporalScope(TemporalQueryMode mode, DateTimeOffset asOfTime, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        Mode = mode;
        AsOfTime = asOfTime;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
    }

    /// <summary>
    /// The named graphs a graph variable ranges over in this scope (ADR-061 D10, Q15). The current state: the graphs
    /// that hold something remembered now. Any other scope reads what is forgotten too, so it ranges over every graph
    /// that holds a period; a graph with nothing in the scope then gives no solution. Caller must hold read lock.
    /// </summary>
    public NamedGraphEnumerator NamedGraphs(QuadStore store) =>
        Mode == TemporalQueryMode.Current ? store.GetNamedGraphs() : store.GetNamedGraphsHoldingAPeriod();

    /// <summary>The triples of <paramref name="graph"/> (empty: the default graph) that match, as this scope sees them.</summary>
    public TemporalResultEnumerator Query(QuadStore store, ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object, ReadOnlySpan<char> graph) => Mode switch
    {
        TemporalQueryMode.AsOf => store.QueryAsOf(subject, predicate, @object, AsOfTime, graph),
        TemporalQueryMode.During => store.QueryChanges(RangeStart, RangeEnd, subject, predicate, @object, graph),
        TemporalQueryMode.AllVersions => store.QueryEvolution(subject, predicate, @object, graph),
        _ => store.QueryCurrent(subject, predicate, @object, graph)
    };
}
