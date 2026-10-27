namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// The clauses of a subquery, <c>{ SELECT ... WHERE { ... } ... }</c> inside an outer WHERE clause: its projection and
/// its solution modifiers. Its WHERE is not here: it is the child subtree of the query's SubSelectHeader, parsed once
/// with every other group (ADR-055 D4). Only projected variables from the subquery are visible to the outer query.
/// </summary>
internal struct SubSelect
{
    // [9] SelectClause: DISTINCT / REDUCED, *, the projected variables and expressions. Parsed by the same
    // ParseSelectClause as a query's, so a sub-SELECT's projection has the same grammar and the same capacities.
    public SelectClause Select;

    // Solution modifiers
    public GroupByClause GroupBy;
    public HavingClause Having;
    public OrderByClause OrderBy;
    public LimitOffsetClause LimitOffset;

    // VALUES clause (inline bindings at end of subquery)
    public ValuesClause Values;
}
