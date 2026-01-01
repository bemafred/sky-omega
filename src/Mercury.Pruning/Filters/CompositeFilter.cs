namespace SkyOmega.Mercury.Pruning.Filters;

/// <summary>
/// Combines multiple filters with AND/OR logic.
/// </summary>
public sealed class CompositeFilter : IPruningFilter
{
    private readonly IPruningFilter[] _filters;
    private readonly bool _requireAll; // true = AND, false = OR

    private CompositeFilter(IPruningFilter[] filters, bool requireAll)
    {
        if (filters.Length == 0)
            throw new ArgumentException("At least one filter is required", nameof(filters));

        _filters = filters;
        _requireAll = requireAll;
    }

    /// <summary>
    /// Creates a filter that requires ALL sub-filters to pass (AND logic).
    /// </summary>
    public static CompositeFilter All(params IPruningFilter[] filters) =>
        new(filters, requireAll: true);

    /// <summary>
    /// Creates a filter that requires ANY sub-filter to pass (OR logic).
    /// </summary>
    public static CompositeFilter Any(params IPruningFilter[] filters) =>
        new(filters, requireAll: false);

    /// <inheritdoc/>
    public bool ShouldInclude(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo)
    {
        if (_requireAll)
        {
            // AND logic: all must pass
            foreach (var filter in _filters)
            {
                if (!filter.ShouldInclude(graph, subject, predicate, obj, validFrom, validTo))
                    return false;
            }
            return true;
        }
        else
        {
            // OR logic: any can pass
            foreach (var filter in _filters)
            {
                if (filter.ShouldInclude(graph, subject, predicate, obj, validFrom, validTo))
                    return true;
            }
            return false;
        }
    }
}
