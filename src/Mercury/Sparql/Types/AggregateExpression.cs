namespace SkyOmega.Mercury.Sparql.Types;

public struct AggregateExpression
{
    public AggregateFunction Function;
    public int VariableStart;   // The variable being aggregated (e.g., ?x in COUNT(?x))
    public int VariableLength;
    public int AliasStart;      // The alias (e.g., ?count in AS ?count)
    public int AliasLength;
    public bool Distinct;       // COUNT(DISTINCT ?x)
    public int SeparatorStart;  // For GROUP_CONCAT: separator string position
    public int SeparatorLength; // For GROUP_CONCAT: separator string length (0 = default " ")
}
