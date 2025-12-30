using System;

namespace SkyOmega.Mercury.Sparql;

public enum QueryType
{
    Unknown,
    Select,
    Construct,
    Describe,
    Ask,
    // SPARQL Update operations
    InsertData,
    DeleteData,
    DeleteWhere,
    Modify,  // DELETE/INSERT ... WHERE
    Load,
    Clear,
    Create,
    Drop,
    Copy,
    Move,
    Add
}

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
}

public struct Prologue
{
    private const int MaxPrefixes = 32;
    private int _prefixCount;
    private unsafe fixed byte _prefixData[2048]; // Inline storage

    public int BaseUriStart;   // Start offset in source span
    public int BaseUriLength;  // Length in source span

    public void AddPrefix(ReadOnlySpan<char> prefix, ReadOnlySpan<char> iri)
    {
        // Store prefixes in inline buffer
        _prefixCount++;
    }
}

public struct SelectClause
{
    public const int MaxAggregates = 8;
    public bool Distinct;
    public bool Reduced;
    public bool SelectAll;

    private int _aggregateCount;
    // Inline storage for up to 8 aggregate expressions
    private AggregateExpression _a0, _a1, _a2, _a3, _a4, _a5, _a6, _a7;

    public readonly int AggregateCount => _aggregateCount;
    public readonly bool HasAggregates => _aggregateCount > 0;

    public void AddAggregate(AggregateExpression agg)
    {
        if (_aggregateCount >= MaxAggregates)
            throw new SparqlParseException("Too many aggregate expressions (max 8)");

        switch (_aggregateCount)
        {
            case 0: _a0 = agg; break;
            case 1: _a1 = agg; break;
            case 2: _a2 = agg; break;
            case 3: _a3 = agg; break;
            case 4: _a4 = agg; break;
            case 5: _a5 = agg; break;
            case 6: _a6 = agg; break;
            case 7: _a7 = agg; break;
        }
        _aggregateCount++;
    }

