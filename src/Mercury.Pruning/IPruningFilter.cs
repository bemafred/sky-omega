namespace SkyOmega.Mercury.Pruning;

/// <summary>
/// Predicate to determine if a quad should be included in the transfer.
/// Designed for zero-allocation hot path - no LINQ, no closures.
/// </summary>
internal interface IPruningFilter
{
    /// <summary>
    /// Tests whether a quad should be included in the transfer.
    /// </summary>
    /// <param name="graph">Graph IRI (empty for default graph)</param>
    /// <param name="subject">Subject IRI or blank node</param>
    /// <param name="predicate">Predicate IRI</param>
    /// <param name="obj">Object (IRI, blank node, or literal)</param>
    /// <param name="memorizedAt">When the version was memorized</param>
    /// <param name="forgottenAt">When it was forgotten; the far future while it is remembered</param>
    /// <returns>True to include the quad, false to exclude</returns>
    bool ShouldInclude(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset memorizedAt,
        DateTimeOffset forgottenAt);
}
