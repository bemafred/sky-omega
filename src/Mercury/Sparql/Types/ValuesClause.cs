namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A VALUES clause ([28] ValuesClause, [61] InlineData): where <c>'VALUES' DataBlock</c> stands in the query source.
/// Its variables and rows are read from there with <see cref="Parsing.SparqlParser.DataBlockReader"/>, which the parser
/// already used to check it, so a clause holds any number of variables and rows.
/// </summary>
internal struct ValuesClause
{
    /// <summary>Offset of the <c>VALUES</c> keyword.</summary>
    public int Start;

    /// <summary>Length up to and including the data's closing <c>'}'</c>; 0 when the query has no such clause.</summary>
    public int Length;

    public readonly bool IsPresent => Length > 0;
}