    public readonly AggregateExpression GetAggregate(int index)
    {
        return index switch
        {
            0 => _a0,
            1 => _a1,
            2 => _a2,
            3 => _a3,
            4 => _a4,
            5 => _a5,
            6 => _a6,
            7 => _a7,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

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

public enum AggregateFunction
{
    None = 0,
    Count,
    Sum,
    Avg,
    Min,
    Max,
    GroupConcat,
    Sample
}

/// <summary>
/// SPARQL dataset clause (FROM or FROM NAMED).
/// FROM clauses define the default graph, FROM NAMED define available named graphs.
/// </summary>
public struct DatasetClause
{
    public bool IsNamed;      // false = FROM (default graph), true = FROM NAMED
    public Term GraphIri;     // The graph IRI (Start/Length into source span)

    public static DatasetClause Default(int iriStart, int iriLength) =>
        new() { IsNamed = false, GraphIri = Term.Iri(iriStart, iriLength) };

    public static DatasetClause Named(int iriStart, int iriLength) =>
        new() { IsNamed = true, GraphIri = Term.Iri(iriStart, iriLength) };
}

public struct WhereClause
{
    public GraphPattern Pattern;
}

/// <summary>
/// A graph pattern containing triple patterns and filters.
/// Uses inline storage for zero-allocation parsing.
/// </summary>
public struct GraphPattern
{
    public const int MaxTriplePatterns = 32;
    public const int MaxFilters = 16;
    public const int MaxBinds = 8;
    public const int MaxMinusPatterns = 8;
    public const int MaxExistsFilters = 4;
    public const int MaxGraphClauses = 4;
    public const int MaxSubQueries = 2;
    public const int MaxServiceClauses = 2;

    private int _patternCount;
    private int _filterCount;
    private int _bindCount;
    private int _minusPatternCount;
    private int _existsFilterCount;
    private int _graphClauseCount;
    private int _subQueryCount;
    private int _serviceClauseCount;
    private uint _optionalFlags; // Bitmask: bit N = 1 means pattern N is optional
    private int _unionStartIndex; // If > 0, patterns from this index are the UNION branch

    // Inline storage for triple patterns (32 * 24 bytes = 768 bytes)
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    private TriplePattern _p8, _p9, _p10, _p11, _p12, _p13, _p14, _p15;
    private TriplePattern _p16, _p17, _p18, _p19, _p20, _p21, _p22, _p23;
    private TriplePattern _p24, _p25, _p26, _p27, _p28, _p29, _p30, _p31;

    // Inline storage for filter expression offsets (16 * 8 bytes = 128 bytes)
    private FilterExpr _f0, _f1, _f2, _f3, _f4, _f5, _f6, _f7;
    private FilterExpr _f8, _f9, _f10, _f11, _f12, _f13, _f14, _f15;

    // Inline storage for bind expressions (8 * 16 bytes = 128 bytes)
    private BindExpr _b0, _b1, _b2, _b3, _b4, _b5, _b6, _b7;

    // Inline storage for MINUS patterns (8 * 24 bytes = 192 bytes)
    private TriplePattern _m0, _m1, _m2, _m3, _m4, _m5, _m6, _m7;

    // Inline storage for EXISTS/NOT EXISTS filters (4 * ~100 bytes)
    private ExistsFilter _e0, _e1, _e2, _e3;

    // Inline storage for GRAPH clauses (4 * ~200 bytes)
    private GraphClause _g0, _g1, _g2, _g3;

    // Inline storage for subqueries (2 * ~500 bytes)
    private SubSelect _sq0, _sq1;

    // Inline storage for SERVICE clauses (2 * ~200 bytes)
    private ServiceClause _svc0, _svc1;

    // VALUES clause storage
    private ValuesClause _values;

    public readonly int PatternCount => _patternCount;
    public readonly int FilterCount => _filterCount;
    public readonly int BindCount => _bindCount;
    public readonly int MinusPatternCount => _minusPatternCount;
    public readonly int ExistsFilterCount => _existsFilterCount;
    public readonly int GraphClauseCount => _graphClauseCount;
    public readonly int SubQueryCount => _subQueryCount;
    public readonly int ServiceClauseCount => _serviceClauseCount;
    public readonly bool HasBinds => _bindCount > 0;
    public readonly bool HasMinus => _minusPatternCount > 0;
    public readonly bool HasExists => _existsFilterCount > 0;
    public readonly bool HasGraph => _graphClauseCount > 0;
    public readonly bool HasSubQueries => _subQueryCount > 0;
    public readonly bool HasService => _serviceClauseCount > 0;
    public readonly bool HasValues => _values.HasValues;
    public readonly bool HasOptionalPatterns => _optionalFlags != 0;
    public readonly bool HasUnion => _unionStartIndex > 0;

    public readonly ValuesClause Values => _values;

    /// <summary>
    /// Count of patterns in the first branch (before UNION).
    /// </summary>
    public readonly int FirstBranchPatternCount => HasUnion ? _unionStartIndex : _patternCount;

    /// <summary>
    /// Count of patterns in the UNION branch.
    /// </summary>
    public readonly int UnionBranchPatternCount => HasUnion ? _patternCount - _unionStartIndex : 0;

    /// <summary>
    /// Get a pattern from the UNION branch.
    /// </summary>
    public readonly TriplePattern GetUnionPattern(int index) => GetPattern(_unionStartIndex + index);

    /// <summary>
    /// Count of required (non-optional) patterns.
    /// </summary>
    public readonly int RequiredPatternCount
    {
        get
        {
            int count = 0;
            // Only count patterns in first branch (before UNION)
            int limit = HasUnion ? _unionStartIndex : _patternCount;
            for (int i = 0; i < limit; i++)
            {
                if (!IsOptional(i)) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Mark the start of UNION patterns.
    /// </summary>
    public void StartUnionBranch()
    {
        _unionStartIndex = _patternCount;
    }

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxTriplePatterns) return;
        SetPattern(_patternCount++, pattern);
    }

    /// <summary>
    /// Add a pattern from an OPTIONAL clause.
    /// </summary>
    public void AddOptionalPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxTriplePatterns) return;
        _optionalFlags |= (1u << _patternCount);
        SetPattern(_patternCount++, pattern);
    }

    /// <summary>
    /// Check if a pattern at the given index is optional.
    /// </summary>
    public readonly bool IsOptional(int index) => (_optionalFlags & (1u << index)) != 0;

    public void AddFilter(FilterExpr filter)
    {
        if (_filterCount >= MaxFilters) return;
        SetFilter(_filterCount++, filter);
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0, 1 => _p1, 2 => _p2, 3 => _p3,
            4 => _p4, 5 => _p5, 6 => _p6, 7 => _p7,
            8 => _p8, 9 => _p9, 10 => _p10, 11 => _p11,
            12 => _p12, 13 => _p13, 14 => _p14, 15 => _p15,
            16 => _p16, 17 => _p17, 18 => _p18, 19 => _p19,
            20 => _p20, 21 => _p21, 22 => _p22, 23 => _p23,
            24 => _p24, 25 => _p25, 26 => _p26, 27 => _p27,
            28 => _p28, 29 => _p29, 30 => _p30, 31 => _p31,
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
            case 16: _p16 = pattern; break; case 17: _p17 = pattern; break;
            case 18: _p18 = pattern; break; case 19: _p19 = pattern; break;
            case 20: _p20 = pattern; break; case 21: _p21 = pattern; break;
            case 22: _p22 = pattern; break; case 23: _p23 = pattern; break;
            case 24: _p24 = pattern; break; case 25: _p25 = pattern; break;
            case 26: _p26 = pattern; break; case 27: _p27 = pattern; break;
            case 28: _p28 = pattern; break; case 29: _p29 = pattern; break;
            case 30: _p30 = pattern; break; case 31: _p31 = pattern; break;
        }
    }

    public readonly FilterExpr GetFilter(int index)
    {
        return index switch
        {
            0 => _f0, 1 => _f1, 2 => _f2, 3 => _f3,
            4 => _f4, 5 => _f5, 6 => _f6, 7 => _f7,
            8 => _f8, 9 => _f9, 10 => _f10, 11 => _f11,
            12 => _f12, 13 => _f13, 14 => _f14, 15 => _f15,
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
            case 8: _f8 = filter; break; case 9: _f9 = filter; break;
            case 10: _f10 = filter; break; case 11: _f11 = filter; break;
            case 12: _f12 = filter; break; case 13: _f13 = filter; break;
            case 14: _f14 = filter; break; case 15: _f15 = filter; break;
        }
    }

    public void AddBind(BindExpr bind)
    {
        if (_bindCount >= MaxBinds) return;
        SetBind(_bindCount++, bind);
    }

    public readonly BindExpr GetBind(int index)
    {
        return index switch
        {
            0 => _b0, 1 => _b1, 2 => _b2, 3 => _b3,
            4 => _b4, 5 => _b5, 6 => _b6, 7 => _b7,
            _ => default
        };
    }

    private void SetBind(int index, BindExpr bind)
    {
        switch (index)
        {
            case 0: _b0 = bind; break; case 1: _b1 = bind; break;
            case 2: _b2 = bind; break; case 3: _b3 = bind; break;
            case 4: _b4 = bind; break; case 5: _b5 = bind; break;
            case 6: _b6 = bind; break; case 7: _b7 = bind; break;
        }
    }

    public void AddMinusPattern(TriplePattern pattern)
    {
        if (_minusPatternCount >= MaxMinusPatterns) return;
        SetMinusPattern(_minusPatternCount++, pattern);
    }

    public readonly TriplePattern GetMinusPattern(int index)
    {
        return index switch
        {
            0 => _m0, 1 => _m1, 2 => _m2, 3 => _m3,
            4 => _m4, 5 => _m5, 6 => _m6, 7 => _m7,
            _ => default
        };
    }

    private void SetMinusPattern(int index, TriplePattern pattern)
    {
        switch (index)
        {
            case 0: _m0 = pattern; break; case 1: _m1 = pattern; break;
            case 2: _m2 = pattern; break; case 3: _m3 = pattern; break;
            case 4: _m4 = pattern; break; case 5: _m5 = pattern; break;
            case 6: _m6 = pattern; break; case 7: _m7 = pattern; break;
        }
    }

    public void AddExistsFilter(ExistsFilter filter)
    {
        if (_existsFilterCount >= MaxExistsFilters) return;
        SetExistsFilter(_existsFilterCount++, filter);
    }

    public readonly ExistsFilter GetExistsFilter(int index)
    {
        return index switch
        {
            0 => _e0,
            1 => _e1,
            2 => _e2,
            3 => _e3,
            _ => default
        };
    }

    private void SetExistsFilter(int index, ExistsFilter filter)
    {
        switch (index)
        {
            case 0: _e0 = filter; break;
            case 1: _e1 = filter; break;
            case 2: _e2 = filter; break;
            case 3: _e3 = filter; break;
        }
    }

    public void AddGraphClause(GraphClause clause)
    {
        if (_graphClauseCount >= MaxGraphClauses) return;
        SetGraphClause(_graphClauseCount++, clause);
    }

    public readonly GraphClause GetGraphClause(int index)
    {
        return index switch
        {
            0 => _g0,
            1 => _g1,
            2 => _g2,
            3 => _g3,
            _ => default
        };
    }

    private void SetGraphClause(int index, GraphClause clause)
    {
        switch (index)
        {
            case 0: _g0 = clause; break;
            case 1: _g1 = clause; break;
            case 2: _g2 = clause; break;
            case 3: _g3 = clause; break;
        }
    }

    public void SetValues(ValuesClause values)
    {
        _values = values;
    }

    public void AddSubQuery(SubSelect subQuery)
    {
        if (_subQueryCount >= MaxSubQueries) return;
        SetSubQuery(_subQueryCount++, subQuery);
    }

    public readonly SubSelect GetSubQuery(int index)
    {
        return index switch
        {
            0 => _sq0,
            1 => _sq1,
            _ => default
        };
    }

    private void SetSubQuery(int index, SubSelect subQuery)
    {
        switch (index)
        {
            case 0: _sq0 = subQuery; break;
            case 1: _sq1 = subQuery; break;
        }
    }

    public void AddServiceClause(ServiceClause clause)
    {
        if (_serviceClauseCount >= MaxServiceClauses) return;
        SetServiceClause(_serviceClauseCount++, clause);
    }

    public readonly ServiceClause GetServiceClause(int index)
    {
        return index switch
        {
            0 => _svc0,
            1 => _svc1,
            _ => default
        };
    }

    private void SetServiceClause(int index, ServiceClause clause)
    {
        switch (index)
        {
            case 0: _svc0 = clause; break;
            case 1: _svc1 = clause; break;
        }
    }
}

/// <summary>
/// A triple pattern with subject, predicate, and object terms.
/// Supports property paths in the predicate position.
/// </summary>
public struct TriplePattern
{
    public Term Subject;
    public Term Predicate;
    public Term Object;
    public PropertyPath Path;  // Used when HasPropertyPath is true

