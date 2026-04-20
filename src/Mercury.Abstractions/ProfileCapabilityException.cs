namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Thrown when a caller requests an operation that the store's <see cref="StoreSchema"/>
/// does not support. Typical causes (per ADR-029):
/// <list type="bullet">
/// <item>A session-API mutation (Add, Delete, SPARQL UPDATE) against a Reference-profile
/// store — Decision 7 makes Reference session-API immutable.</item>
/// <item>A temporal query (AS_OF, history, range) against a non-temporal profile.</item>
/// </list>
/// </summary>
public sealed class ProfileCapabilityException : System.InvalidOperationException
{
    public ProfileCapabilityException(string message) : base(message) { }
    public ProfileCapabilityException(string message, System.Exception inner) : base(message, inner) { }
}
