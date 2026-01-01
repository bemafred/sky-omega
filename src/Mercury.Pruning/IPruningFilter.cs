namespace SkyOmega.Mercury.Pruning;

/// <summary>
/// Predicate to determine if a quad should be included in the transfer.
/// Designed for zero-allocation hot path - no LINQ, no closures.
/// </summary>
public interface IPruningFilter
{
    /// <summary>
    /// Tests whether a quad should be included in the transfer.
    /// </summary>
    /// <param name="graph">Graph IRI (empty for default graph)</param>
    /// <param name="subject">Subject IRI or blank node</param>
    /// <param name="predicate">Predicate IRI</param>
    /// <param name="obj">Object (IRI, blank node, or literal)</param>
    /// <param name="validFrom">Valid-time start</param>
    /// <param name="validTo">Valid-time end</param>
    /// <returns>True to include the quad, false to exclude</returns>
    bool ShouldInclude(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo);
}
