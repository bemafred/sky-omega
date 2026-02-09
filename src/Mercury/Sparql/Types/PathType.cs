namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Type of property path expression.
/// </summary>
public enum PathType : byte
{
    None = 0,        // Simple IRI predicate (not a property path)
    Inverse,         // ^iri - traverse in reverse direction
    ZeroOrMore,      // iri* - zero or more hops
    OneOrMore,       // iri+ - one or more hops
    ZeroOrOne,       // iri? - zero or one hop
    Sequence,        // path1/path2 - sequence of paths
    Alternative,     // path1|path2 - alternative paths
    NegatedSet,      // !(iri1|iri2|...) - matches any predicate except those listed
    GroupedZeroOrMore, // (path)* - zero or more repetitions of grouped path
    GroupedOneOrMore,  // (path)+ - one or more repetitions of grouped path
    GroupedZeroOrOne,  // (path)? - zero or one occurrence of grouped path
    InverseGroup       // ^(path) - inverse of grouped path
}
