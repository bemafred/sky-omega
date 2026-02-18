namespace SkyOmega.Mercury.Sparql.Types;

internal struct HavingClause
{
    public int ExpressionStart;   // Start offset of HAVING expression in source
    public int ExpressionLength;  // Length of expression

    public readonly bool HasHaving => ExpressionLength > 0;
}
