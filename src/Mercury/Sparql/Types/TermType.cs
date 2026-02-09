namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Type of term in a triple pattern.
/// </summary>
public enum TermType : byte
{
    Variable,
    Iri,
    Literal,
    BlankNode,
    QuotedTriple
}
