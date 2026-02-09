namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A BIND expression: BIND(expression AS ?variable)
/// </summary>
public struct BindExpr
{
    public int ExprStart;    // Start of expression
    public int ExprLength;   // Length of expression
    public int VarStart;     // Start of target variable (including ?)
    public int VarLength;    // Length of target variable
    /// <summary>
    /// Index of the triple pattern after which this BIND should be evaluated.
    /// -1 means evaluate before any patterns (rare), 0 means after pattern 0, etc.
    /// This enables proper BIND semantics where the computed variable can be used
    /// as a constraint in subsequent patterns.
    /// </summary>
    public int AfterPatternIndex;
    /// <summary>
    /// Scope depth (0 = top level, 1 = first nested group, etc.)
    /// Used to exclude this binding from filters in deeper scopes per SPARQL scoping rules.
    /// </summary>
    public int ScopeDepth;
}
