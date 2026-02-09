namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Type of value in a binding.
/// </summary>
public enum BindingValueType : byte
{
    Unbound = 0,
    Uri = 1,
    String = 2,
    Integer = 3,
    Double = 4,
    Boolean = 5
}
