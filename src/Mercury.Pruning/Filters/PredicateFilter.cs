namespace SkyOmega.Mercury.Pruning.Filters;

/// <summary>
/// Filters quads by predicate IRI(s). Useful for excluding system predicates.
/// </summary>
internal sealed class PredicateFilter : IPruningFilter
{
    private readonly HashSet<string> _predicateIris;
    private readonly bool _includeMode;

    private PredicateFilter(IEnumerable<string> predicateIris, bool includeMode)
    {
        _predicateIris = new HashSet<string>(predicateIris, StringComparer.Ordinal);
        _includeMode = includeMode;
    }

    /// <summary>
    /// Creates a filter that includes only quads with the specified predicates.
    /// </summary>
    public static PredicateFilter Include(params string[] predicateIris) =>
        new(predicateIris, includeMode: true);

    /// <summary>
    /// Creates a filter that excludes quads with the specified predicates.
    /// </summary>
    public static PredicateFilter Exclude(params string[] predicateIris) =>
        new(predicateIris, includeMode: false);

    /// <inheritdoc/>
    public bool ShouldInclude(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo)
    {
        var predicateStr = predicate.ToString();
        var inSet = _predicateIris.Contains(predicateStr);
        return _includeMode ? inSet : !inSet;
    }
}
