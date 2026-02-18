namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A term in a triple pattern - can be a variable, IRI, literal, blank node, or quoted triple.
/// Uses offsets into the source string for zero-allocation.
/// For QuotedTriple, Start/Length point to the "&lt;&lt; s p o &gt;&gt;" text;
/// nested terms are re-parsed on demand during pattern expansion.
/// </summary>
internal struct Term
{
    public TermType Type;
    public int Start;   // Offset into source
    public int Length;  // Length in source

    public static Term Variable(int start, int length) =>
        new() { Type = TermType.Variable, Start = start, Length = length };

    public static Term Iri(int start, int length) =>
        new() { Type = TermType.Iri, Start = start, Length = length };

    public static Term Literal(int start, int length) =>
        new() { Type = TermType.Literal, Start = start, Length = length };

    public static Term BlankNode(int start, int length) =>
        new() { Type = TermType.BlankNode, Start = start, Length = length };

    /// <summary>
    /// Create a QuotedTriple term. The start/length point to the "&lt;&lt; s p o &gt;&gt;" text.
    /// Nested terms are parsed on demand via ParseQuotedTripleContent.
    /// </summary>
    public static Term QuotedTriple(int start, int length) =>
        new() { Type = TermType.QuotedTriple, Start = start, Length = length };

    public readonly bool IsVariable => Type == TermType.Variable;
    public readonly bool IsIri => Type == TermType.Iri;
    public readonly bool IsLiteral => Type == TermType.Literal;
    public readonly bool IsBlankNode => Type == TermType.BlankNode;
    public readonly bool IsQuotedTriple => Type == TermType.QuotedTriple;
}