    public readonly bool HasPropertyPath => Path.Type != PathType.None;
}

/// <summary>
/// A property path expression for SPARQL 1.1 property paths.
/// Supports: ^iri (inverse), iri+ (one or more), iri* (zero or more),
/// iri? (zero or one), path1/path2 (sequence), path1|path2 (alternative)
/// </summary>
public struct PropertyPath
{
    public PathType Type;
    public Term Iri;           // The IRI for simple paths
    public int LeftStart;      // For sequence/alternative: offset of left operand
    public int LeftLength;
    public int RightStart;     // For sequence/alternative: offset of right operand
    public int RightLength;

    public static PropertyPath Simple(Term iri) =>
        new() { Type = PathType.None, Iri = iri };

    public static PropertyPath Inverse(Term iri) =>
        new() { Type = PathType.Inverse, Iri = iri };

    public static PropertyPath ZeroOrMore(Term iri) =>
        new() { Type = PathType.ZeroOrMore, Iri = iri };

    public static PropertyPath OneOrMore(Term iri) =>
        new() { Type = PathType.OneOrMore, Iri = iri };

    public static PropertyPath ZeroOrOne(Term iri) =>
        new() { Type = PathType.ZeroOrOne, Iri = iri };

    public static PropertyPath Sequence(int leftStart, int leftLength, int rightStart, int rightLength) =>
        new() { Type = PathType.Sequence, LeftStart = leftStart, LeftLength = leftLength, RightStart = rightStart, RightLength = rightLength };

