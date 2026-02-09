namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A triple pattern with subject, predicate, and object terms.
/// Supports property paths in the predicate position.
/// </summary>
public struct TriplePattern
{
    public Term Subject;
    public Term Predicate;
    public Term Object;
    public PropertyPath Path;  // Used when HasPropertyPath is true

    public readonly bool HasPropertyPath => Path.Type != PathType.None;
}
