namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A graph pattern containing triple patterns and filters.
/// Uses inline storage for zero-allocation parsing.
/// </summary>
internal struct GraphPattern
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
