namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Single variable binding (unmanaged for stackalloc)
/// </summary>
public struct Binding
{
    public int VariableNameHash;
    public BindingValueType Type;
    public long IntegerValue;
    public double DoubleValue;
    public bool BooleanValue;
    public int StringOffset;
    public int StringLength;
    /// <summary>
    /// Scope depth where this binding was created.
    /// -1 = from triple pattern match, >= 0 = from BIND at that scope depth.
    /// Used for BIND scoping rules where filters in nested groups should not
    /// see BIND variables from outer scopes.
    /// </summary>
    public int BindScopeDepth;
}
