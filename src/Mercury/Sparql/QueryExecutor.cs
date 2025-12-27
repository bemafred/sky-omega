using System;
using System.Buffers;
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

        if (pattern.PatternCount == 0)
            return QueryResults.Empty();

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Single pattern - just scan
        if (pattern.PatternCount == 1)
        {
            var tp = pattern.GetPattern(0);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable);

            // Apply filters if any
            if (pattern.FilterCount > 0)
            {
                return new QueryResults(scan, pattern, _source, bindings, stringBuffer);
            }

            return new QueryResults(scan, bindings, stringBuffer);
        }

        // Multiple patterns - need join
        return ExecuteWithJoins(pattern, bindings, stringBuffer);
    }

    private QueryResults ExecuteWithJoins(GraphPattern pattern, Binding[] bindings, char[] stringBuffer)
    {
        // For now, use nested loop join for all patterns
        // Future: optimize join order based on selectivity
        return new QueryResults(
            new MultiPatternScan(_store, _source, pattern),
            pattern,
            _source,
            bindings,
            stringBuffer);
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
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private BindingTable _bindingTable;
    private readonly bool _hasFilters;
    private readonly bool _isMultiPattern;
    private bool _isEmpty;
    private FilterEvaluator _filterEvaluator;

    public static QueryResults Empty()
    {
        var result = new QueryResults();
        result._isEmpty = true;
        return result;
    }

    internal QueryResults(TriplePatternScan scan, Binding[] bindings, char[] stringBuffer)
    {
        _singleScan = scan;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = false;
        _isMultiPattern = false;
        _isEmpty = false;
    }

    internal QueryResults(TriplePatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        Binding[] bindings, char[] stringBuffer)
    {
        _singleScan = scan;
        _pattern = pattern;
        _source = source;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _isMultiPattern = false;
        _isEmpty = false;
    }

    internal QueryResults(MultiPatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        Binding[] bindings, char[] stringBuffer)
    {
        _multiScan = scan;
        _pattern = pattern;
        _source = source;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _isMultiPattern = true;
        _isEmpty = false;
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

            return true;
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

        var patternCount = Math.Min(_pattern.PatternCount, 4); // Support up to 4 patterns for now

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
