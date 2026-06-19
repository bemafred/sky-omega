namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A BIND expression: BIND(expression AS ?variable)
/// </summary>
internal struct BindExpr
{
    public int ExprStart;    // Start of expression
    public int ExprLength;   // Length of expression
    public int VarStart;     // Start of target variable (including ?)
    public int VarLength;    // Length of target variable
    /// <summary>
    /// Scope depth (0 = top level, 1 = first nested group, etc.)
    /// Used to exclude this binding from filters in deeper scopes per SPARQL scoping rules.
    /// </summary>
    public int ScopeDepth;
}
