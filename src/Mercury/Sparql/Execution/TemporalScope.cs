using System;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// What one triple pattern lets its scan see, and the times it binds (ADR-061 D6 to D10). Without the words of
/// Cognitive SPARQL a pattern sees what is remembered, through the store's current read on every profile (D7). With
/// them, or under <c>INCLUDING FORGOTTEN</c>, it sees the periods its two ranges of times select, through the read
/// that only a Cognitive store has. Every store read a query makes goes through <see cref="Query"/>.
/// </summary>
internal readonly struct TemporalScope
{
    private readonly TemporalQuery _times;
    private readonly bool _selectsByTime;

    /// <summary>
    /// Whether the pattern sees forgotten memories: under <c>INCLUDING FORGOTTEN</c>, or because it carries
    /// <c>FORGOTTEN</c>. A graph variable then ranges over every graph that holds a period (D10).
    /// </summary>
    public readonly bool IncludesForgotten;

    /// <summary>The variable <c>MEMORIZED</c> binds, as a source span; length 0: none.</summary>
    public readonly int MemorizedVariableStart, MemorizedVariableLength;

    /// <summary>The variable <c>FORGOTTEN</c> binds, as a source span; length 0: none.</summary>
    public readonly int ForgottenVariableStart, ForgottenVariableLength;

    private TemporalScope(TemporalQuery times, bool includesForgotten,
        int memorizedVariableStart, int memorizedVariableLength, int forgottenVariableStart, int forgottenVariableLength)
    {
        _times = times;
        _selectsByTime = true;
        IncludesForgotten = includesForgotten;
        MemorizedVariableStart = memorizedVariableStart;
        MemorizedVariableLength = memorizedVariableLength;
        ForgottenVariableStart = forgottenVariableStart;
        ForgottenVariableLength = forgottenVariableLength;
    }

    /// <summary>What is remembered: the default view (D7). It is the default value.</summary>
    public static TemporalScope Remembered => default;

    /// <summary>The scope of a query's patterns that carry no words: what is remembered, or under
    /// <c>INCLUDING FORGOTTEN</c> every period.</summary>
    public static TemporalScope ForQuery(bool includesForgotten) => includesForgotten
        ? new TemporalScope(TemporalQuery.Words(long.MinValue, long.MaxValue, null, null, forgottenToo: true),
            includesForgotten: true, 0, 0, 0, 0)
        : Remembered;

    /// <summary>
    /// The scope of a pattern with words: <paramref name="times"/> as the words select, and the variables they bind.
    /// </summary>
    public static TemporalScope ForWords(TemporalQuery times, bool includesForgotten,
        int memorizedVariableStart, int memorizedVariableLength, int forgottenVariableStart, int forgottenVariableLength)
        => new(times, includesForgotten, memorizedVariableStart, memorizedVariableLength,
            forgottenVariableStart, forgottenVariableLength);

    /// <summary>Whether the pattern binds a time variable.</summary>
    public bool BindsTimes => MemorizedVariableLength > 0 || ForgottenVariableLength > 0;

    /// <summary>Whether the pattern sees what is remembered and nothing else, through the store's current read.</summary>
    public bool IsRemembered => !_selectsByTime;

    /// <summary>
    /// The named graphs a graph variable ranges over in this scope (ADR-061 D10, Q15). What is remembered: the graphs
    /// that hold something remembered now. A scope that sees forgotten memories ranges over every graph that holds
    /// a period; a graph with nothing in the scope then gives no solution. Caller must hold read lock.
    /// </summary>
    public NamedGraphEnumerator NamedGraphs(QuadStore store) =>
        IncludesForgotten ? store.GetNamedGraphsHoldingAPeriod() : store.GetNamedGraphs();

    /// <summary>The triples of <paramref name="graph"/> (empty: the default graph) that match, as this scope sees them.</summary>
    public ResultEnumerator Query(QuadStore store, ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object, ReadOnlySpan<char> graph) => _selectsByTime
        ? store.QueryByMemoryTimes(subject, predicate, @object, graph, _times)
        : store.QueryCurrent(subject, predicate, @object, graph);
}
