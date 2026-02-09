namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A FILTER expression reference (offset into source).
/// </summary>
public struct FilterExpr
{
    public int Start;      // Offset into source (after "FILTER")
    public int Length;     // Length of expression
    public int ScopeDepth; // Scope depth (0 = top level, 1 = first nested group, etc.)
}
