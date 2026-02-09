namespace SkyOmega.Mercury.Sparql.Types;

public struct Query
{
    public QueryType Type;
    public Prologue Prologue;
    public SelectClause SelectClause;
    public ConstructTemplate ConstructTemplate;
    public bool DescribeAll;
    public DatasetClause[] Datasets;
    public WhereClause WhereClause;
    public SolutionModifier SolutionModifier;
    public ValuesClause Values;  // Trailing VALUES clause at end of query
}
