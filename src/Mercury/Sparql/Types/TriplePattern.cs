namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A triple pattern with subject, predicate, and object terms.
/// Supports property paths in the predicate position.
/// </summary>
internal struct TriplePattern
{
    public Term Subject;
    public Term Predicate;
    public Term Object;
    public PropertyPath Path;  // Used when HasPropertyPath is true

    /// <summary>The source span of the words of Cognitive SPARQL after the pattern (ADR-061 D6); length 0: none.</summary>
    public int WordsStart, WordsLength;

    public readonly bool HasPropertyPath => Path.Type != PathType.None;
}
