namespace SkyOmega.Mercury.Pruning.Filters;

/// <summary>
/// Filters quads by graph IRI(s). Supports include/exclude modes.
/// </summary>
internal sealed class GraphFilter : IPruningFilter
{
    private readonly HashSet<string> _graphIris;
    private readonly bool _includeMode; // true = include only these, false = exclude these
    private readonly bool _includeDefaultGraph;

    private GraphFilter(IEnumerable<string> graphIris, bool includeMode, bool includeDefaultGraph)
    {
        _graphIris = new HashSet<string>(graphIris, StringComparer.Ordinal);
        _includeMode = includeMode;
        _includeDefaultGraph = includeDefaultGraph;
    }

    /// <summary>
    /// Creates a filter that includes only the specified graphs.
    /// The default graph is excluded unless specified in the list.
    /// </summary>
    public static GraphFilter Include(params string[] graphIris) =>
        new(graphIris, includeMode: true, includeDefaultGraph: false);

    /// <summary>
    /// Creates a filter that includes only the specified graphs plus the default graph.
    /// </summary>
    public static GraphFilter IncludeWithDefault(params string[] graphIris) =>
        new(graphIris, includeMode: true, includeDefaultGraph: true);

    /// <summary>
    /// Creates a filter that excludes the specified graphs (includes all others).
    /// </summary>
    public static GraphFilter Exclude(params string[] graphIris) =>
        new(graphIris, includeMode: false, includeDefaultGraph: true);

    /// <summary>
    /// Creates a filter for the default graph only.
    /// </summary>
    public static GraphFilter DefaultGraphOnly() =>
        new(Array.Empty<string>(), includeMode: true, includeDefaultGraph: true);

    /// <summary>
    /// Creates a filter that excludes the default graph (includes all named graphs).
    /// </summary>
    public static GraphFilter NamedGraphsOnly() =>
        new(Array.Empty<string>(), includeMode: false, includeDefaultGraph: false);

    /// <inheritdoc/>
    public bool ShouldInclude(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo)
    {
        var isDefaultGraph = graph.IsEmpty;

        if (isDefaultGraph)
        {
            return _includeMode ? _includeDefaultGraph : _includeDefaultGraph;
        }

        // For named graphs, check if in set
        var graphStr = graph.ToString();
        var inSet = _graphIris.Contains(graphStr);

        return _includeMode ? inSet : !inSet;
    }
}
