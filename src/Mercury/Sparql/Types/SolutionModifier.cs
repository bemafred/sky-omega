namespace SkyOmega.Mercury.Sparql.Types;

internal struct SolutionModifier
{
    public GroupByClause GroupBy;
    public HavingClause Having;
    public OrderByClause OrderBy;
    public LimitOffsetClause LimitOffset;
    public TemporalClause Temporal;
}