    public static PropertyPath Alternative(int leftStart, int leftLength, int rightStart, int rightLength) =>
        new() { Type = PathType.Alternative, LeftStart = leftStart, LeftLength = leftLength, RightStart = rightStart, RightLength = rightLength };
}

/// <summary>
/// Type of property path expression.
/// </summary>
public enum PathType : byte
{
    None = 0,        // Simple IRI predicate (not a property path)
    Inverse,         // ^iri - traverse in reverse direction
    ZeroOrMore,      // iri* - zero or more hops
    OneOrMore,       // iri+ - one or more hops
    ZeroOrOne,       // iri? - zero or one hop
    Sequence,        // path1/path2 - sequence of paths
    Alternative      // path1|path2 - alternative paths
}

/// <summary>
/// A term in a triple pattern - can be a variable, IRI, literal, blank node, or quoted triple.
/// Uses offsets into the source string for zero-allocation.
/// For QuotedTriple, Start/Length point to the "<< s p o >>" text;
/// nested terms are re-parsed on demand during pattern expansion.
/// </summary>
public struct Term
{
    public TermType Type;
    public int Start;   // Offset into source
    public int Length;  // Length in source

    public static Term Variable(int start, int length) =>
        new() { Type = TermType.Variable, Start = start, Length = length };

    public static Term Iri(int start, int length) =>
        new() { Type = TermType.Iri, Start = start, Length = length };

