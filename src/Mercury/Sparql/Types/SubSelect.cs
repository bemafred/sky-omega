namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A subquery: { SELECT ... WHERE { ... } } inside an outer WHERE clause.
/// Only projected variables from the subquery are visible to the outer query.
/// </summary>
internal struct SubSelect
{
    public const int MaxProjectedVars = 8;
    public const int MaxPatterns = 16;
    public const int MaxFilters = 8;

    // SELECT clause flags
    public bool Distinct;
    public bool SelectAll;  // SELECT * means project all

    // Projected variables
    private int _projectedVarCount;
    private int _pv0Start, _pv0Len, _pv1Start, _pv1Len, _pv2Start, _pv2Len, _pv3Start, _pv3Len;
    private int _pv4Start, _pv4Len, _pv5Start, _pv5Len, _pv6Start, _pv6Len, _pv7Start, _pv7Len;

    // Triple patterns
    private int _patternCount;
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    private TriplePattern _p8, _p9, _p10, _p11, _p12, _p13, _p14, _p15;

    // Filters
    private int _filterCount;
    private FilterExpr _f0, _f1, _f2, _f3, _f4, _f5, _f6, _f7;

    // Solution modifiers
    public int Limit;
    public int Offset;
    public OrderByClause OrderBy;

    // UNION support
    public bool HasUnion;
    public int UnionStartIndex;  // Index of first pattern in union branch

    // Bitmask tracking which patterns are inside GRAPH blocks (vs default graph)
    // Bit N = 1 means pattern N is inside a GRAPH { } block in UNION
    private ushort _graphPatternFlags;

    // VALUES clause (inline bindings at end of subquery)
    public ValuesClause Values;

    // Graph context (for subqueries inside GRAPH clauses)
    // When this is set, the subquery patterns should be evaluated against this graph
    public Term GraphContext;
    public readonly bool HasGraphContext => GraphContext.Type != TermType.Variable || GraphContext.Length > 0;

    // GROUP BY and HAVING support
    public GroupByClause GroupBy;
    public HavingClause Having;

    // Aggregate expressions (e.g., COUNT(?x) AS ?c)
    public const int MaxAggregates = 8;
    private int _aggregateCount;
    private AggregateExpression _a0, _a1, _a2, _a3, _a4, _a5, _a6, _a7;

    // Nested subqueries (boxed to avoid recursive struct definition)
    // For queries like: SELECT * WHERE { { SELECT ?x WHERE { ... } } ?x ?p ?o }
    public const int MaxNestedSubQueries = 2;
    private int _nestedSubQueryCount;
    private object? _nestedSq0;  // Boxed SubSelect
    private object? _nestedSq1;  // Boxed SubSelect

    public readonly int ProjectedVarCount => _projectedVarCount;
    public readonly int PatternCount => _patternCount;
    public readonly int FilterCount => _filterCount;
    public readonly int AggregateCount => _aggregateCount;
    public readonly bool HasPatterns => _patternCount > 0;
    public readonly bool HasFilters => _filterCount > 0;
    public readonly bool HasOrderBy => OrderBy.HasOrderBy;
    public readonly bool HasAggregates => _aggregateCount > 0;
    /// <summary>
    /// Returns true if there are any real aggregate functions (COUNT, SUM, AVG, etc.).
    /// Non-aggregate computed expressions (CONCAT, STR, etc.) with AggregateFunction.None
    /// do not count as real aggregates and should not trigger implicit grouping.
    /// </summary>
    public readonly bool HasRealAggregates
    {
        get
        {
            for (int i = 0; i < _aggregateCount; i++)
            {
                if (GetAggregate(i).Function != AggregateFunction.None)
                    return true;
            }
            return false;
        }
    }
    public readonly bool HasGroupBy => GroupBy.HasGroupBy;
    public readonly bool HasHaving => Having.HasHaving;
    public readonly int FirstBranchPatternCount => HasUnion ? UnionStartIndex : PatternCount;
    public readonly int UnionBranchPatternCount => HasUnion ? PatternCount - UnionStartIndex : 0;
    public readonly int SubQueryCount => _nestedSubQueryCount;
    public readonly bool HasSubQueries => _nestedSubQueryCount > 0;

    /// <summary>
    /// Check if a pattern at the given index is inside a GRAPH block.
    /// Used for UNION branches where some are GRAPH patterns and some are default graph.
    /// </summary>
    public readonly bool IsPatternInGraphBlock(int index) => (_graphPatternFlags & (1 << index)) != 0;

    /// <summary>
    /// Mark a pattern as being inside a GRAPH block.
    /// Called by parser when adding patterns inside GRAPH { } in UNION branches.
    /// </summary>
    public void SetPatternInGraphBlock(int index) => _graphPatternFlags |= (ushort)(1 << index);

    /// <summary>
    /// Returns true if any patterns in UNION are inside GRAPH blocks (some may be default graph).
    /// </summary>
    public readonly bool HasMixedGraphUnion => HasUnion && _graphPatternFlags != 0 && HasGraphContext;

