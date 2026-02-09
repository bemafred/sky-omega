namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// SPARQL dataset clause (FROM or FROM NAMED).
/// FROM clauses define the default graph, FROM NAMED define available named graphs.
/// </summary>
public struct DatasetClause
{
    public bool IsNamed;      // false = FROM (default graph), true = FROM NAMED
    public Term GraphIri;     // The graph IRI (Start/Length into source span)

    public static DatasetClause Default(int iriStart, int iriLength) =>
        new() { IsNamed = false, GraphIri = Term.Iri(iriStart, iriLength) };

    public static DatasetClause Named(int iriStart, int iriLength) =>
        new() { IsNamed = true, GraphIri = Term.Iri(iriStart, iriLength) };
}