    public static Term Literal(int start, int length) =>
        new() { Type = TermType.Literal, Start = start, Length = length };

    public static Term BlankNode(int start, int length) =>
        new() { Type = TermType.BlankNode, Start = start, Length = length };

    /// <summary>
    /// Create a QuotedTriple term. The start/length point to the "<< s p o >>" text.
    /// Nested terms are parsed on demand via ParseQuotedTripleContent.
    /// </summary>
    public static Term QuotedTriple(int start, int length) =>
        new() { Type = TermType.QuotedTriple, Start = start, Length = length };

    public readonly bool IsVariable => Type == TermType.Variable;
    public readonly bool IsIri => Type == TermType.Iri;
    public readonly bool IsLiteral => Type == TermType.Literal;
    public readonly bool IsBlankNode => Type == TermType.BlankNode;
    public readonly bool IsQuotedTriple => Type == TermType.QuotedTriple;
}

/// <summary>
/// Type of term in a triple pattern.
/// </summary>
public enum TermType : byte
{
    Variable,
    Iri,
    Literal,
    BlankNode,
    QuotedTriple
}

/// <summary>
/// A FILTER expression reference (offset into source).
/// </summary>
public struct FilterExpr
{
    public int Start;   // Offset into source (after "FILTER")
    public int Length;  // Length of expression
}

/// <summary>
/// An EXISTS or NOT EXISTS filter: FILTER [NOT] EXISTS { pattern }
/// Stores the pattern for later evaluation against the store.
/// </summary>
public struct ExistsFilter
{
    public const int MaxPatterns = 4;

    public bool Negated;         // true for NOT EXISTS, false for EXISTS
    private int _patternCount;

    // Inline storage for up to 4 triple patterns
    private TriplePattern _p0, _p1, _p2, _p3;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            _ => default
        };
    }
}

/// <summary>
/// A GRAPH clause: GRAPH &lt;iri&gt; { patterns } or GRAPH ?var { patterns }
/// Stores the graph term and patterns to be evaluated within that graph context.
/// </summary>
public struct GraphClause
{
    public const int MaxPatterns = 8;

    public Term Graph;           // The graph IRI or variable
    private int _patternCount;

    // Inline storage for up to 8 triple patterns
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;
    public readonly bool IsVariable => Graph.IsVariable;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break;
            case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break;
            case 7: _p7 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            4 => _p4,
            5 => _p5,
            6 => _p6,
            7 => _p7,
            _ => default
        };
    }
}

/// <summary>
/// A SERVICE clause: SERVICE [SILENT] &lt;uri&gt; { patterns } or SERVICE [SILENT] ?var { patterns }
/// Stores the endpoint term and patterns to be sent to a remote SPARQL endpoint.
/// </summary>
public struct ServiceClause
{
    public const int MaxPatterns = 8;

    public bool Silent;           // SILENT modifier - ignore failures
    public Term Endpoint;         // The endpoint IRI or variable
    private int _patternCount;

    // Inline storage for up to 8 triple patterns
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;
    public readonly bool IsVariable => Endpoint.IsVariable;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break;
            case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break;
            case 7: _p7 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            4 => _p4,
            5 => _p5,
            6 => _p6,
            7 => _p7,
            _ => default
        };
    }
}

/// <summary>
/// A BIND expression: BIND(expression AS ?variable)
/// </summary>
public struct BindExpr
{
    public int ExprStart;    // Start of expression
    public int ExprLength;   // Length of expression
    public int VarStart;     // Start of target variable (including ?)
    public int VarLength;    // Length of target variable
}

/// <summary>
/// A VALUES clause: VALUES ?var { value1 value2 ... }
/// Stores a single variable and up to 8 inline values.
/// </summary>
public struct ValuesClause
{
    public const int MaxValues = 8;

    public int VarStart;     // Start of variable name (including ?)
    public int VarLength;    // Length of variable name
    private int _valueCount;

    // Inline storage for value offsets (8 values * 2 ints = 64 bytes)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;

    public readonly int ValueCount => _valueCount;
    public readonly bool HasValues => _valueCount > 0;

    public void AddValue(int start, int length)
    {
        if (_valueCount >= MaxValues) return;
        SetValue(_valueCount++, start, length);
    }

    public readonly (int Start, int Length) GetValue(int index)
    {
        return index switch
        {
            0 => (_v0Start, _v0Len), 1 => (_v1Start, _v1Len),
            2 => (_v2Start, _v2Len), 3 => (_v3Start, _v3Len),
            4 => (_v4Start, _v4Len), 5 => (_v5Start, _v5Len),
            6 => (_v6Start, _v6Len), 7 => (_v7Start, _v7Len),
            _ => (0, 0)
        };
    }

