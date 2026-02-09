using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// Executes multiple triple patterns using nested loop join.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// Due to ref struct limitations, we store resolved term strings in fixed buffers
/// to avoid span scoping issues with enumerator initialization.
/// </summary>
internal ref struct MultiPatternScan
{
    /// <summary>
    /// Wrapper class to box GraphPattern on the heap for subquery execution.
    /// This prevents stack overflow from nested subqueries since each MultiPatternScan
    /// would otherwise embed a ~4KB GraphPattern on the stack.
    /// </summary>
    internal sealed class BoxedPattern
    {
        public GraphPattern Pattern;
    }

    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    // ADR-011: Pattern always stored via reference to reduce stack size by ~4KB
    // Boxing the pattern allocates ~4KB on heap but saves ~4KB on stack per scan
    private readonly BoxedPattern _boxedPattern;
    private readonly bool _unionMode;
    private readonly ReadOnlySpan<char> _graph;
    private readonly int[]? _patternOrder;  // Optimized pattern execution order

    // Temporal query parameters
    private readonly TemporalQueryMode _temporalMode;
    private readonly DateTimeOffset _asOfTime;
    private readonly DateTimeOffset _rangeStart;
    private readonly DateTimeOffset _rangeEnd;

    // Prefix expansion support
    private readonly PrefixMapping[]? _prefixes;
    // Expanded IRIs stored as strings to ensure span lifetime safety
    // Each position needs its own storage since ResolveAndQuery() resolves all three
    // before calling ExecuteTemporalQuery
    private string? _expandedSubject;
    private string? _expandedPredicate;
    private string? _expandedObject;

    // Current state for each pattern level (support up to 12 patterns for SPARQL-star with multiple annotations)
    // ADR-011: Changed from 12 inline fields to pooled array to reduce stack size from ~18KB to ~15KB
    // Note: Nullable because default-constructed struct (as field in another struct) will have null
    private TemporalResultEnumerator[]? _enumerators;
    private const int MaxPatternLevels = 12;

    private int _currentLevel;
    private bool _init0, _init1, _init2, _init3;
    private bool _init4, _init5, _init6, _init7;
    private bool _init8, _init9, _init10, _init11;
    private bool _exhausted;

    // Track binding count at entry to each level for rollback
    private int _bindingCount0, _bindingCount1, _bindingCount2, _bindingCount3;
    private int _bindingCount4, _bindingCount5, _bindingCount6, _bindingCount7;
    private int _bindingCount8, _bindingCount9, _bindingCount10, _bindingCount11;

    // Pushed filter assignments: for each level (0-11), which filter indices to evaluate
    // Using fixed arrays to avoid heap allocations in ref struct
    // Supports up to 8 levels of filter pushing (levels 0-7)
    private int _levelFilterCount0, _levelFilterCount1, _levelFilterCount2, _levelFilterCount3;
    private int _levelFilterCount4, _levelFilterCount5, _levelFilterCount6, _levelFilterCount7;

    // Filter indices per level (max 4 filters per level)
    private int _f0_0, _f0_1, _f0_2, _f0_3;  // Level 0 filters
    private int _f1_0, _f1_1, _f1_2, _f1_3;  // Level 1 filters
    private int _f2_0, _f2_1, _f2_2, _f2_3;  // Level 2 filters
    private int _f3_0, _f3_1, _f3_2, _f3_3;  // Level 3 filters
    private int _f4_0, _f4_1, _f4_2, _f4_3;  // Level 4 filters
    private int _f5_0, _f5_1, _f5_2, _f5_3;  // Level 5 filters
    private int _f6_0, _f6_1, _f6_2, _f6_3;  // Level 6 filters
    private int _f7_0, _f7_1, _f7_2, _f7_3;  // Level 7 filters

    private bool _hasPushedFilters;

    // Term position enum for prefix expansion storage
    private enum TermPosition { Subject, Predicate, Object }

    /// <summary>
    /// Get the current pattern from boxed storage.
    /// Note: This copies the ~4KB struct - call sparingly and cache locally.
    /// </summary>
    private readonly GraphPattern CurrentPattern => _boxedPattern.Pattern;

    // Cached pattern count to avoid repeated CurrentPattern access
    private int _cachedPatternCount;

    public MultiPatternScan(QuadStore store, ReadOnlySpan<char> source, GraphPattern pattern,
        bool unionMode = false, ReadOnlySpan<char> graph = default, PrefixMapping[]? prefixes = null)
        : this(store, source, pattern, unionMode, graph,
               TemporalQueryMode.Current, default, default, default, null, null, prefixes)
    {
    }

    /// <summary>
    /// Constructor for subqueries that takes a boxed pattern to avoid stack overflow.
    /// The pattern is stored by reference on the heap instead of being copied inline.
    /// </summary>
    public MultiPatternScan(QuadStore store, ReadOnlySpan<char> source, BoxedPattern boxedPattern,
        PrefixMapping[]? prefixes = null)
    {
        _store = store;
        _source = source;
        _boxedPattern = boxedPattern;
        _unionMode = false;
        _graph = default;
        _patternOrder = null;
        _temporalMode = TemporalQueryMode.Current;
        _asOfTime = default;
        _rangeStart = default;
        _rangeEnd = default;
        _prefixes = prefixes;
        _expandedSubject = null;
        _expandedPredicate = null;
        _expandedObject = null;
        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _init4 = _init5 = _init6 = _init7 = false;
        _init8 = _init9 = _init10 = _init11 = false;
        _exhausted = false;
        // ADR-011: Rent enumerator array from pool instead of inline storage
        _enumerators = System.Buffers.ArrayPool<TemporalResultEnumerator>.Shared.Rent(MaxPatternLevels);
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;
        _bindingCount4 = _bindingCount5 = _bindingCount6 = _bindingCount7 = 0;
        _bindingCount8 = _bindingCount9 = _bindingCount10 = _bindingCount11 = 0;
        _levelFilterCount0 = _levelFilterCount1 = _levelFilterCount2 = _levelFilterCount3 = 0;
        _levelFilterCount4 = _levelFilterCount5 = _levelFilterCount6 = _levelFilterCount7 = 0;
        _f0_0 = _f0_1 = _f0_2 = _f0_3 = 0;
        _f1_0 = _f1_1 = _f1_2 = _f1_3 = 0;
        _f2_0 = _f2_1 = _f2_2 = _f2_3 = 0;
        _f3_0 = _f3_1 = _f3_2 = _f3_3 = 0;
        _f4_0 = _f4_1 = _f4_2 = _f4_3 = 0;
        _f5_0 = _f5_1 = _f5_2 = _f5_3 = 0;
        _f6_0 = _f6_1 = _f6_2 = _f6_3 = 0;
        _f7_0 = _f7_1 = _f7_2 = _f7_3 = 0;
        _hasPushedFilters = false;

        // Cache pattern count for boxed pattern
        _cachedPatternCount = boxedPattern.Pattern.RequiredPatternCount;
    }

    public MultiPatternScan(QuadStore store, ReadOnlySpan<char> source, BoxedPattern boxedPattern,
        ReadOnlySpan<char> graph, PrefixMapping[]? prefixes = null)
    {
        _store = store;
        _source = source;
        _boxedPattern = boxedPattern;
        _unionMode = false;
        _graph = graph;
        _patternOrder = null;
        _temporalMode = TemporalQueryMode.Current;
        _asOfTime = default;
        _rangeStart = default;
        _rangeEnd = default;
        _prefixes = prefixes;
        _expandedSubject = null;
        _expandedPredicate = null;
        _expandedObject = null;
        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _init4 = _init5 = _init6 = _init7 = false;
        _init8 = _init9 = _init10 = _init11 = false;
        _exhausted = false;
        _enumerators = System.Buffers.ArrayPool<TemporalResultEnumerator>.Shared.Rent(MaxPatternLevels);
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;
        _bindingCount4 = _bindingCount5 = _bindingCount6 = _bindingCount7 = 0;
        _bindingCount8 = _bindingCount9 = _bindingCount10 = _bindingCount11 = 0;
        _levelFilterCount0 = _levelFilterCount1 = _levelFilterCount2 = _levelFilterCount3 = 0;
        _levelFilterCount4 = _levelFilterCount5 = _levelFilterCount6 = _levelFilterCount7 = 0;
        _f0_0 = _f0_1 = _f0_2 = _f0_3 = 0;
        _f1_0 = _f1_1 = _f1_2 = _f1_3 = 0;
        _f2_0 = _f2_1 = _f2_2 = _f2_3 = 0;
        _f3_0 = _f3_1 = _f3_2 = _f3_3 = 0;
        _f4_0 = _f4_1 = _f4_2 = _f4_3 = 0;
        _f5_0 = _f5_1 = _f5_2 = _f5_3 = 0;
        _f6_0 = _f6_1 = _f6_2 = _f6_3 = 0;
        _f7_0 = _f7_1 = _f7_2 = _f7_3 = 0;
        _hasPushedFilters = false;
        _cachedPatternCount = boxedPattern.Pattern.RequiredPatternCount;
    }

    public MultiPatternScan(QuadStore store, ReadOnlySpan<char> source, GraphPattern pattern,
        bool unionMode, ReadOnlySpan<char> graph,
        TemporalQueryMode temporalMode, DateTimeOffset asOfTime,
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        int[]? patternOrder = null, List<int>[]? levelFilters = null,
        PrefixMapping[]? prefixes = null)
    {
        _store = store;
        _source = source;
        // ADR-011: Always box pattern to reduce stack size by ~4KB
        _boxedPattern = new BoxedPattern { Pattern = pattern };
        _unionMode = unionMode;
        _graph = graph;
        _patternOrder = patternOrder;
        _temporalMode = temporalMode;
        _asOfTime = asOfTime;
        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;
        _prefixes = prefixes;
        _expandedSubject = null;
        _expandedPredicate = null;
        _expandedObject = null;
        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _init4 = _init5 = _init6 = _init7 = false;
        _init8 = _init9 = _init10 = _init11 = false;
        _exhausted = false;
        // ADR-011: Rent enumerator array from pool instead of inline storage
        _enumerators = System.Buffers.ArrayPool<TemporalResultEnumerator>.Shared.Rent(MaxPatternLevels);
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;
        _bindingCount4 = _bindingCount5 = _bindingCount6 = _bindingCount7 = 0;
        _bindingCount8 = _bindingCount9 = _bindingCount10 = _bindingCount11 = 0;

        // Initialize pushed filter fields (levels 0-7)
        _levelFilterCount0 = _levelFilterCount1 = _levelFilterCount2 = _levelFilterCount3 = 0;
        _levelFilterCount4 = _levelFilterCount5 = _levelFilterCount6 = _levelFilterCount7 = 0;
        _f0_0 = _f0_1 = _f0_2 = _f0_3 = 0;
        _f1_0 = _f1_1 = _f1_2 = _f1_3 = 0;
        _f2_0 = _f2_1 = _f2_2 = _f2_3 = 0;
        _f3_0 = _f3_1 = _f3_2 = _f3_3 = 0;
        _f4_0 = _f4_1 = _f4_2 = _f4_3 = 0;
        _f5_0 = _f5_1 = _f5_2 = _f5_3 = 0;
        _f6_0 = _f6_1 = _f6_2 = _f6_3 = 0;
        _f7_0 = _f7_1 = _f7_2 = _f7_3 = 0;
        _hasPushedFilters = false;

        // Copy pushed filter assignments
        if (levelFilters != null)
        {
            _hasPushedFilters = true;
            for (int level = 0; level < Math.Min(levelFilters.Length, 8); level++)
            {
                var filters = levelFilters[level];
                if (filters == null || filters.Count == 0)
                    continue;

                var count = Math.Min(filters.Count, 4);
                SetLevelFilterCount(level, count);

                for (int i = 0; i < count; i++)
                    SetLevelFilter(level, i, filters[i]);
            }
        }

        // Cache pattern count to avoid repeated struct copying in hot path
        _cachedPatternCount = unionMode ? pattern.UnionBranchPatternCount : pattern.RequiredPatternCount;
    }

    /// <summary>
    /// Get pattern at level, respecting union mode and pattern ordering.
    /// When _patternOrder is set, maps level to pattern index for optimized execution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TriplePattern GetPatternAt(int level)
    {
        // Apply pattern order mapping if set
        var patternIndex = _patternOrder != null && level < _patternOrder.Length
            ? _patternOrder[level]
            : level;

        var pattern = CurrentPattern;
        return _unionMode ? pattern.GetUnionPattern(patternIndex) : pattern.GetPattern(patternIndex);
    }

    /// <summary>
    /// Get the number of patterns to process (uses cached value).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetPatternCount() => _cachedPatternCount;

    #region Pushed Filter Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetLevelFilterCount(int level, int count)
    {
        switch (level)
        {
            case 0: _levelFilterCount0 = count; break;
            case 1: _levelFilterCount1 = count; break;
            case 2: _levelFilterCount2 = count; break;
            case 3: _levelFilterCount3 = count; break;
            case 4: _levelFilterCount4 = count; break;
            case 5: _levelFilterCount5 = count; break;
            case 6: _levelFilterCount6 = count; break;
            case 7: _levelFilterCount7 = count; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int GetLevelFilterCount(int level)
    {
        return level switch
        {
            0 => _levelFilterCount0,
            1 => _levelFilterCount1,
            2 => _levelFilterCount2,
            3 => _levelFilterCount3,
            4 => _levelFilterCount4,
            5 => _levelFilterCount5,
            6 => _levelFilterCount6,
            7 => _levelFilterCount7,
            _ => 0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetLevelFilter(int level, int index, int filterIndex)
    {
        switch (level)
        {
            case 0:
                switch (index) { case 0: _f0_0 = filterIndex; break; case 1: _f0_1 = filterIndex; break; case 2: _f0_2 = filterIndex; break; case 3: _f0_3 = filterIndex; break; }
                break;
            case 1:
                switch (index) { case 0: _f1_0 = filterIndex; break; case 1: _f1_1 = filterIndex; break; case 2: _f1_2 = filterIndex; break; case 3: _f1_3 = filterIndex; break; }
                break;
            case 2:
                switch (index) { case 0: _f2_0 = filterIndex; break; case 1: _f2_1 = filterIndex; break; case 2: _f2_2 = filterIndex; break; case 3: _f2_3 = filterIndex; break; }
                break;
            case 3:
                switch (index) { case 0: _f3_0 = filterIndex; break; case 1: _f3_1 = filterIndex; break; case 2: _f3_2 = filterIndex; break; case 3: _f3_3 = filterIndex; break; }
                break;
            case 4:
                switch (index) { case 0: _f4_0 = filterIndex; break; case 1: _f4_1 = filterIndex; break; case 2: _f4_2 = filterIndex; break; case 3: _f4_3 = filterIndex; break; }
                break;
            case 5:
                switch (index) { case 0: _f5_0 = filterIndex; break; case 1: _f5_1 = filterIndex; break; case 2: _f5_2 = filterIndex; break; case 3: _f5_3 = filterIndex; break; }
                break;
            case 6:
                switch (index) { case 0: _f6_0 = filterIndex; break; case 1: _f6_1 = filterIndex; break; case 2: _f6_2 = filterIndex; break; case 3: _f6_3 = filterIndex; break; }
                break;
            case 7:
                switch (index) { case 0: _f7_0 = filterIndex; break; case 1: _f7_1 = filterIndex; break; case 2: _f7_2 = filterIndex; break; case 3: _f7_3 = filterIndex; break; }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int GetLevelFilter(int level, int index)
    {
        return level switch
        {
            0 => index switch { 0 => _f0_0, 1 => _f0_1, 2 => _f0_2, 3 => _f0_3, _ => 0 },
            1 => index switch { 0 => _f1_0, 1 => _f1_1, 2 => _f1_2, 3 => _f1_3, _ => 0 },
            2 => index switch { 0 => _f2_0, 1 => _f2_1, 2 => _f2_2, 3 => _f2_3, _ => 0 },
            3 => index switch { 0 => _f3_0, 1 => _f3_1, 2 => _f3_2, 3 => _f3_3, _ => 0 },
            4 => index switch { 0 => _f4_0, 1 => _f4_1, 2 => _f4_2, 3 => _f4_3, _ => 0 },
            5 => index switch { 0 => _f5_0, 1 => _f5_1, 2 => _f5_2, 3 => _f5_3, _ => 0 },
            6 => index switch { 0 => _f6_0, 1 => _f6_1, 2 => _f6_2, 3 => _f6_3, _ => 0 },
            7 => index switch { 0 => _f7_0, 1 => _f7_1, 2 => _f7_2, 3 => _f7_3, _ => 0 },
            _ => 0
        };
    }

    /// <summary>
    /// Evaluate pushed filters at the given level.
    /// Returns true if all filters pass, false if any filter fails.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool PassesLevelFilters(int level, ref BindingTable bindings)
    {
        if (!_hasPushedFilters)
            return true;

        var count = GetLevelFilterCount(level);
        if (count == 0)
            return true;

        var pattern = CurrentPattern;
        for (int i = 0; i < count; i++)
        {
            var filterIndex = GetLevelFilter(level, i);
            var filter = pattern.GetFilter(filterIndex);
            var filterExpr = _source.Slice(filter.Start, filter.Length);
            var evaluator = new FilterEvaluator(filterExpr);
            // Pass prefixes for prefix expansion in filter expressions (e.g., ?a = :s1)
            if (!evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer(),
                _prefixes, _source))
            {
                return false;
            }
        }
        return true;
    }

    #endregion

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        // Get pattern count based on mode (support up to 12 patterns for SPARQL-star with multiple annotations)
        var patternCount = Math.Min(GetPatternCount(), 12);
        if (patternCount == 0)
            return false;

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            bool advanced;
            switch (_currentLevel)
            {
                case 0: advanced = TryAdvanceLevel0(ref bindings); break;
                case 1: advanced = TryAdvanceLevel1(ref bindings); break;
                case 2: advanced = TryAdvanceLevel2(ref bindings); break;
                case 3: advanced = TryAdvanceLevel3(ref bindings); break;
                case 4: advanced = TryAdvanceLevel4(ref bindings); break;
                case 5: advanced = TryAdvanceLevel5(ref bindings); break;
                case 6: advanced = TryAdvanceLevel6(ref bindings); break;
                case 7: advanced = TryAdvanceLevel7(ref bindings); break;
                case 8: advanced = TryAdvanceLevel8(ref bindings); break;
                case 9: advanced = TryAdvanceLevel9(ref bindings); break;
                case 10: advanced = TryAdvanceLevel10(ref bindings); break;
                case 11: advanced = TryAdvanceLevel11(ref bindings); break;
                default: advanced = false; break;
            }

            if (advanced)
            {
                // Check pushed filters at this level before proceeding
                if (!PassesLevelFilters(_currentLevel, ref bindings))
                {
                    // Filters failed - continue trying next match at this level
                    continue;
                }

                // Evaluate any BIND expressions that should run after this pattern level.
                // This enables proper BIND semantics where:
                //   ?s ?p ?o .           # pattern 0
                //   BIND(?o+1 AS ?z)     # evaluated here, after pattern 0
                //   ?s1 ?p1 ?z           # pattern 1 - now ?z is bound from BIND
                EvaluateBindsForLevel(_currentLevel, ref bindings);

                if (_currentLevel == patternCount - 1)
                {
                    // At deepest level - we have a complete result
                    return true;
                }
                else
                {
                    // Go deeper
                    _currentLevel++;
                    SetInitialized(_currentLevel, false);
                }
            }
            else
            {
                // Current level exhausted, backtrack
                if (_currentLevel == 0)
                {
                    _exhausted = true;
                    return false;
                }

                // Go up
                _currentLevel--;
            }
        }
    }

    private void SetInitialized(int level, bool value)
    {
        switch (level)
        {
            case 0: _init0 = value; break;
            case 1: _init1 = value; break;
            case 2: _init2 = value; break;
            case 3: _init3 = value; break;
            case 4: _init4 = value; break;
            case 5: _init5 = value; break;
            case 6: _init6 = value; break;
            case 7: _init7 = value; break;
            case 8: _init8 = value; break;
            case 9: _init9 = value; break;
            case 10: _init10 = value; break;
            case 11: _init11 = value; break;
        }
    }

    /// <summary>
    /// Evaluate BIND expressions that should run after the pattern at the given level.
    /// This enables proper BIND semantics where computed variables can be used as constraints
    /// in subsequent patterns.
    /// </summary>
    private void EvaluateBindsForLevel(int level, scoped ref BindingTable bindings)
    {
        var pattern = CurrentPattern;
        if (pattern.BindCount == 0) return;

        // Determine the pattern index at this level (accounting for pattern reordering)
        var patternIndex = _patternOrder != null && level < _patternOrder.Length
            ? _patternOrder[level]
            : level;

        // Evaluate all BINDs whose AfterPatternIndex matches this pattern
        for (int i = 0; i < pattern.BindCount; i++)
        {
            var bind = pattern.GetBind(i);
            if (bind.AfterPatternIndex != patternIndex) continue;

            // Get expression and variable name from source
            var expr = _source.Slice(bind.ExprStart, bind.ExprLength);
            var varName = _source.Slice(bind.VarStart, bind.VarLength);

            // Evaluate the expression
            var evaluator = new BindExpressionEvaluator(expr,
                bindings.GetBindings(),
                bindings.Count,
                bindings.GetStringBuffer());
            var value = evaluator.Evaluate();

            // Bind the result to the target variable using typed overloads
            switch (value.Type)
            {
                case ValueType.Integer:
                    bindings.Bind(varName, value.IntegerValue);
                    break;
                case ValueType.Double:
                    bindings.Bind(varName, value.DoubleValue);
                    break;
                case ValueType.Boolean:
                    bindings.Bind(varName, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.Uri:
                    bindings.Bind(varName, value.StringValue);
                    break;
            }
        }
    }

    private bool TryAdvanceLevel0(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(0);

        if (!_init0)
        {
            _bindingCount0 = bindings.Count;
            InitializeEnumerator0(pattern, ref bindings);
            _init0 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount0);
        }

        return TryAdvanceEnumerator(ref _enumerators![0], pattern, ref bindings);
    }

    private bool TryAdvanceLevel1(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(1);

        if (!_init1)
        {
            _bindingCount1 = bindings.Count;
            InitializeEnumerator1(pattern, ref bindings);
            _init1 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            // If bindings from previous level are missing, backtrack to repopulate
            if (bindings.Count < _bindingCount1)
                return false;
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount1);
        }

        return TryAdvanceEnumerator(ref _enumerators![1], pattern, ref bindings);
    }

    private bool TryAdvanceLevel2(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(2);

        if (!_init2)
        {
            _bindingCount2 = bindings.Count;
            InitializeEnumerator2(pattern, ref bindings);
            _init2 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount2)
                return false;
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount2);
        }

        return TryAdvanceEnumerator(ref _enumerators![2], pattern, ref bindings);
    }

    private bool TryAdvanceLevel3(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(3);

        if (!_init3)
        {
            _bindingCount3 = bindings.Count;
            InitializeEnumerator3(pattern, ref bindings);
            _init3 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount3)
                return false;
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount3);
        }

        return TryAdvanceEnumerator(ref _enumerators![3], pattern, ref bindings);
    }

    private bool TryAdvanceLevel4(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(4);

        if (!_init4)
        {
            _bindingCount4 = bindings.Count;
            InitializeEnumerator4(pattern, ref bindings);
            _init4 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount4)
                return false;
            bindings.TruncateTo(_bindingCount4);
        }

        return TryAdvanceEnumerator(ref _enumerators![4], pattern, ref bindings);
    }

    private bool TryAdvanceLevel5(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(5);

        if (!_init5)
        {
            _bindingCount5 = bindings.Count;
            InitializeEnumerator5(pattern, ref bindings);
            _init5 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount5)
                return false;
            bindings.TruncateTo(_bindingCount5);
        }

        return TryAdvanceEnumerator(ref _enumerators![5], pattern, ref bindings);
    }

    private bool TryAdvanceLevel6(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(6);

        if (!_init6)
        {
            _bindingCount6 = bindings.Count;
            InitializeEnumerator6(pattern, ref bindings);
            _init6 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount6)
                return false;
            bindings.TruncateTo(_bindingCount6);
        }

        return TryAdvanceEnumerator(ref _enumerators![6], pattern, ref bindings);
    }

    private bool TryAdvanceLevel7(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(7);

        if (!_init7)
        {
            _bindingCount7 = bindings.Count;
            InitializeEnumerator7(pattern, ref bindings);
            _init7 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount7)
                return false;
            bindings.TruncateTo(_bindingCount7);
        }

        return TryAdvanceEnumerator(ref _enumerators![7], pattern, ref bindings);
    }

    private bool TryAdvanceLevel8(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(8);

        if (!_init8)
        {
            _bindingCount8 = bindings.Count;
            InitializeEnumerator8(pattern, ref bindings);
            _init8 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount8)
                return false;
            bindings.TruncateTo(_bindingCount8);
        }

        return TryAdvanceEnumerator(ref _enumerators![8], pattern, ref bindings);
    }

    private bool TryAdvanceLevel9(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(9);

        if (!_init9)
        {
            _bindingCount9 = bindings.Count;
            InitializeEnumerator9(pattern, ref bindings);
            _init9 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount9)
                return false;
            bindings.TruncateTo(_bindingCount9);
        }

        return TryAdvanceEnumerator(ref _enumerators![9], pattern, ref bindings);
    }

    private bool TryAdvanceLevel10(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(10);

        if (!_init10)
        {
            _bindingCount10 = bindings.Count;
            InitializeEnumerator10(pattern, ref bindings);
            _init10 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount10)
                return false;
            bindings.TruncateTo(_bindingCount10);
        }

        return TryAdvanceEnumerator(ref _enumerators![10], pattern, ref bindings);
    }

    private bool TryAdvanceLevel11(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(11);

        if (!_init11)
        {
            _bindingCount11 = bindings.Count;
            InitializeEnumerator11(pattern, ref bindings);
            _init11 = true;
        }
        else
        {
            // Check if binding table was externally cleared (e.g., by VALUES filtering)
            if (bindings.Count < _bindingCount11)
                return false;
            bindings.TruncateTo(_bindingCount11);
        }

        return TryAdvanceEnumerator(ref _enumerators![11], pattern, ref bindings);
    }

    // Each init method resolves terms and creates the enumerator.
    // Terms resolve to either a slice of _source (for constants) or empty span (for unbound variables).
    // Bound variables need their value copied to avoid span escaping issues.
    private void InitializeEnumerator0(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![0]);
    }

    private void InitializeEnumerator1(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![1]);
    }

    private void InitializeEnumerator2(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![2]);
    }

    private void InitializeEnumerator3(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![3]);
    }

    private void InitializeEnumerator4(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![4]);
    }

    private void InitializeEnumerator5(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![5]);
    }

    private void InitializeEnumerator6(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![6]);
    }

    private void InitializeEnumerator7(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![7]);
    }

    private void InitializeEnumerator8(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![8]);
    }

    private void InitializeEnumerator9(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![9]);
    }

    private void InitializeEnumerator10(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![10]);
    }

    private void InitializeEnumerator11(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enumerators![11]);
    }

    private void ResolveAndQuery(TriplePattern pattern, scoped ref BindingTable bindings, out TemporalResultEnumerator enumerator)
    {
        // For each term, resolve to a span. Constants come from _source (stable),
        // unbound variables become empty span. Bound variables need special handling.
        // Synthetic terms (negative offsets) come from SPARQL-star expansion.
        // IMPORTANT: Each term uses its own storage field to prevent buffer overwrite issues.
        ReadOnlySpan<char> subject, predicate, obj;

        // Check if this is an inverse path
        bool isInverse = pattern.Path.Type == PathType.Inverse;

        // Resolve subject - store in _expandedSubject if expansion needed
        subject = ResolveTerm(pattern.Subject, ref bindings, TermPosition.Subject);

        // Resolve predicate - for inverse paths, use Path.Iri
        // Store in _expandedPredicate if expansion needed
        predicate = isInverse
            ? ResolveTerm(pattern.Path.Iri, ref bindings, TermPosition.Predicate)
            : ResolveTerm(pattern.Predicate, ref bindings, TermPosition.Predicate);

        // Resolve object - store in _expandedObject if expansion needed
        obj = ResolveTerm(pattern.Object, ref bindings, TermPosition.Object);

        // For inverse paths, swap subject and object in the query
        if (isInverse)
        {
            enumerator = ExecuteTemporalQuery(obj, predicate, subject);
        }
        else
        {
            enumerator = ExecuteTemporalQuery(subject, predicate, obj);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTerm(Term term, scoped ref BindingTable bindings, TermPosition position)
    {
        // Handle synthetic terms (negative offsets from SPARQL-star expansion)
        if (SyntheticTermHelper.IsSynthetic(term.Start))
        {
            if (term.IsVariable)
            {
                var varName = SyntheticTermHelper.GetSyntheticVarName(term.Start);
                var idx = bindings.FindBinding(varName);
                return idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
            }
            return SyntheticTermHelper.GetSyntheticIri(term.Start);
        }

        // Blank nodes in patterns: named (_:b) match literal, anonymous ([]) are wildcards
        if (term.IsBlankNode)
        {
            var bnSpan = _source.Slice(term.Start, term.Length);
            if (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':')
                return bnSpan; // Named blank node - match literal value
            return ReadOnlySpan<char>.Empty; // Anonymous blank node - wildcard
        }

        if (!term.IsVariable)
        {
            var termSpan = _source.Slice(term.Start, term.Length);

            // Handle 'a' shorthand for rdf:type (SPARQL keyword)
            if (termSpan.Length == 1 && termSpan[0] == 'a')
            {
                return SyntheticTermHelper.RdfType.AsSpan();
            }

            // Check if this is a prefixed name that needs expansion
            if (_prefixes != null && termSpan.Length > 0 && termSpan[0] != '<' && termSpan[0] != '"')
            {
                var colonIdx = termSpan.IndexOf(':');
                if (colonIdx >= 0)
                {
                    var prefix = termSpan.Slice(0, colonIdx + 1);
                    var localName = termSpan.Slice(colonIdx + 1);

                    for (int i = 0; i < _prefixes.Length; i++)
                    {
                        var mapping = _prefixes[i];
                        var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);

                        if (prefix.SequenceEqual(mappedPrefix))
                        {
                            // Found matching prefix - expand to full IRI
                            var iriNs = _source.Slice(mapping.IriStart, mapping.IriLength);

                            // IRI namespace is like <http://example.org/> - we need to strip angle brackets and append local name
                            var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1); // Remove trailing >

                            // Store expanded IRI in position-specific field to prevent buffer overwrite
                            var expanded = string.Concat(nsWithoutClose, localName, ">");
                            switch (position)
                            {
                                case TermPosition.Subject:
                                    _expandedSubject = expanded;
                                    return _expandedSubject.AsSpan();
                                case TermPosition.Predicate:
                                    _expandedPredicate = expanded;
                                    return _expandedPredicate.AsSpan();
                                default:
                                    _expandedObject = expanded;
                                    return _expandedObject.AsSpan();
                            }
                        }
                    }
                }
            }

            return termSpan;
        }

        var name = _source.Slice(term.Start, term.Length);
        var index = bindings.FindBinding(name);
        if (index < 0)
            return ReadOnlySpan<char>.Empty;

        // For typed values from BIND, format as proper RDF literal for store matching
        var bindingType = bindings.GetType(index);
        if (bindingType == BindingValueType.Integer)
        {
            // Format as: "2"^^<http://www.w3.org/2001/XMLSchema#integer>
            var intVal = bindings.GetInteger(index);
            var formatted = $"\"{intVal.ToString(CultureInfo.InvariantCulture)}\"^^<http://www.w3.org/2001/XMLSchema#integer>";
            switch (position)
            {
                case TermPosition.Subject:
                    _expandedSubject = formatted;
                    return _expandedSubject.AsSpan();
                case TermPosition.Predicate:
                    _expandedPredicate = formatted;
                    return _expandedPredicate.AsSpan();
                default:
                    _expandedObject = formatted;
                    return _expandedObject.AsSpan();
            }
        }
        else if (bindingType == BindingValueType.Double)
        {
            // Format as: "3.14"^^<http://www.w3.org/2001/XMLSchema#double>
            var doubleVal = bindings.GetDouble(index);
            var formatted = $"\"{doubleVal.ToString(CultureInfo.InvariantCulture)}\"^^<http://www.w3.org/2001/XMLSchema#double>";
            switch (position)
            {
                case TermPosition.Subject:
                    _expandedSubject = formatted;
                    return _expandedSubject.AsSpan();
                case TermPosition.Predicate:
                    _expandedPredicate = formatted;
                    return _expandedPredicate.AsSpan();
                default:
                    _expandedObject = formatted;
                    return _expandedObject.AsSpan();
            }
        }
        else if (bindingType == BindingValueType.Boolean)
        {
            // Format as: "true"^^<http://www.w3.org/2001/XMLSchema#boolean>
            var boolVal = bindings.GetBoolean(index);
            var formatted = $"\"{(boolVal ? "true" : "false")}\"^^<http://www.w3.org/2001/XMLSchema#boolean>";
            switch (position)
            {
                case TermPosition.Subject:
                    _expandedSubject = formatted;
                    return _expandedSubject.AsSpan();
                case TermPosition.Predicate:
                    _expandedPredicate = formatted;
                    return _expandedPredicate.AsSpan();
                default:
                    _expandedObject = formatted;
                    return _expandedObject.AsSpan();
            }
        }

        // For String and Uri types, return the raw value
        return bindings.GetString(index);
    }

    private TemporalResultEnumerator ExecuteTemporalQuery(
        ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        return _temporalMode switch
        {
            TemporalQueryMode.AsOf =>
                _store.QueryAsOf(subject, predicate, obj, _asOfTime, _graph),
            TemporalQueryMode.During =>
                _store.QueryChanges(_rangeStart, _rangeEnd, subject, predicate, obj, _graph),
            TemporalQueryMode.AllVersions =>
                _store.QueryEvolution(subject, predicate, obj, _graph),
            _ => _store.QueryCurrent(subject, predicate, obj, _graph)
        };
    }

    private bool TryAdvanceEnumerator(ref TemporalResultEnumerator enumerator,
        TriplePattern pattern, scoped ref BindingTable bindings)
    {
        bool isInverse = pattern.Path.Type == PathType.Inverse;

        while (enumerator.MoveNext())
        {
            var triple = enumerator.Current;

            // Try to bind variables, checking consistency with existing bindings
            // For inverse paths, swap subject and object bindings
            if (isInverse)
            {
                // Inverse: pattern.Subject binds to triple.Object, pattern.Object binds to triple.Subject
                if (TryBindVariable(pattern.Subject, triple.Object, ref bindings) &&
                    TryBindVariable(pattern.Object, triple.Subject, ref bindings))
                {
                    return true;
                }
            }
            else
            {
                if (TryBindVariable(pattern.Subject, triple.Subject, ref bindings) &&
                    TryBindVariable(pattern.Predicate, triple.Predicate, ref bindings) &&
                    TryBindVariable(pattern.Object, triple.Object, ref bindings))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(Term term, ReadOnlySpan<char> value, scoped ref BindingTable bindings)
    {
        if (!term.IsVariable)
            return true;

        // Handle synthetic variables (from SPARQL-star expansion)
        ReadOnlySpan<char> varName;
        if (SyntheticTermHelper.IsSynthetic(term.Start))
            varName = SyntheticTermHelper.GetSyntheticVarName(term.Start);
        else
            varName = _source.Slice(term.Start, term.Length);

        var existingIndex = bindings.FindBinding(varName);

        if (existingIndex >= 0)
        {
            // Check if existing binding is a typed value (Integer, Double, Boolean)
            // These need semantic comparison with store values (which include type suffixes)
            var bindingType = bindings.GetType(existingIndex);

            if (bindingType == BindingValueType.Integer)
            {
                // Compare integer: existing is numeric, store value is "N"^^<xsd:integer>
                var existingInt = bindings.GetInteger(existingIndex);
                // Try to parse incoming value as typed integer literal
                if (TryParseIntegerLiteral(value, out var parsedInt))
                {
                    return existingInt == parsedInt;
                }
                return false;
            }
            else if (bindingType == BindingValueType.Double)
            {
                // Compare double: existing is numeric, store value is "N.N"^^<xsd:double/decimal>
                var existingDouble = bindings.GetDouble(existingIndex);
                if (TryParseDoubleLiteral(value, out var parsedDouble))
                {
                    return Math.Abs(existingDouble - parsedDouble) < 1e-10;
                }
                return false;
            }
            else if (bindingType == BindingValueType.Boolean)
            {
                // Compare boolean
                var existingBool = bindings.GetBoolean(existingIndex);
                if (TryParseBooleanLiteral(value, out var parsedBool))
                {
                    return existingBool == parsedBool;
                }
                return false;
            }

            // String comparison for String/Uri types
            var existingValue = bindings.GetString(existingIndex);
            return value.SequenceEqual(existingValue);
        }

        bindings.Bind(varName, value);
        return true;
    }

    // Parse typed integer literal: "N"^^<http://www.w3.org/2001/XMLSchema#integer>
    private static bool TryParseIntegerLiteral(ReadOnlySpan<char> literal, out long value)
    {
        value = 0;
        if (literal.Length < 3 || literal[0] != '"')
            return false;

        // Find closing quote
        var closeQuote = literal.Slice(1).IndexOf('"');
        if (closeQuote < 0)
            return false;

        var numberPart = literal.Slice(1, closeQuote);
        return long.TryParse(numberPart, out value);
    }

    // Parse typed double literal: "N.N"^^<http://www.w3.org/2001/XMLSchema#double/decimal>
    private static bool TryParseDoubleLiteral(ReadOnlySpan<char> literal, out double value)
    {
        value = 0;
        if (literal.Length < 3 || literal[0] != '"')
            return false;

        var closeQuote = literal.Slice(1).IndexOf('"');
        if (closeQuote < 0)
            return false;

        var numberPart = literal.Slice(1, closeQuote);
        return double.TryParse(numberPart, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    // Parse typed boolean literal: "true"^^<xsd:boolean> or "false"^^<xsd:boolean>
    private static bool TryParseBooleanLiteral(ReadOnlySpan<char> literal, out bool value)
    {
        value = false;
        if (literal.Length < 5 || literal[0] != '"')
            return false;

        var closeQuote = literal.Slice(1).IndexOf('"');
        if (closeQuote < 0)
            return false;

        var boolPart = literal.Slice(1, closeQuote);
        if (boolPart.SequenceEqual("true".AsSpan()))
        {
            value = true;
            return true;
        }
        if (boolPart.SequenceEqual("false".AsSpan()))
        {
            value = false;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        // ADR-011: Dispose enumerators in pooled array and return array to pool
        // Note: _enumerators can be null if this is a default-constructed struct (e.g., field in another struct)
        if (_enumerators != null && _enumerators.Length > 0)
        {
            for (int i = 0; i < MaxPatternLevels; i++)
            {
                _enumerators![i].Dispose();
            }
            System.Buffers.ArrayPool<TemporalResultEnumerator>.Shared.Return(_enumerators);
            _enumerators = Array.Empty<TemporalResultEnumerator>();
        }
    }
}
