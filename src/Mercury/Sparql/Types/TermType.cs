namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Type of term in a triple pattern.
/// </summary>
internal enum TermType : byte
{
    Variable,
    Iri,
    Literal,
    BlankNode,
    QuotedTriple
}
