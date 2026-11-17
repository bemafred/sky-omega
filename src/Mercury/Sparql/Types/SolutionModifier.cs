namespace SkyOmega.Mercury.Sparql.Types;

internal struct SolutionModifier
{
    public GroupByClause GroupBy;
    public HavingClause Having;
    public OrderByClause OrderBy;
    public LimitOffsetClause LimitOffset;
    /// <summary>Cognitive SPARQL's INCLUDING FORGOTTEN (ADR-061 D6): every pattern matches forgotten memories too.</summary>
    public bool IncludesForgotten;
}