    private void SetValue(int index, int start, int length)
    {
        switch (index)
        {
            case 0: _v0Start = start; _v0Len = length; break;
            case 1: _v1Start = start; _v1Len = length; break;
            case 2: _v2Start = start; _v2Len = length; break;
            case 3: _v3Start = start; _v3Len = length; break;
            case 4: _v4Start = start; _v4Len = length; break;
            case 5: _v5Start = start; _v5Len = length; break;
            case 6: _v6Start = start; _v6Len = length; break;
            case 7: _v7Start = start; _v7Len = length; break;
        }
    }
}

/// <summary>
/// A subquery: { SELECT ... WHERE { ... } } inside an outer WHERE clause.
/// Only projected variables from the subquery are visible to the outer query.
/// </summary>
public struct SubSelect
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

    public readonly int ProjectedVarCount => _projectedVarCount;
    public readonly int PatternCount => _patternCount;
    public readonly int FilterCount => _filterCount;
    public readonly bool HasPatterns => _patternCount > 0;
    public readonly bool HasFilters => _filterCount > 0;
    public readonly bool HasOrderBy => OrderBy.HasOrderBy;

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
}

public struct ConstructTemplate
{
    public const int MaxPatterns = 16;
    private int _patternCount;

    // Inline storage for up to 16 template triple patterns
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    private TriplePattern _p8, _p9, _p10, _p11, _p12, _p13, _p14, _p15;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns)
            throw new SparqlParseException("Too many patterns in CONSTRUCT template (max 16)");

        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break;
            case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break;
            case 7: _p7 = pattern; break;
            case 8: _p8 = pattern; break;
            case 9: _p9 = pattern; break;
            case 10: _p10 = pattern; break;
            case 11: _p11 = pattern; break;
            case 12: _p12 = pattern; break;
            case 13: _p13 = pattern; break;
            case 14: _p14 = pattern; break;
            case 15: _p15 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            4 => _p4,
            5 => _p5,
            6 => _p6,
            7 => _p7,
            8 => _p8,
            9 => _p9,
            10 => _p10,
            11 => _p11,
            12 => _p12,
            13 => _p13,
            14 => _p14,
            15 => _p15,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public struct SolutionModifier
{
    public GroupByClause GroupBy;
    public HavingClause Having;
    public OrderByClause OrderBy;
    public int Limit;
    public int Offset;
    public TemporalClause Temporal;
}

/// <summary>
/// Temporal query mode for bitemporal queries.
/// </summary>
public enum TemporalQueryMode
{
    Current,      // Default: valid at UtcNow
    AsOf,         // Point-in-time: valid at specific time
    During,       // Range: changed during period
    AllVersions   // Evolution: all versions ever
}

/// <summary>
/// Temporal clause parsed from SPARQL query.
/// Supports: AS OF, DURING, ALL VERSIONS
/// </summary>
public struct TemporalClause
{
    public TemporalQueryMode Mode;

    // For AS OF: single timestamp (TimeStart only)
    // For DURING: range [TimeStart, TimeEnd]
    public int TimeStartStart;   // Offset into source for start time literal
    public int TimeStartLength;
    public int TimeEndStart;     // Offset for end time (DURING only)
    public int TimeEndLength;

    public readonly bool HasTemporal => Mode != TemporalQueryMode.Current;
}

public struct HavingClause
{
    public int ExpressionStart;   // Start offset of HAVING expression in source
    public int ExpressionLength;  // Length of expression

    public readonly bool HasHaving => ExpressionLength > 0;
}

public struct GroupByClause
{
    public const int MaxVariables = 8;
    private int _count;

    // Inline storage for up to 8 grouping variables (start, length pairs)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;

    public readonly int Count => _count;
    public readonly bool HasGroupBy => _count > 0;

    public void AddVariable(int start, int length)
    {
        if (_count >= MaxVariables)
            throw new SparqlParseException("Too many GROUP BY variables (max 8)");

        switch (_count)
        {
            case 0: _v0Start = start; _v0Len = length; break;
            case 1: _v1Start = start; _v1Len = length; break;
            case 2: _v2Start = start; _v2Len = length; break;
            case 3: _v3Start = start; _v3Len = length; break;
            case 4: _v4Start = start; _v4Len = length; break;
            case 5: _v5Start = start; _v5Len = length; break;
            case 6: _v6Start = start; _v6Len = length; break;
            case 7: _v7Start = start; _v7Len = length; break;
        }
        _count++;
    }

