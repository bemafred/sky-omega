using System;
using System.Globalization;

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
    public ValuesClause Values;  // Trailing VALUES clause at end of query
}

public struct Prologue
{
    public const int MaxPrefixes = 32;
    private int _prefixCount;

    // Store prefixes as start/length pairs into source string
    // Each prefix entry: (prefixStart, prefixLength, iriStart, iriLength)
    private int _p0s, _p0l, _i0s, _i0l;
    private int _p1s, _p1l, _i1s, _i1l;
    private int _p2s, _p2l, _i2s, _i2l;
    private int _p3s, _p3l, _i3s, _i3l;
    private int _p4s, _p4l, _i4s, _i4l;
    private int _p5s, _p5l, _i5s, _i5l;
    private int _p6s, _p6l, _i6s, _i6l;
    private int _p7s, _p7l, _i7s, _i7l;
    private int _p8s, _p8l, _i8s, _i8l;
    private int _p9s, _p9l, _i9s, _i9l;
    private int _p10s, _p10l, _i10s, _i10l;
    private int _p11s, _p11l, _i11s, _i11l;
    private int _p12s, _p12l, _i12s, _i12l;
    private int _p13s, _p13l, _i13s, _i13l;
    private int _p14s, _p14l, _i14s, _i14l;
    private int _p15s, _p15l, _i15s, _i15l;

    public int BaseUriStart;   // Start offset in source span
    public int BaseUriLength;  // Length in source span

    public readonly int PrefixCount => _prefixCount;

    public void AddPrefix(ReadOnlySpan<char> prefix, ReadOnlySpan<char> iri)
    {
        // This overload doesn't work well - we need start/length pairs
        // For now, just count - this will be fixed by AddPrefixRange
        _prefixCount++;
    }

    public void AddPrefixRange(int prefixStart, int prefixLength, int iriStart, int iriLength)
    {
        if (_prefixCount >= MaxPrefixes)
            throw new SparqlParseException("Too many prefix declarations (max 32)");

        switch (_prefixCount)
        {
            case 0: _p0s = prefixStart; _p0l = prefixLength; _i0s = iriStart; _i0l = iriLength; break;
            case 1: _p1s = prefixStart; _p1l = prefixLength; _i1s = iriStart; _i1l = iriLength; break;
            case 2: _p2s = prefixStart; _p2l = prefixLength; _i2s = iriStart; _i2l = iriLength; break;
            case 3: _p3s = prefixStart; _p3l = prefixLength; _i3s = iriStart; _i3l = iriLength; break;
            case 4: _p4s = prefixStart; _p4l = prefixLength; _i4s = iriStart; _i4l = iriLength; break;
            case 5: _p5s = prefixStart; _p5l = prefixLength; _i5s = iriStart; _i5l = iriLength; break;
            case 6: _p6s = prefixStart; _p6l = prefixLength; _i6s = iriStart; _i6l = iriLength; break;
            case 7: _p7s = prefixStart; _p7l = prefixLength; _i7s = iriStart; _i7l = iriLength; break;
            case 8: _p8s = prefixStart; _p8l = prefixLength; _i8s = iriStart; _i8l = iriLength; break;
            case 9: _p9s = prefixStart; _p9l = prefixLength; _i9s = iriStart; _i9l = iriLength; break;
            case 10: _p10s = prefixStart; _p10l = prefixLength; _i10s = iriStart; _i10l = iriLength; break;
            case 11: _p11s = prefixStart; _p11l = prefixLength; _i11s = iriStart; _i11l = iriLength; break;
            case 12: _p12s = prefixStart; _p12l = prefixLength; _i12s = iriStart; _i12l = iriLength; break;
            case 13: _p13s = prefixStart; _p13l = prefixLength; _i13s = iriStart; _i13l = iriLength; break;
            case 14: _p14s = prefixStart; _p14l = prefixLength; _i14s = iriStart; _i14l = iriLength; break;
            case 15: _p15s = prefixStart; _p15l = prefixLength; _i15s = iriStart; _i15l = iriLength; break;
            default: throw new SparqlParseException("Too many prefix declarations (max 16)");
        }
        _prefixCount++;
    }

