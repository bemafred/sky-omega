namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Reference to an EXISTS pattern embedded within a compound FILTER expression.
/// Used to track positions of [NOT] EXISTS in expressions like: FILTER ( ?x = ?y || NOT EXISTS { ... } )
/// </summary>
public struct CompoundExistsRef
{
    /// <summary>
    /// Start position of the [NOT] EXISTS portion in the filter expression (relative to filter start).
    /// </summary>
    public int StartInFilter;

    /// <summary>
    /// Length of the [NOT] EXISTS portion including the braces.
    /// </summary>
    public int Length;

    /// <summary>
    /// Index of the corresponding ExistsFilter (MinusExistsFilter) that contains the patterns.
    /// </summary>
    public int ExistsFilterIndex;

    /// <summary>
    /// True if this is NOT EXISTS (negate the result).
    /// </summary>
    public bool Negated;

    /// <summary>
    /// Which MINUS block this compound EXISTS ref belongs to.
    /// </summary>
    public int BlockIndex;
}
