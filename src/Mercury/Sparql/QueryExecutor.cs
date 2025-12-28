using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql;

/// <summary>
/// Zero-allocation SPARQL query executor using specialized operators.
///
/// Execution model:
/// 1. Parse query → Query struct with triple patterns + filters
/// 2. Build execution plan → Stack of operators
/// 3. Execute → Pull-based iteration through operator pipeline
/// </summary>
public ref struct QueryExecutor
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly Query _query;

    public QueryExecutor(TripleStore store, ReadOnlySpan<char> source, Query query)
    {
        _store = store;
        _source = source;
        _query = query;
    }

    /// <summary>
    /// Execute a parsed query and return results.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public QueryResults Execute()
    {
        var pattern = _query.WhereClause.Pattern;
        var limit = _query.SolutionModifier.Limit;
        var offset = _query.SolutionModifier.Offset;
        var distinct = _query.SelectClause.Distinct;

        if (pattern.PatternCount == 0)
            return QueryResults.Empty();

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        var requiredCount = pattern.RequiredPatternCount;

        // Single required pattern - just scan
        if (requiredCount == 1)
        {
            // Find the first required pattern
            int requiredIdx = 0;
            for (int i = 0; i < pattern.PatternCount; i++)
            {
                if (!pattern.IsOptional(i)) { requiredIdx = i; break; }
            }

            var tp = pattern.GetPattern(requiredIdx);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable);

            return new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer, limit, offset, distinct);
        }

        // No required patterns but have optional - need special handling
        if (requiredCount == 0)
        {
            // All patterns are optional - start with empty bindings and try to match optionals
            // For now, just return empty (proper implementation would need different semantics)
            return QueryResults.Empty();
        }

        // Multiple required patterns - need join
        return ExecuteWithJoins(pattern, bindings, stringBuffer, limit, offset, distinct);
    }

    private QueryResults ExecuteWithJoins(GraphPattern pattern, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct)
    {
        // Use nested loop join for required patterns only
        return new QueryResults(
            new MultiPatternScan(_store, _source, pattern),
            pattern,
            _source,
            _store,
            bindings,
            stringBuffer,
            limit,
            offset,
            distinct);
    }
}

