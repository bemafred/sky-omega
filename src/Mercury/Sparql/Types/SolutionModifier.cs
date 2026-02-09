namespace SkyOmega.Mercury.Sparql.Types;

public struct SolutionModifier
{
    public GroupByClause GroupBy;
    public HavingClause Having;
    public OrderByClause OrderBy;
    public int Limit;
    public int Offset;
    public TemporalClause Temporal;
}