    public readonly (int PrefixStart, int PrefixLength, int IriStart, int IriLength) GetPrefix(int index)
    {
        return index switch
        {
            0 => (_p0s, _p0l, _i0s, _i0l),
            1 => (_p1s, _p1l, _i1s, _i1l),
            2 => (_p2s, _p2l, _i2s, _i2l),
            3 => (_p3s, _p3l, _i3s, _i3l),
            4 => (_p4s, _p4l, _i4s, _i4l),
            5 => (_p5s, _p5l, _i5s, _i5l),
            6 => (_p6s, _p6l, _i6s, _i6l),
            7 => (_p7s, _p7l, _i7s, _i7l),
            8 => (_p8s, _p8l, _i8s, _i8l),
            9 => (_p9s, _p9l, _i9s, _i9l),
            10 => (_p10s, _p10l, _i10s, _i10l),
            11 => (_p11s, _p11l, _i11s, _i11l),
            12 => (_p12s, _p12l, _i12s, _i12l),
            13 => (_p13s, _p13l, _i13s, _i13l),
            14 => (_p14s, _p14l, _i14s, _i14l),
            15 => (_p15s, _p15l, _i15s, _i15l),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public struct SelectClause
{
    public const int MaxAggregates = 8;
    public const int MaxProjectedVariables = 16;
    public bool Distinct;
    public bool Reduced;
    public bool SelectAll;

    private int _aggregateCount;
    // Inline storage for up to 8 aggregate expressions
    private AggregateExpression _a0, _a1, _a2, _a3, _a4, _a5, _a6, _a7;

    // Inline storage for up to 16 projected variables (start, length pairs)
    private int _projectedVariableCount;
    private int _pv0s, _pv0l, _pv1s, _pv1l, _pv2s, _pv2l, _pv3s, _pv3l;
    private int _pv4s, _pv4l, _pv5s, _pv5l, _pv6s, _pv6l, _pv7s, _pv7l;
    private int _pv8s, _pv8l, _pv9s, _pv9l, _pv10s, _pv10l, _pv11s, _pv11l;
    private int _pv12s, _pv12l, _pv13s, _pv13l, _pv14s, _pv14l, _pv15s, _pv15l;

    public readonly int AggregateCount => _aggregateCount;
    public readonly bool HasAggregates => _aggregateCount > 0;
    /// <summary>
    /// Returns true if there are any real aggregate functions (COUNT, SUM, AVG, etc.).
    /// Non-aggregate computed expressions (HOURS, STR, etc.) with AggregateFunction.None
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
    public readonly int ProjectedVariableCount => _projectedVariableCount;
    public readonly bool HasProjectedVariables => _projectedVariableCount > 0;

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

    public void AddProjectedVariable(int start, int length)
    {
        if (_projectedVariableCount >= MaxProjectedVariables)
            throw new SparqlParseException("Too many projected variables (max 16)");

        switch (_projectedVariableCount)
        {
            case 0: _pv0s = start; _pv0l = length; break;
            case 1: _pv1s = start; _pv1l = length; break;
            case 2: _pv2s = start; _pv2l = length; break;
            case 3: _pv3s = start; _pv3l = length; break;
            case 4: _pv4s = start; _pv4l = length; break;
            case 5: _pv5s = start; _pv5l = length; break;
            case 6: _pv6s = start; _pv6l = length; break;
            case 7: _pv7s = start; _pv7l = length; break;
            case 8: _pv8s = start; _pv8l = length; break;
            case 9: _pv9s = start; _pv9l = length; break;
            case 10: _pv10s = start; _pv10l = length; break;
            case 11: _pv11s = start; _pv11l = length; break;
            case 12: _pv12s = start; _pv12l = length; break;
            case 13: _pv13s = start; _pv13l = length; break;
            case 14: _pv14s = start; _pv14l = length; break;
            case 15: _pv15s = start; _pv15l = length; break;
        }
        _projectedVariableCount++;
    }

    public readonly (int Start, int Length) GetProjectedVariable(int index)
    {
        return index switch
        {
            0 => (_pv0s, _pv0l),
            1 => (_pv1s, _pv1l),
            2 => (_pv2s, _pv2l),
            3 => (_pv3s, _pv3l),
            4 => (_pv4s, _pv4l),
            5 => (_pv5s, _pv5l),
            6 => (_pv6s, _pv6l),
            7 => (_pv7s, _pv7l),
            8 => (_pv8s, _pv8l),
            9 => (_pv9s, _pv9l),
            10 => (_pv10s, _pv10l),
            11 => (_pv11s, _pv11l),
            12 => (_pv12s, _pv12l),
            13 => (_pv13s, _pv13l),
            14 => (_pv14s, _pv14l),
            15 => (_pv15s, _pv15l),
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
    private int _unionStartIndex; // Patterns from this index are the UNION branch
    private int _firstBranchBindCount; // BINDs before UNION - rest are in second branch
    private bool _hasUnion;       // True if UNION keyword was encountered
    private bool _inUnionBranch;  // True when parsing second UNION branch

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

    // MINUS filter expression (for FILTER inside MINUS)
    private FilterExpr _minusFilter;
    private byte _minusFilterBlock;  // Which MINUS block the filter belongs to

    // Bitmask for OPTIONAL patterns inside MINUS: bit N = 1 means MINUS pattern N is optional
    private byte _minusOptionalFlags;

    // MINUS block tracking: up to 4 MINUS blocks
    // _minusBlockBoundaries[i] = pattern index where block i ends (exclusive)
    private byte _minusBlockCount;
    private byte _minusBlockBoundary0, _minusBlockBoundary1, _minusBlockBoundary2, _minusBlockBoundary3;

    // EXISTS filters inside MINUS (for FILTER NOT EXISTS inside MINUS)
    // _minusExistsBlockIndex[i] = which MINUS block owns EXISTS filter i
    private byte _minusExistsCount;
    private byte _minusExistsBlock0, _minusExistsBlock1, _minusExistsBlock2, _minusExistsBlock3;
    private ExistsFilter _me0, _me1, _me2, _me3;

    // Compound EXISTS refs inside MINUS filters
    // These track the position of [NOT] EXISTS embedded in compound expressions like:
    // FILTER ( ?x = ?y || NOT EXISTS { ... } )
    private byte _compoundExistsRefCount;
    private CompoundExistsRef _cer0, _cer1;

    // Nested MINUS inside MINUS blocks (for MINUS { ... MINUS { ... } })
    // Patterns for nested MINUS are stored separately from outer MINUS patterns
    private byte _nestedMinusCount;
    private byte _nestedMinusPatternCount;
    // Nested MINUS block info: parent block index and pattern boundaries
    private byte _nestedMinusParent0, _nestedMinusParent1, _nestedMinusParent2, _nestedMinusParent3;
    private byte _nestedMinusBoundary0, _nestedMinusBoundary1, _nestedMinusBoundary2, _nestedMinusBoundary3;
    // Inline storage for nested MINUS patterns (8 patterns)
    private TriplePattern _nmp0, _nmp1, _nmp2, _nmp3, _nmp4, _nmp5, _nmp6, _nmp7;
    // EXISTS filters inside nested MINUS (1 per nested block, up to 4)
    private ExistsFilter _nme0, _nme1, _nme2, _nme3;
    private byte _nestedMinusExistsFlags; // bit N = 1 means nested MINUS N has an EXISTS filter

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
    public readonly int FirstBranchBindCount => HasUnion ? _firstBranchBindCount : _bindCount;
    public readonly int UnionBranchBindCount => HasUnion ? _bindCount - _firstBranchBindCount : 0;
    public readonly int MinusPatternCount => _minusPatternCount;
    public readonly int ExistsFilterCount => _existsFilterCount;
    public readonly int GraphClauseCount => _graphClauseCount;
    public readonly int SubQueryCount => _subQueryCount;
    public readonly int ServiceClauseCount => _serviceClauseCount;
    public readonly bool HasBinds => _bindCount > 0;
    public readonly bool HasMinus => _minusPatternCount > 0;
    public readonly bool HasMinusFilter => _minusFilter.Length > 0;
    public readonly FilterExpr MinusFilter => _minusFilter;
    public readonly bool HasMinusOptionalPatterns => _minusOptionalFlags != 0;
    public readonly bool HasMinusExists => _minusExistsCount > 0;
    public readonly int MinusExistsCount => _minusExistsCount;
    public readonly int MinusBlockCount => _minusBlockCount;
    public readonly bool HasCompoundExistsRefs => _compoundExistsRefCount > 0;
    public readonly int CompoundExistsRefCount => _compoundExistsRefCount;
    public readonly bool HasNestedMinus => _nestedMinusCount > 0;
    public readonly int NestedMinusCount => _nestedMinusCount;
    public readonly int NestedMinusPatternCount => _nestedMinusPatternCount;
    public readonly bool HasExists => _existsFilterCount > 0;
    public readonly bool HasGraph => _graphClauseCount > 0;
    public readonly bool HasSubQueries => _subQueryCount > 0;
    public readonly bool HasService => _serviceClauseCount > 0;
    public readonly bool HasValues => _values.HasValues;
    public readonly bool HasOptionalPatterns => _optionalFlags != 0;
    public readonly bool HasUnion => _hasUnion;

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
    /// Sets flag so SERVICE clauses added after this point are tagged with UnionBranch = 1.
    /// Also captures how many BINDs belong to the first branch.
    /// </summary>
    public void StartUnionBranch()
    {
        _hasUnion = true;
        _unionStartIndex = _patternCount;
        _firstBranchBindCount = _bindCount;
        _inUnionBranch = true;
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

    /// <summary>
    /// Add a pattern from an OPTIONAL clause inside MINUS.
    /// </summary>
    public void AddOptionalMinusPattern(TriplePattern pattern)
    {
        if (_minusPatternCount >= MaxMinusPatterns) return;
        _minusOptionalFlags |= (byte)(1 << _minusPatternCount);
        SetMinusPattern(_minusPatternCount++, pattern);
    }

    /// <summary>
    /// Check if a MINUS pattern at the given index is optional.
    /// </summary>
    public readonly bool IsMinusOptional(int index) => (_minusOptionalFlags & (1 << index)) != 0;

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

    /// <summary>
    /// Set the filter expression for MINUS clause.
    /// This filter is evaluated for each matching MINUS pattern.
    /// </summary>
    public void SetMinusFilter(FilterExpr filter)
    {
        _minusFilter = filter;
        // Track which block this filter belongs to (current block is _minusBlockCount - 1)
        _minusFilterBlock = (byte)(_minusBlockCount > 0 ? _minusBlockCount - 1 : 0);
    }

    /// <summary>
    /// Get which MINUS block the MINUS filter belongs to.
    /// </summary>
    public readonly int MinusFilterBlock => _minusFilterBlock;

    /// <summary>
    /// Start a new MINUS block. Call before adding patterns for a new MINUS clause.
    /// </summary>
    public void StartMinusBlock()
    {
        if (_minusBlockCount >= 4) return;  // Max 4 MINUS blocks
        _minusBlockCount++;
    }

    /// <summary>
    /// End the current MINUS block. Records the boundary.
    /// </summary>
    public void EndMinusBlock()
    {
        if (_minusBlockCount == 0) return;
        var blockIdx = _minusBlockCount - 1;
        var endIdx = (byte)_minusPatternCount;
        switch (blockIdx)
        {
            case 0: _minusBlockBoundary0 = endIdx; break;
            case 1: _minusBlockBoundary1 = endIdx; break;
            case 2: _minusBlockBoundary2 = endIdx; break;
            case 3: _minusBlockBoundary3 = endIdx; break;
        }
    }

    /// <summary>
    /// Get the start pattern index for a MINUS block.
    /// </summary>
    public readonly int GetMinusBlockStart(int blockIndex)
    {
        if (blockIndex == 0) return 0;
        return blockIndex switch
        {
            1 => _minusBlockBoundary0,
            2 => _minusBlockBoundary1,
            3 => _minusBlockBoundary2,
            _ => 0
        };
    }

    /// <summary>
    /// Get the end pattern index (exclusive) for a MINUS block.
    /// </summary>
    public readonly int GetMinusBlockEnd(int blockIndex)
    {
        return blockIndex switch
        {
            0 => _minusBlockBoundary0,
            1 => _minusBlockBoundary1,
            2 => _minusBlockBoundary2,
            3 => _minusBlockBoundary3,
            _ => _minusPatternCount
        };
    }

    public void AddMinusExistsFilter(ExistsFilter filter)
    {
        if (_minusExistsCount >= 4) return;  // Max 4 EXISTS filters inside MINUS
        var blockIdx = (byte)(_minusBlockCount > 0 ? _minusBlockCount - 1 : 0);
        switch (_minusExistsCount++)
        {
            case 0:
                _me0 = filter;
                _minusExistsBlock0 = blockIdx;
                break;
            case 1:
                _me1 = filter;
                _minusExistsBlock1 = blockIdx;
                break;
            case 2:
                _me2 = filter;
                _minusExistsBlock2 = blockIdx;
                break;
            case 3:
                _me3 = filter;
                _minusExistsBlock3 = blockIdx;
                break;
        }
    }

    public readonly ExistsFilter GetMinusExistsFilter(int index)
    {
        return index switch
        {
            0 => _me0,
            1 => _me1,
            2 => _me2,
            3 => _me3,
            _ => default
        };
    }

    /// <summary>
    /// Get which MINUS block owns a particular EXISTS filter.
    /// </summary>
    public readonly int GetMinusExistsBlock(int existsIndex)
    {
        return existsIndex switch
        {
            0 => _minusExistsBlock0,
            1 => _minusExistsBlock1,
            2 => _minusExistsBlock2,
            3 => _minusExistsBlock3,
            _ => 0
        };
    }

    /// <summary>
    /// Add a compound EXISTS reference for tracking EXISTS embedded in compound filter expressions.
    /// </summary>
    public void AddCompoundExistsRef(CompoundExistsRef existsRef)
    {
        if (_compoundExistsRefCount >= 2) return;  // Max 2 compound EXISTS refs
        switch (_compoundExistsRefCount++)
        {
            case 0: _cer0 = existsRef; break;
            case 1: _cer1 = existsRef; break;
        }
    }

    /// <summary>
    /// Get a compound EXISTS reference by index.
    /// </summary>
    public readonly CompoundExistsRef GetCompoundExistsRef(int index)
    {
        return index switch
        {
            0 => _cer0,
            1 => _cer1,
            _ => default
        };
    }

    /// <summary>
    /// Start a new nested MINUS block inside the current outer MINUS block.
    /// </summary>
    public void StartNestedMinusBlock()
    {
        if (_nestedMinusCount >= 4) return;  // Max 4 nested MINUS blocks
        // Record which outer MINUS block this nested block belongs to
        var parentBlock = (byte)(_minusBlockCount > 0 ? _minusBlockCount - 1 : 0);
        switch (_nestedMinusCount)
        {
            case 0: _nestedMinusParent0 = parentBlock; break;
            case 1: _nestedMinusParent1 = parentBlock; break;
            case 2: _nestedMinusParent2 = parentBlock; break;
            case 3: _nestedMinusParent3 = parentBlock; break;
        }
        _nestedMinusCount++;
    }

    /// <summary>
    /// End the current nested MINUS block. Records the boundary.
    /// </summary>
    public void EndNestedMinusBlock()
    {
        if (_nestedMinusCount == 0) return;
        var blockIdx = _nestedMinusCount - 1;
        var endIdx = (byte)_nestedMinusPatternCount;
        switch (blockIdx)
        {
            case 0: _nestedMinusBoundary0 = endIdx; break;
            case 1: _nestedMinusBoundary1 = endIdx; break;
            case 2: _nestedMinusBoundary2 = endIdx; break;
            case 3: _nestedMinusBoundary3 = endIdx; break;
        }
    }

    /// <summary>
    /// Add a pattern to the current nested MINUS block.
    /// </summary>
    public void AddNestedMinusPattern(TriplePattern pattern)
    {
        if (_nestedMinusPatternCount >= 8) return;
        SetNestedMinusPattern(_nestedMinusPatternCount++, pattern);
    }

    private void SetNestedMinusPattern(int index, TriplePattern pattern)
    {
        switch (index)
        {
            case 0: _nmp0 = pattern; break;
            case 1: _nmp1 = pattern; break;
            case 2: _nmp2 = pattern; break;
            case 3: _nmp3 = pattern; break;
            case 4: _nmp4 = pattern; break;
            case 5: _nmp5 = pattern; break;
            case 6: _nmp6 = pattern; break;
            case 7: _nmp7 = pattern; break;
        }
    }

    /// <summary>
    /// Get a nested MINUS pattern by index.
    /// </summary>
    public readonly TriplePattern GetNestedMinusPattern(int index)
    {
        return index switch
        {
            0 => _nmp0, 1 => _nmp1, 2 => _nmp2, 3 => _nmp3,
            4 => _nmp4, 5 => _nmp5, 6 => _nmp6, 7 => _nmp7,
            _ => default
        };
    }

    /// <summary>
    /// Get which outer MINUS block a nested MINUS block belongs to.
    /// </summary>
    public readonly int GetNestedMinusParentBlock(int nestedBlockIndex)
    {
        return nestedBlockIndex switch
        {
            0 => _nestedMinusParent0,
            1 => _nestedMinusParent1,
            2 => _nestedMinusParent2,
            3 => _nestedMinusParent3,
            _ => 0
        };
    }

    /// <summary>
    /// Get the start pattern index for a nested MINUS block.
    /// </summary>
    public readonly int GetNestedMinusBlockStart(int nestedBlockIndex)
    {
        if (nestedBlockIndex == 0) return 0;
        return nestedBlockIndex switch
        {
            1 => _nestedMinusBoundary0,
            2 => _nestedMinusBoundary1,
            3 => _nestedMinusBoundary2,
            _ => 0
        };
    }

    /// <summary>
    /// Get the end pattern index (exclusive) for a nested MINUS block.
    /// </summary>
    public readonly int GetNestedMinusBlockEnd(int nestedBlockIndex)
    {
        return nestedBlockIndex switch
        {
            0 => _nestedMinusBoundary0,
            1 => _nestedMinusBoundary1,
            2 => _nestedMinusBoundary2,
            3 => _nestedMinusBoundary3,
            _ => _nestedMinusPatternCount
        };
    }

    /// <summary>
    /// Add an EXISTS filter to the current nested MINUS block.
    /// </summary>
    public void AddNestedMinusExistsFilter(ExistsFilter filter)
    {
        if (_nestedMinusCount == 0 || _nestedMinusCount > 4) return;
        var blockIdx = _nestedMinusCount - 1;
        _nestedMinusExistsFlags |= (byte)(1 << blockIdx);
        switch (blockIdx)
        {
            case 0: _nme0 = filter; break;
            case 1: _nme1 = filter; break;
            case 2: _nme2 = filter; break;
            case 3: _nme3 = filter; break;
        }
    }

    /// <summary>
    /// Check if a nested MINUS block has an EXISTS filter.
    /// </summary>
    public readonly bool HasNestedMinusExistsFilter(int nestedBlockIndex)
    {
        return (_nestedMinusExistsFlags & (1 << nestedBlockIndex)) != 0;
    }

    /// <summary>
    /// Get the EXISTS filter for a nested MINUS block.
    /// </summary>
    public readonly ExistsFilter GetNestedMinusExistsFilter(int nestedBlockIndex)
    {
        return nestedBlockIndex switch
        {
            0 => _nme0,
            1 => _nme1,
            2 => _nme2,
            3 => _nme3,
            _ => default
        };
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

    /// <summary>
    /// Check if a graph clause uses a variable (without copying the full GraphClause struct).
    /// This avoids ~600 byte struct copy on each call.
    /// </summary>
    public readonly bool IsGraphClauseVariable(int index)
    {
        return index switch
        {
            0 => _g0.IsVariable,
            1 => _g1.IsVariable,
            2 => _g2.IsVariable,
            3 => _g3.IsVariable,
            _ => false
        };
    }

    /// <summary>
    /// Get the pattern count of a graph clause (without copying the full GraphClause struct).
    /// This avoids ~600 byte struct copy on each call.
    /// </summary>
    public readonly int GetGraphClausePatternCount(int index)
    {
        return index switch
        {
            0 => _g0.PatternCount,
            1 => _g1.PatternCount,
            2 => _g2.PatternCount,
            3 => _g3.PatternCount,
            _ => 0
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
        clause.UnionBranch = _inUnionBranch ? 1 : 0;
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
/// iri? (zero or one), path1/path2 (sequence), path1|path2 (alternative),
/// !(iri1|iri2|...) (negated property set)
/// </summary>
public struct PropertyPath
{
    public PathType Type;
    public Term Iri;           // The IRI for simple paths
    public int LeftStart;      // For sequence/alternative: offset of left operand; for NegatedSet: offset of content
    public int LeftLength;     // For sequence/alternative: length of left operand; for NegatedSet: length of content
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

    /// <summary>
    /// Creates a negated property set path that matches any predicate EXCEPT those listed.
    /// The content span contains the IRIs separated by | (e.g., "rdf:type|rdfs:label").
    /// </summary>
    public static PropertyPath NegatedSet(int contentStart, int contentLength) =>
        new() { Type = PathType.NegatedSet, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates a grouped zero-or-more path: (path)* - e.g., (p1/p2/p3)*
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath GroupedZeroOrMore(int contentStart, int contentLength) =>
        new() { Type = PathType.GroupedZeroOrMore, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates a grouped one-or-more path: (path)+ - e.g., (p1/p2/p3)+
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath GroupedOneOrMore(int contentStart, int contentLength) =>
        new() { Type = PathType.GroupedOneOrMore, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates a grouped zero-or-one path: (path)? - e.g., (p1/p2)?
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath GroupedZeroOrOne(int contentStart, int contentLength) =>
        new() { Type = PathType.GroupedZeroOrOne, LeftStart = contentStart, LeftLength = contentLength };

    /// <summary>
    /// Creates an inverse grouped path: ^(path) - e.g., ^(p1/p2)
    /// The content span contains the inner path expression.
    /// </summary>
    public static PropertyPath InverseGroup(int contentStart, int contentLength) =>
        new() { Type = PathType.InverseGroup, LeftStart = contentStart, LeftLength = contentLength };
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
    Alternative,     // path1|path2 - alternative paths
    NegatedSet,      // !(iri1|iri2|...) - matches any predicate except those listed
    GroupedZeroOrMore, // (path)* - zero or more repetitions of grouped path
    GroupedOneOrMore,  // (path)+ - one or more repetitions of grouped path
    GroupedZeroOrOne,  // (path)? - zero or one occurrence of grouped path
    InverseGroup       // ^(path) - inverse of grouped path
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
    public int Start;      // Offset into source (after "FILTER")
    public int Length;     // Length of expression
    public int ScopeDepth; // Scope depth (0 = top level, 1 = first nested group, etc.)
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

    // Graph context for patterns inside GRAPH clause
    // If HasGraph is true, all patterns should be evaluated against this graph
    public bool HasGraph;
    public Term GraphTerm;  // The graph IRI or variable

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

    public void SetGraphContext(Term graphTerm)
    {
        HasGraph = true;
        GraphTerm = graphTerm;
    }
}

/// <summary>
/// Reference to an EXISTS pattern embedded within a compound FILTER expression.
/// Used to track positions of [NOT] EXISTS in expressions like: FILTER ( ?x = ?y || NOT EXISTS { ... } )
/// </summary>
public struct CompoundExistsRef
{
    /// <summary>
    /// Start position of the [NOT] EXISTS portion in the filter expression (relative to filter start).
    /// </summary>
    public int StartInFilter;

    /// <summary>
    /// Length of the [NOT] EXISTS portion including the braces.
    /// </summary>
    public int Length;

    /// <summary>
    /// Index of the corresponding ExistsFilter (MinusExistsFilter) that contains the patterns.
    /// </summary>
    public int ExistsFilterIndex;

    /// <summary>
    /// True if this is NOT EXISTS (negate the result).
    /// </summary>
    public bool Negated;

    /// <summary>
    /// Which MINUS block this compound EXISTS ref belongs to.
    /// </summary>
    public int BlockIndex;
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
    public bool IsOptional;       // Inside OPTIONAL block - preserve outer bindings on no match
    public int UnionBranch;       // 0 = not in UNION or first branch, 1 = second branch
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
    /// <summary>
    /// Index of the triple pattern after which this BIND should be evaluated.
    /// -1 means evaluate before any patterns (rare), 0 means after pattern 0, etc.
    /// This enables proper BIND semantics where the computed variable can be used
    /// as a constraint in subsequent patterns.
    /// </summary>
    public int AfterPatternIndex;
    /// <summary>
    /// Scope depth (0 = top level, 1 = first nested group, etc.)
    /// Used to exclude this binding from filters in deeper scopes per SPARQL scoping rules.
    /// </summary>
    public int ScopeDepth;
}

/// <summary>
/// A VALUES clause: VALUES ?var { value1 ... } or VALUES (?var1 ?var2) { (val1 val2) ... }
/// Supports up to 4 variables and up to 16 total values (stored in row-major order).
/// UNDEF values are marked with length = -1.
/// </summary>
public struct ValuesClause
{
    public const int MaxVariables = 4;
    public const int MaxValues = 16;

    // Variable storage (up to 4 variables)
    public int VarStart;     // Start of first variable name (including ?) - for backwards compatibility
    public int VarLength;    // Length of first variable name - for backwards compatibility
    private int _var1Start, _var1Len, _var2Start, _var2Len, _var3Start, _var3Len;
    private int _varCount;

    private int _valueCount;

    // Inline storage for value offsets (16 values * 2 ints = 128 bytes)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;
    private int _v8Start, _v8Len, _v9Start, _v9Len, _v10Start, _v10Len, _v11Start, _v11Len;
    private int _v12Start, _v12Len, _v13Start, _v13Len, _v14Start, _v14Len, _v15Start, _v15Len;

    public readonly int ValueCount => _valueCount;
    public readonly int VariableCount => _varCount;
    public readonly bool HasValues => _valueCount > 0;
    public readonly int RowCount => _varCount > 0 ? _valueCount / _varCount : 0;

    /// <summary>
    /// Add a variable to the VALUES clause.
    /// </summary>
    public void AddVariable(int start, int length)
    {
        if (_varCount >= MaxVariables) return;
        SetVariable(_varCount++, start, length);
    }

    /// <summary>
    /// Get variable by index.
    /// </summary>
    public readonly (int Start, int Length) GetVariable(int index)
    {
        return index switch
        {
            0 => (VarStart, VarLength),
            1 => (_var1Start, _var1Len),
            2 => (_var2Start, _var2Len),
            3 => (_var3Start, _var3Len),
            _ => (0, 0)
        };
    }

    private void SetVariable(int index, int start, int length)
    {
        switch (index)
        {
            case 0: VarStart = start; VarLength = length; break;
            case 1: _var1Start = start; _var1Len = length; break;
            case 2: _var2Start = start; _var2Len = length; break;
            case 3: _var3Start = start; _var3Len = length; break;
        }
    }

    /// <summary>
    /// Add a value to the VALUES clause. Use length = -1 for UNDEF.
    /// Values are stored in row-major order.
    /// </summary>
    public void AddValue(int start, int length)
    {
        if (_valueCount >= MaxValues) return;
        SetValue(_valueCount++, start, length);
    }

    /// <summary>
    /// Check if a value is UNDEF (length = -1).
    /// </summary>
    public readonly bool IsUndef(int index)
    {
        var (_, length) = GetValue(index);
        return length == -1;
    }

    /// <summary>
    /// Get value at a specific row and column (variable index).
    /// </summary>
    public readonly (int Start, int Length) GetValueAt(int row, int varIndex)
    {
        if (_varCount == 0) return (0, 0);
        var index = row * _varCount + varIndex;
        return GetValue(index);
    }

    public readonly (int Start, int Length) GetValue(int index)
    {
        return index switch
        {
            0 => (_v0Start, _v0Len), 1 => (_v1Start, _v1Len),
            2 => (_v2Start, _v2Len), 3 => (_v3Start, _v3Len),
            4 => (_v4Start, _v4Len), 5 => (_v5Start, _v5Len),
            6 => (_v6Start, _v6Len), 7 => (_v7Start, _v7Len),
            8 => (_v8Start, _v8Len), 9 => (_v9Start, _v9Len),
            10 => (_v10Start, _v10Len), 11 => (_v11Start, _v11Len),
            12 => (_v12Start, _v12Len), 13 => (_v13Start, _v13Len),
            14 => (_v14Start, _v14Len), 15 => (_v15Start, _v15Len),
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
            case 8: _v8Start = start; _v8Len = length; break;
            case 9: _v9Start = start; _v9Len = length; break;
            case 10: _v10Start = start; _v10Len = length; break;
            case 11: _v11Start = start; _v11Len = length; break;
            case 12: _v12Start = start; _v12Len = length; break;
            case 13: _v13Start = start; _v13Len = length; break;
            case 14: _v14Start = start; _v14Len = length; break;
            case 15: _v15Start = start; _v15Len = length; break;
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

    // Inline storage for up to 8 grouping variables/aliases (start, length pairs)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;

    // Inline storage for up to 8 grouping expressions (start, length pairs)
    // When expression length is 0, the entry is a simple variable; otherwise it's an expression with alias
    private int _e0Start, _e0Len, _e1Start, _e1Len, _e2Start, _e2Len, _e3Start, _e3Len;
    private int _e4Start, _e4Len, _e5Start, _e5Len, _e6Start, _e6Len, _e7Start, _e7Len;

    public readonly int Count => _count;
    public readonly bool HasGroupBy => _count > 0;

    /// <summary>
    /// Add a simple variable to GROUP BY (e.g., GROUP BY ?x).
    /// </summary>
    public void AddVariable(int start, int length)
    {
        AddExpression(start, length, 0, 0);
    }

    /// <summary>
    /// Add an expression with alias to GROUP BY (e.g., GROUP BY ((?O1 + ?O2) AS ?O12)).
    /// </summary>
    /// <param name="aliasStart">Start of alias variable (e.g., ?O12)</param>
    /// <param name="aliasLength">Length of alias variable</param>
    /// <param name="exprStart">Start of expression (e.g., (?O1 + ?O2))</param>
    /// <param name="exprLength">Length of expression</param>
    public void AddExpression(int aliasStart, int aliasLength, int exprStart, int exprLength)
    {
        if (_count >= MaxVariables)
            throw new SparqlParseException("Too many GROUP BY variables (max 8)");

        switch (_count)
        {
            case 0: _v0Start = aliasStart; _v0Len = aliasLength; _e0Start = exprStart; _e0Len = exprLength; break;
            case 1: _v1Start = aliasStart; _v1Len = aliasLength; _e1Start = exprStart; _e1Len = exprLength; break;
            case 2: _v2Start = aliasStart; _v2Len = aliasLength; _e2Start = exprStart; _e2Len = exprLength; break;
            case 3: _v3Start = aliasStart; _v3Len = aliasLength; _e3Start = exprStart; _e3Len = exprLength; break;
            case 4: _v4Start = aliasStart; _v4Len = aliasLength; _e4Start = exprStart; _e4Len = exprLength; break;
            case 5: _v5Start = aliasStart; _v5Len = aliasLength; _e5Start = exprStart; _e5Len = exprLength; break;
            case 6: _v6Start = aliasStart; _v6Len = aliasLength; _e6Start = exprStart; _e6Len = exprLength; break;
            case 7: _v7Start = aliasStart; _v7Len = aliasLength; _e7Start = exprStart; _e7Len = exprLength; break;
        }
        _count++;
    }

    /// <summary>
    /// Get the variable/alias at the given index.
    /// </summary>
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

    /// <summary>
    /// Get the expression at the given index. Returns (0, 0) for simple variables.
    /// </summary>
    public readonly (int Start, int Length) GetExpression(int index)
    {
        return index switch
        {
            0 => (_e0Start, _e0Len),
            1 => (_e1Start, _e1Len),
            2 => (_e2Start, _e2Len),
            3 => (_e3Start, _e3Len),
            4 => (_e4Start, _e4Len),
            5 => (_e5Start, _e5Len),
            6 => (_e6Start, _e6Len),
            7 => (_e7Start, _e7Len),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>
    /// Returns true if the entry at the given index is an expression with alias.
    /// </summary>
    public readonly bool IsExpression(int index)
    {
        var (_, len) = GetExpression(index);
        return len > 0;
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

        // Format the value as string and store in buffer (must use InvariantCulture for RDF compliance)
        Span<char> temp = stackalloc char[24];
        if (!value.TryFormat(temp, out int written, default, CultureInfo.InvariantCulture))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Integer;
        binding.IntegerValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND
        _stringOffset += written;
    }

    /// <summary>
    /// Bind a double value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, double value)
    {
        if (_count >= _bindings.Length) return;

        // Format the value as string and store in buffer (must use InvariantCulture for RDF compliance)
        Span<char> temp = stackalloc char[32];
        if (!value.TryFormat(temp, out int written, default, CultureInfo.InvariantCulture))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Double;
        binding.DoubleValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND
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
        binding.BindScopeDepth = -1; // From triple pattern, not BIND
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
        binding.BindScopeDepth = -1; // From triple pattern, not BIND

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
        binding.BindScopeDepth = -1; // From triple pattern, not BIND

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
        binding.BindScopeDepth = -1; // From triple pattern, not BIND

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Set the scope depth of the last added binding.
    /// Call this after Bind() when adding a binding from a BIND expression.
    /// </summary>
    /// <param name="scopeDepth">The scope depth of the BIND expression</param>
    public void SetLastBindScopeDepth(int scopeDepth)
    {
        if (_count > 0)
        {
            _bindings[_count - 1].BindScopeDepth = scopeDepth;
        }
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
    /// Reclaims string buffer space to prevent overflow during heavy backtracking.
    /// </summary>
    public void TruncateTo(int count)
    {
        if (count < _count)
        {
            // Reclaim string buffer space by resetting to right after the last retained binding
            // Each binding stores its StringOffset and StringLength, and bindings are added
            // in order, so string space is allocated sequentially.
            if (count > 0)
            {
                ref var lastBinding = ref _bindings[count - 1];
                _stringOffset = lastBinding.StringOffset + lastBinding.StringLength;
            }
            else
            {
                _stringOffset = 0;
            }
            _count = count;
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

    /// <summary>
    /// Get a binding at the specified index.
    /// </summary>
    public readonly Binding Get(int index) => _bindings[index];

    /// <summary>
    /// Get the current length of the string buffer (bytes used).
    /// </summary>
    public readonly int StringBufferLength => _stringOffset;

    /// <summary>
    /// Copy the string buffer contents to a destination span.
    /// </summary>
    public readonly void CopyStringsTo(Span<char> destination)
    {
        _stringBuffer.Slice(0, _stringOffset).CopyTo(destination);
    }

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
    /// <summary>
    /// Scope depth where this binding was created.
    /// -1 = from triple pattern match, >= 0 = from BIND at that scope depth.
    /// Used for BIND scoping rules where filters in nested groups should not
    /// see BIND variables from outer scopes.
    /// </summary>
    public int BindScopeDepth;
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