/// <summary>
/// Results from query execution. Must be disposed to return pooled resources.
/// </summary>
public ref struct QueryResults
{
    private TriplePatternScan _singleScan;
    private MultiPatternScan _multiScan;
    private GraphPattern _pattern;
    private ReadOnlySpan<char> _source;
    private TripleStore? _store;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private BindingTable _bindingTable;
    private readonly bool _hasFilters;
    private readonly bool _hasOptional;
    private readonly bool _isMultiPattern;
    private bool _isEmpty;
    private FilterEvaluator _filterEvaluator;

    // LIMIT/OFFSET support
    private readonly int _limit;
    private readonly int _offset;
    private int _skipped;
    private int _returned;

    // DISTINCT support
    private readonly bool _distinct;
    private HashSet<int>? _seenHashes;

    public static QueryResults Empty()
    {
        var result = new QueryResults();
        result._isEmpty = true;
        return result;
    }

    internal QueryResults(TriplePatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false)
    {
        _singleScan = scan;
        _pattern = pattern;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _hasOptional = pattern.HasOptionalPatterns;
        _isMultiPattern = false;
        _isEmpty = false;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
    }

    internal QueryResults(MultiPatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false)
    {
        _multiScan = scan;
        _pattern = pattern;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _hasOptional = pattern.HasOptionalPatterns;
        _isMultiPattern = true;
        _isEmpty = false;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
    }

    /// <summary>
    /// Current result row with variable bindings.
    /// </summary>
    public readonly BindingTable Current => _bindingTable;

    /// <summary>
    /// Move to next result row.
    /// </summary>
    public bool MoveNext()
    {
        if (_isEmpty) return false;

        // Check if we've hit the limit
        if (_limit > 0 && _returned >= _limit)
            return false;

        while (true)
        {
            bool hasNext;

            if (_isMultiPattern)
            {
                hasNext = _multiScan.MoveNext(ref _bindingTable);
            }
            else
            {
                hasNext = _singleScan.MoveNext(ref _bindingTable);
            }

            if (!hasNext) return false;

            // Apply filters
            if (_hasFilters)
            {
                if (!EvaluateFilters())
                {
                    _bindingTable.Clear();
                    continue; // Try next row
                }
            }

            // Try to extend with optional patterns (left outer join semantics)
            if (_hasOptional)
            {
                TryMatchOptionalPatterns();
            }

            // Apply DISTINCT - skip duplicate rows
            if (_distinct)
            {
                var hash = ComputeBindingsHash();
                if (!_seenHashes!.Add(hash))
                {
                    _bindingTable.Clear();
                    continue; // Duplicate, try next row
                }
            }

            // Apply OFFSET - skip results until we've skipped enough
            if (_skipped < _offset)
            {
                _skipped++;
                _bindingTable.Clear();
                continue;
            }

            _returned++;
            return true;
        }
    }

    /// <summary>
    /// Try to match optional patterns and extend bindings.
    /// If a pattern doesn't match, we continue without it (left outer join).
    /// </summary>
    private void TryMatchOptionalPatterns()
    {
        if (_store == null) return;

        for (int i = 0; i < _pattern.PatternCount; i++)
        {
            if (!_pattern.IsOptional(i)) continue;

            var optPattern = _pattern.GetPattern(i);
            TryMatchSingleOptionalPattern(optPattern);
        }
    }

    /// <summary>
    /// Try to match a single optional pattern and bind its variables.
    /// </summary>
    private void TryMatchSingleOptionalPattern(TriplePattern pattern)
    {
        if (_store == null) return;

        // Resolve terms - variables that are already bound use their value,
        // unbound variables become wildcards
        var subject = ResolveTermForOptional(pattern.Subject);
        var predicate = ResolveTermForOptional(pattern.Predicate);
        var obj = ResolveTermForOptional(pattern.Object);

        // Query the store
        var results = _store.QueryCurrent(subject, predicate, obj);
        try
        {
            if (results.MoveNext())
            {
                var triple = results.Current;

                // Bind any unbound variables from the result
                TryBindOptionalVariable(pattern.Subject, triple.Subject);
                TryBindOptionalVariable(pattern.Predicate, triple.Predicate);
                TryBindOptionalVariable(pattern.Object, triple.Object);
            }
            // If no match, we just don't add bindings (left outer join semantics)
        }
        finally
        {
            results.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForOptional(Term term)
    {
        if (!term.IsVariable)
        {
            // Constant - use source text
            return _source.Slice(term.Start, term.Length);
        }

        // Check if variable is already bound
        var varName = _source.Slice(term.Start, term.Length);
        var idx = _bindingTable.FindBinding(varName);
        if (idx >= 0)
        {
            // Use bound value
            return _bindingTable.GetString(idx);
        }

        // Unbound - use wildcard
        return ReadOnlySpan<char>.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryBindOptionalVariable(Term term, ReadOnlySpan<char> value)
    {
        if (!term.IsVariable) return;

        var varName = _source.Slice(term.Start, term.Length);

        // Only bind if not already bound
        if (_bindingTable.FindBinding(varName) < 0)
        {
            _bindingTable.Bind(varName, value);
        }
    }

    /// <summary>
    /// Compute a hash of all current bindings for DISTINCT checking.
    /// Uses FNV-1a hash combined across all binding values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComputeBindingsHash()
    {
        unchecked
        {
            int hash = (int)2166136261; // FNV offset basis

            for (int i = 0; i < _bindingTable.Count; i++)
            {
                var value = _bindingTable.GetString(i);
                foreach (var ch in value)
                {
                    hash = (hash ^ ch) * 16777619; // FNV prime
                }
                hash = (hash ^ '|') * 16777619; // Separator between bindings
            }

            return hash;
        }
    }

    private bool EvaluateFilters()
    {
        for (int i = 0; i < _pattern.FilterCount; i++)
        {
            var filter = _pattern.GetFilter(i);
            var filterExpr = _source.Slice(filter.Start, filter.Length);

            _filterEvaluator = new FilterEvaluator(filterExpr);
            var result = _filterEvaluator.Evaluate(
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer());

            if (!result) return false;
        }
        return true;
    }

    public void Dispose()
    {
        _singleScan.Dispose();
        _multiScan.Dispose();
    }
}

/// <summary>
/// Scans a single triple pattern against the store.
/// Binds matching values to variables.
/// </summary>
public ref struct TriplePatternScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly TriplePattern _pattern;
    private TemporalResultEnumerator _enumerator;
    private bool _initialized;
    private readonly BindingTable _initialBindings;

    public TriplePatternScan(TripleStore store, ReadOnlySpan<char> source,
        TriplePattern pattern, BindingTable initialBindings)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _initialBindings = initialBindings;
        _initialized = false;
        _enumerator = default;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (!_initialized)
        {
            Initialize();
            _initialized = true;
        }

        while (_enumerator.MoveNext())
        {
            var triple = _enumerator.Current;

            bindings.Clear();

            // Bind variables to values from result
            if (!TryBindVariable(_pattern.Subject, triple.Subject, ref bindings))
                continue;
            if (!TryBindVariable(_pattern.Predicate, triple.Predicate, ref bindings))
                continue;
            if (!TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                continue;

            return true;
        }

        return false;
    }

    private void Initialize()
    {
        // Resolve terms to spans for querying
        var subject = ResolveTermForQuery(_pattern.Subject);
        var predicate = ResolveTermForQuery(_pattern.Predicate);
        var obj = ResolveTermForQuery(_pattern.Object);

        _enumerator = _store.QueryCurrent(subject, predicate, obj);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForQuery(Term term)
    {
        // Variables become wildcards (empty span)
        if (term.IsVariable)
            return ReadOnlySpan<char>.Empty;

        // IRIs and literals use their source text
        return _source.Slice(term.Start, term.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(Term term, ReadOnlySpan<char> value, ref BindingTable bindings)
    {
        if (!term.IsVariable)
        {
            // Constant - verify it matches (should already match from query)
            return true;
        }

        var varName = _source.Slice(term.Start, term.Length);

        // Check if already bound (from earlier pattern in join)
        var existingIndex = bindings.FindBinding(varName);
        if (existingIndex >= 0)
        {
            // Must match existing binding
            var existingValue = bindings.GetString(existingIndex);
            return value.SequenceEqual(existingValue);
        }

        // Bind new variable
        bindings.Bind(varName, value);
        return true;
    }

    public void Dispose()
    {
        _enumerator.Dispose();
    }
}

/// <summary>
/// Executes multiple triple patterns using nested loop join.
/// Due to ref struct limitations, we store resolved term strings in fixed buffers
/// to avoid span scoping issues with enumerator initialization.
/// </summary>
public ref struct MultiPatternScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly GraphPattern _pattern;

    // Current state for each pattern level
    private TemporalResultEnumerator _enum0;
    private TemporalResultEnumerator _enum1;
    private TemporalResultEnumerator _enum2;
    private TemporalResultEnumerator _enum3;

    private int _currentLevel;
    private bool _init0, _init1, _init2, _init3;
    private bool _exhausted;

    // Track binding count at entry to each level for rollback
    private int _bindingCount0, _bindingCount1, _bindingCount2, _bindingCount3;

    public MultiPatternScan(TripleStore store, ReadOnlySpan<char> source, GraphPattern pattern)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _exhausted = false;
        _enum0 = default;
        _enum1 = default;
        _enum2 = default;
        _enum3 = default;
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted || _pattern.PatternCount == 0)
            return false;

        // Only process required patterns - optional patterns are handled separately
        var patternCount = Math.Min(_pattern.RequiredPatternCount, 4); // Support up to 4 required patterns

        while (true)
        {
            bool advanced;
            switch (_currentLevel)
            {
                case 0: advanced = TryAdvanceLevel0(ref bindings); break;
                case 1: advanced = TryAdvanceLevel1(ref bindings); break;
                case 2: advanced = TryAdvanceLevel2(ref bindings); break;
                case 3: advanced = TryAdvanceLevel3(ref bindings); break;
                default: advanced = false; break;
            }

            if (advanced)
            {
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
        }
    }

    private bool TryAdvanceLevel0(scoped ref BindingTable bindings)
    {
        var pattern = _pattern.GetPattern(0);

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

        return TryAdvanceEnumerator(ref _enum0, pattern, ref bindings);
    }

    private bool TryAdvanceLevel1(scoped ref BindingTable bindings)
    {
        var pattern = _pattern.GetPattern(1);

        if (!_init1)
        {
            _bindingCount1 = bindings.Count;
            InitializeEnumerator1(pattern, ref bindings);
            _init1 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount1);
        }

        return TryAdvanceEnumerator(ref _enum1, pattern, ref bindings);
    }

    private bool TryAdvanceLevel2(scoped ref BindingTable bindings)
    {
        var pattern = _pattern.GetPattern(2);

        if (!_init2)
        {
            _bindingCount2 = bindings.Count;
            InitializeEnumerator2(pattern, ref bindings);
            _init2 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount2);
        }

        return TryAdvanceEnumerator(ref _enum2, pattern, ref bindings);
    }

    private bool TryAdvanceLevel3(scoped ref BindingTable bindings)
    {
        var pattern = _pattern.GetPattern(3);

        if (!_init3)
        {
            _bindingCount3 = bindings.Count;
            InitializeEnumerator3(pattern, ref bindings);
            _init3 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount3);
        }

        return TryAdvanceEnumerator(ref _enum3, pattern, ref bindings);
    }

    // Each init method resolves terms and creates the enumerator.
    // Terms resolve to either a slice of _source (for constants) or empty span (for unbound variables).
    // Bound variables need their value copied to avoid span escaping issues.
    private void InitializeEnumerator0(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum0);
    }

    private void InitializeEnumerator1(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum1);
    }

    private void InitializeEnumerator2(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum2);
    }

    private void InitializeEnumerator3(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum3);
    }

    private void ResolveAndQuery(TriplePattern pattern, scoped ref BindingTable bindings, out TemporalResultEnumerator enumerator)
    {
        // For each term, resolve to a span. Constants come from _source (stable),
        // unbound variables become empty span. Bound variables need special handling.
        ReadOnlySpan<char> subject, predicate, obj;

        // Resolve subject
        if (!pattern.Subject.IsVariable)
            subject = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
        else
        {
            var varName = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
            var idx = bindings.FindBinding(varName);
            subject = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        // Resolve predicate
        if (!pattern.Predicate.IsVariable)
            predicate = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
        else
        {
            var varName = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
            var idx = bindings.FindBinding(varName);
            predicate = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        // Resolve object
        if (!pattern.Object.IsVariable)
            obj = _source.Slice(pattern.Object.Start, pattern.Object.Length);
        else
        {
            var varName = _source.Slice(pattern.Object.Start, pattern.Object.Length);
            var idx = bindings.FindBinding(varName);
            obj = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        enumerator = _store.QueryCurrent(subject, predicate, obj);
    }

    private bool TryAdvanceEnumerator(ref TemporalResultEnumerator enumerator,
        TriplePattern pattern, scoped ref BindingTable bindings)
    {
        while (enumerator.MoveNext())
        {
            var triple = enumerator.Current;

            // Try to bind variables, checking consistency with existing bindings
            if (TryBindVariable(pattern.Subject, triple.Subject, ref bindings) &&
                TryBindVariable(pattern.Predicate, triple.Predicate, ref bindings) &&
                TryBindVariable(pattern.Object, triple.Object, ref bindings))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(Term term, ReadOnlySpan<char> value, scoped ref BindingTable bindings)
    {
        if (!term.IsVariable)
            return true;

        var varName = _source.Slice(term.Start, term.Length);
        var existingIndex = bindings.FindBinding(varName);

        if (existingIndex >= 0)
        {
            var existingValue = bindings.GetString(existingIndex);
            return value.SequenceEqual(existingValue);
        }

        bindings.Bind(varName, value);
        return true;
    }

    public void Dispose()
    {
        _enum0.Dispose();
        _enum1.Dispose();
        _enum2.Dispose();
        _enum3.Dispose();
    }
}
