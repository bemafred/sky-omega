namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A property path expression for SPARQL 1.1 property paths.
/// Supports: ^iri (inverse), iri+ (one or more), iri* (zero or more),
/// iri? (zero or one), path1/path2 (sequence), path1|path2 (alternative),
/// !(iri1|iri2|...) (negated property set)
/// </summary>
internal struct PropertyPath
{
    public PathType Type;
    public Term Iri;           // The IRI for simple paths
    public int LeftStart;      // For sequence/alternative: offset of left operand; for NegatedSet: offset of content
    public int LeftLength;     // For sequence/alternative: length of left operand; for NegatedSet: length of content
    public int RightStart;     // For sequence/alternative: offset of right operand
    public int RightLength;

    public static PropertyPath Simple(Term iri) =>
        new() { Type = PathType.None, Iri = iri };

    public static PropertyPath Inverse(Term iri) =>
        new() { Type = PathType.Inverse, Iri = iri };

    public static PropertyPath ZeroOrMore(Term iri) =>
        new() { Type = PathType.ZeroOrMore, Iri = iri };

    public static PropertyPath OneOrMore(Term iri) =>
        new() { Type = PathType.OneOrMore, Iri = iri };

    public static PropertyPath ZeroOrOne(Term iri) =>
        new() { Type = PathType.ZeroOrOne, Iri = iri };

    public static PropertyPath Sequence(int leftStart, int leftLength, int rightStart, int rightLength) =>
        new() { Type = PathType.Sequence, LeftStart = leftStart, LeftLength = leftLength, RightStart = rightStart, RightLength = rightLength };

    public static PropertyPath Alternative(int leftStart, int leftLength, int rightStart, int rightLength) =>
        new() { Type = PathType.Alternative, LeftStart = leftStart, LeftLength = leftLength, RightStart = rightStart, RightLength = rightLength };

    /// <summary>
    /// Creates a negated property set path that matches any predicate EXCEPT those listed.
    /// The content span contains the IRIs separated by | (e.g., "rdf:type|rdfs:label").
    /// </summary>
    public static PropertyPath NegatedSet(int contentStart, int contentLength) =>
        new() { Type = PathType.NegatedSet, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates a grouped zero-or-more path: (path)* - e.g., (p1/p2/p3)*
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath GroupedZeroOrMore(int contentStart, int contentLength) =>
        new() { Type = PathType.GroupedZeroOrMore, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates a grouped one-or-more path: (path)+ - e.g., (p1/p2/p3)+
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath GroupedOneOrMore(int contentStart, int contentLength) =>
        new() { Type = PathType.GroupedOneOrMore, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates a grouped zero-or-one path: (path)? - e.g., (p1/p2)?
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath GroupedZeroOrOne(int contentStart, int contentLength) =>
        new() { Type = PathType.GroupedZeroOrOne, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates an inverse grouped path: ^(path) - e.g., ^(p1/p2)
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath InverseGroup(int contentStart, int contentLength) =>
        new() { Type = PathType.InverseGroup, LeftStart = contentStart, LeftLength = contentLength };
}
