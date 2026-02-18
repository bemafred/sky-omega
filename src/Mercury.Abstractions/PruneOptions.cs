namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Options for store pruning operations.
/// </summary>
public sealed class PruneOptions
{
    /// <summary>
    /// If true, enumerate and filter without writing to target.
    /// Returns what WOULD be transferred. Default: false.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// How to handle temporal history. Default: FlattenToCurrent.
    /// </summary>
    public HistoryMode HistoryMode { get; init; } = HistoryMode.FlattenToCurrent;

    /// <summary>
    /// Graph IRIs to exclude from the pruned output. Null or empty means no graph filtering.
    /// </summary>
    public string[]? ExcludeGraphs { get; init; }

    /// <summary>
    /// Predicate IRIs to exclude from the pruned output. Null or empty means no predicate filtering.
    /// </summary>
    public string[]? ExcludePredicates { get; init; }

    /// <summary>
    /// Default options: FlattenToCurrent, no filters, not dry run.
    /// </summary>
    public static PruneOptions Default { get; } = new();
}