    public void AddProjectedVariable(int start, int length)
    {
        if (_projectedVarCount >= MaxProjectedVars) return;
        SetProjectedVariable(_projectedVarCount++, start, length);
    }

    public readonly (int Start, int Length) GetProjectedVariable(int index)
    {
        return index switch
        {
            0 => (_pv0Start, _pv0Len), 1 => (_pv1Start, _pv1Len),
            2 => (_pv2Start, _pv2Len), 3 => (_pv3Start, _pv3Len),
            4 => (_pv4Start, _pv4Len), 5 => (_pv5Start, _pv5Len),
            6 => (_pv6Start, _pv6Len), 7 => (_pv7Start, _pv7Len),
            _ => (0, 0)
        };
    }

    private void SetProjectedVariable(int index, int start, int length)
    {
        switch (index)
        {
            case 0: _pv0Start = start; _pv0Len = length; break;
            case 1: _pv1Start = start; _pv1Len = length; break;
            case 2: _pv2Start = start; _pv2Len = length; break;
            case 3: _pv3Start = start; _pv3Len = length; break;
            case 4: _pv4Start = start; _pv4Len = length; break;
            case 5: _pv5Start = start; _pv5Len = length; break;
            case 6: _pv6Start = start; _pv6Len = length; break;
            case 7: _pv7Start = start; _pv7Len = length; break;
        }
    }

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        SetPattern(_patternCount++, pattern);
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0, 1 => _p1, 2 => _p2, 3 => _p3,
            4 => _p4, 5 => _p5, 6 => _p6, 7 => _p7,
            8 => _p8, 9 => _p9, 10 => _p10, 11 => _p11,
            12 => _p12, 13 => _p13, 14 => _p14, 15 => _p15,
            _ => default
        };
    }

    private void SetPattern(int index, TriplePattern pattern)
    {
        switch (index)
        {
            case 0: _p0 = pattern; break; case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break; case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break; case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break; case 7: _p7 = pattern; break;
            case 8: _p8 = pattern; break; case 9: _p9 = pattern; break;
            case 10: _p10 = pattern; break; case 11: _p11 = pattern; break;
            case 12: _p12 = pattern; break; case 13: _p13 = pattern; break;
            case 14: _p14 = pattern; break; case 15: _p15 = pattern; break;
        }
    }

    public void AddFilter(FilterExpr filter)
    {
        if (_filterCount >= MaxFilters) return;
        SetFilter(_filterCount++, filter);
    }

    public readonly FilterExpr GetFilter(int index)
    {
        return index switch
        {
            0 => _f0, 1 => _f1, 2 => _f2, 3 => _f3,
            4 => _f4, 5 => _f5, 6 => _f6, 7 => _f7,
            _ => default
        };
    }

    private void SetFilter(int index, FilterExpr filter)
    {
        switch (index)
        {
            case 0: _f0 = filter; break; case 1: _f1 = filter; break;
            case 2: _f2 = filter; break; case 3: _f3 = filter; break;
            case 4: _f4 = filter; break; case 5: _f5 = filter; break;
            case 6: _f6 = filter; break; case 7: _f7 = filter; break;
        }
    }

    public void AddAggregate(AggregateExpression agg)
    {
        if (_aggregateCount >= MaxAggregates) return;
        SetAggregate(_aggregateCount++, agg);
    }

    public readonly AggregateExpression GetAggregate(int index)
    {
        return index switch
        {
            0 => _a0, 1 => _a1, 2 => _a2, 3 => _a3,
            4 => _a4, 5 => _a5, 6 => _a6, 7 => _a7,
            _ => default
        };
    }

    private void SetAggregate(int index, AggregateExpression agg)
    {
        switch (index)
        {
            case 0: _a0 = agg; break; case 1: _a1 = agg; break;
            case 2: _a2 = agg; break; case 3: _a3 = agg; break;
            case 4: _a4 = agg; break; case 5: _a5 = agg; break;
            case 6: _a6 = agg; break; case 7: _a7 = agg; break;
        }
    }

    /// <summary>
    /// Add a nested subquery to this SubSelect.
    /// Used for queries like: SELECT * WHERE { { SELECT ?x WHERE { ... } } }
    /// </summary>
    public void AddSubQuery(SubSelect subQuery)
    {
        if (_nestedSubQueryCount >= MaxNestedSubQueries) return;
        switch (_nestedSubQueryCount++)
        {
            case 0: _nestedSq0 = subQuery; break;
            case 1: _nestedSq1 = subQuery; break;
        }
    }

    /// <summary>
    /// Get a nested subquery by index.
    /// </summary>
    public readonly SubSelect GetSubQuery(int index)
    {
        return index switch
        {
            0 => _nestedSq0 is SubSelect sq0 ? sq0 : default,
            1 => _nestedSq1 is SubSelect sq1 ? sq1 : default,
            _ => default
        };
    }
}
