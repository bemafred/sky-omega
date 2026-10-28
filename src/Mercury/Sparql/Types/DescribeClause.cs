namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// [11] DescribeQuery's <c>( VarOrIri+ | '*' )</c>: what a DESCRIBE describes. The targets are read from the query
/// source with <see cref="Parsing.SparqlParser.DescribeTargetReader"/>, which the parser already used to check them.
/// </summary>
internal struct DescribeClause
{
    /// <summary><c>DESCRIBE *</c>: every variable of the WHERE's solutions.</summary>
    public bool All;

    /// <summary>Offset of the first target; unused for <c>*</c>.</summary>
    public int TargetsStart;
}