    public readonly (int Start, int Length) GetVariable(int index)
    {
        return index switch
        {
            0 => (_v0Start, _v0Len),
            1 => (_v1Start, _v1Len),
            2 => (_v2Start, _v2Len),
            3 => (_v3Start, _v3Len),
            4 => (_v4Start, _v4Len),
            5 => (_v5Start, _v5Len),
            6 => (_v6Start, _v6Len),
            7 => (_v7Start, _v7Len),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public struct OrderByClause
{
    // Store up to 4 order conditions inline
    private OrderCondition _cond0, _cond1, _cond2, _cond3;
    private int _count;

    public readonly int Count => _count;
    public readonly bool HasOrderBy => _count > 0;

    public void AddCondition(int variableStart, int variableLength, OrderDirection direction)
    {
        var cond = new OrderCondition(variableStart, variableLength, direction);
        switch (_count)
        {
            case 0: _cond0 = cond; break;
            case 1: _cond1 = cond; break;
            case 2: _cond2 = cond; break;
            case 3: _cond3 = cond; break;
            default: return; // Ignore beyond 4
        }
        _count++;
    }

    public readonly OrderCondition GetCondition(int index)
    {
        return index switch
        {
            0 => _cond0,
            1 => _cond1,
            2 => _cond2,
            3 => _cond3,
            _ => default
        };
    }
}

public readonly struct OrderCondition
{
    public readonly int VariableStart;
    public readonly int VariableLength;
    public readonly OrderDirection Direction;

    public OrderCondition(int start, int length, OrderDirection direction)
    {
        VariableStart = start;
        VariableLength = length;
        Direction = direction;
    }
}

public enum OrderDirection
{
    Ascending,
    Descending
}

/// <summary>
/// Binding table for variable bindings during query execution.
/// Zero-allocation design using stackalloc buffers.
/// </summary>
public ref struct BindingTable
{
    private Span<Binding> _bindings;
    private int _count;
    private Span<char> _stringBuffer;
    private int _stringOffset;

    public BindingTable(Span<Binding> storage)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = Span<char>.Empty;
        _stringOffset = 0;
    }

    public BindingTable(Span<Binding> storage, Span<char> stringBuffer)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = stringBuffer;
        _stringOffset = 0;
    }

    /// <summary>
    /// Bind an integer value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, long value)
    {
        if (_count >= _bindings.Length) return;

        // Format the value as string and store in buffer
        Span<char> temp = stackalloc char[24];
        if (!value.TryFormat(temp, out int written))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Integer;
        binding.IntegerValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        _stringOffset += written;
    }

    /// <summary>
    /// Bind a double value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, double value)
    {
        if (_count >= _bindings.Length) return;

        // Format the value as string and store in buffer
        Span<char> temp = stackalloc char[32];
        if (!value.TryFormat(temp, out int written))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Double;
        binding.DoubleValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        _stringOffset += written;
    }

    /// <summary>
    /// Bind a boolean value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, bool value)
    {
        if (_count >= _bindings.Length) return;

        // Store string representation
        var str = value ? "true" : "false";
        var len = str.Length;
        if (_stringOffset + len > _stringBuffer.Length) return;

        str.AsSpan().CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Boolean;
        binding.BooleanValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = len;
        _stringOffset += len;
    }

    /// <summary>
    /// Bind a string value to a variable.
    /// Copies the string into the internal buffer.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.String;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Bind a URI value to a variable.
    /// Copies the URI into the internal buffer.
    /// </summary>
    public void BindUri(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Uri;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Bind a string value using a pre-computed hash.
    /// Used for ORDER BY result reconstruction.
    /// </summary>
    public void BindWithHash(int variableNameHash, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = variableNameHash;
        binding.Type = BindingValueType.String;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Try to get the binding for a variable.
    /// Returns the index if found, -1 otherwise.
    /// </summary>
    public readonly int FindBinding(ReadOnlySpan<char> variableName)
    {
        var hash = ComputeHash(variableName);
        for (int i = 0; i < _count; i++)
        {
            if (_bindings[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the index of a binding by its pre-computed hash.
    /// </summary>
    public readonly int FindBindingByHash(int hash)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_bindings[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Get the type of the binding at the given index.
    /// </summary>
    public readonly BindingValueType GetType(int index) => _bindings[index].Type;

    /// <summary>
    /// Get the integer value at the given index.
    /// </summary>
    public readonly long GetInteger(int index) => _bindings[index].IntegerValue;

    /// <summary>
    /// Get the double value at the given index.
    /// </summary>
    public readonly double GetDouble(int index) => _bindings[index].DoubleValue;

    /// <summary>
    /// Get the boolean value at the given index.
    /// </summary>
    public readonly bool GetBoolean(int index) => _bindings[index].BooleanValue;

    /// <summary>
    /// Get the string value at the given index.
    /// </summary>
    public readonly ReadOnlySpan<char> GetString(int index)
    {
        ref readonly var binding = ref _bindings[index];
        return _stringBuffer.Slice(binding.StringOffset, binding.StringLength);
    }

    /// <summary>
    /// Get the variable name hash at the given index.
    /// </summary>
    public readonly int GetVariableHash(int index) => _bindings[index].VariableNameHash;

    /// <summary>
    /// Clear all bindings for reuse with next row.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _stringOffset = 0;
    }

    /// <summary>
    /// Truncate bindings to a previous count.
    /// Used for backtracking in multi-pattern joins.
    /// Note: String buffer space is not reclaimed (acceptable for fixed buffer).
    /// </summary>
    public void TruncateTo(int count)
    {
        if (count < _count)
        {
            _count = count;
            // String buffer is not truncated - we leave gaps, which is fine for fixed buffer
            // In a more sophisticated implementation, we'd track string offset per binding
        }
    }

    /// <summary>
    /// Number of bound variables.
    /// </summary>
    public readonly int Count => _count;

    /// <summary>
    /// Get the raw binding data for direct access.
    /// </summary>
    public readonly ReadOnlySpan<Binding> GetBindings() => _bindings.Slice(0, _count);

    /// <summary>
    /// Get the string buffer for direct access.
    /// </summary>
    public readonly ReadOnlySpan<char> GetStringBuffer() => _stringBuffer.Slice(0, _stringOffset);

    private static int ComputeHash(ReadOnlySpan<char> value)
    {
        // FNV-1a hash
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }
}

/// <summary>
/// Type of value in a binding.
/// </summary>
public enum BindingValueType : byte
{
    Unbound = 0,
    Uri = 1,
    String = 2,
    Integer = 3,
    Double = 4,
    Boolean = 5
}

/// <summary>
/// Single variable binding (unmanaged for stackalloc)
/// </summary>
public struct Binding
{
    public int VariableNameHash;
    public BindingValueType Type;
    public long IntegerValue;
    public double DoubleValue;
    public bool BooleanValue;
    public int StringOffset;
    public int StringLength;
}

/// <summary>
/// Represents a parsed SPARQL Update operation.
/// </summary>
public struct UpdateOperation
{
    public QueryType Type;
    public Prologue Prologue;

    // For INSERT DATA / DELETE DATA - inline triple data
    public QuadData[] InsertData;
    public QuadData[] DeleteData;

    // For DELETE/INSERT ... WHERE - template patterns
    public GraphPattern DeleteTemplate;
    public GraphPattern InsertTemplate;
    public WhereClause WhereClause;

    // For USING clause in DELETE/INSERT
    public DatasetClause[] UsingClauses;

    // For WITH clause in DELETE/INSERT WHERE
    public int WithGraphStart;
    public int WithGraphLength;

    // For graph management (LOAD, CLEAR, DROP, CREATE, COPY, MOVE, ADD)
    public GraphTarget SourceGraph;
    public GraphTarget DestinationGraph;
    public bool Silent;  // SILENT modifier

    // For LOAD
    public int SourceUriStart;
    public int SourceUriLength;
}

/// <summary>
/// Represents a quad (triple + optional graph) for INSERT DATA / DELETE DATA.
/// Uses offsets into source span for zero-allocation parsing.
/// </summary>
public struct QuadData
{
    public int SubjectStart;
    public int SubjectLength;
    public TermType SubjectType;

    public int PredicateStart;
    public int PredicateLength;
    public TermType PredicateType;

    public int ObjectStart;
    public int ObjectLength;
    public TermType ObjectType;

    // Optional graph (0 length = default graph)
    public int GraphStart;
    public int GraphLength;
}

/// <summary>
/// Specifies a target graph for graph management operations.
/// </summary>
public struct GraphTarget
{
    public GraphTargetType Type;
    public int IriStart;
    public int IriLength;
}

public enum GraphTargetType
{
    Default,      // DEFAULT
    Named,        // NAMED
    All,          // ALL
    Graph         // GRAPH <iri>
}

public class SparqlParseException : Exception
{
    public SparqlParseException(string message) : base(message) { }
}
