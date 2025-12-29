using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref struct TriplePatternScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly TriplePattern _pattern;
    private TemporalResultEnumerator _enumerator;
    private bool _initialized;
    private readonly BindingTable _initialBindings;
    private readonly int _initialBindingsCount;
    private readonly ReadOnlySpan<char> _graph;

    // For property path traversal
    private readonly bool _isInverse;
    private readonly bool _isZeroOrMore;
    private readonly bool _isOneOrMore;
    private readonly bool _isZeroOrOne;

    // State for transitive path traversal
    private HashSet<string>? _visited;
    private Queue<string>? _frontier;
    private string? _currentNode;
    private bool _emittedReflexive;

    public TriplePatternScan(TripleStore store, ReadOnlySpan<char> source,
        TriplePattern pattern, BindingTable initialBindings, ReadOnlySpan<char> graph = default)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _initialBindings = initialBindings;
        _initialBindingsCount = initialBindings.Count;
        _graph = graph;
        _initialized = false;
        _enumerator = default;

        // Check property path type
        _isInverse = pattern.Path.Type == PathType.Inverse;
        _isZeroOrMore = pattern.Path.Type == PathType.ZeroOrMore;
        _isOneOrMore = pattern.Path.Type == PathType.OneOrMore;
        _isZeroOrOne = pattern.Path.Type == PathType.ZeroOrOne;

        _visited = null;
        _frontier = null;
        _currentNode = null;
        _emittedReflexive = false;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (!_initialized)
        {
            Initialize();
            _initialized = true;
        }

        // Handle transitive paths with BFS
        if (_isZeroOrMore || _isOneOrMore)
        {
            return MoveNextTransitive(ref bindings);
        }

        while (_enumerator.MoveNext())
        {
            var triple = _enumerator.Current;

            // Preserve initial bindings (from subquery join), only clear added bindings
            bindings.TruncateTo(_initialBindingsCount);

            if (_isInverse)
            {
                // For inverse, swap subject and object bindings
                if (!TryBindVariable(_pattern.Subject, triple.Object, ref bindings))
                    continue;
                if (!TryBindVariable(_pattern.Object, triple.Subject, ref bindings))
                    continue;
            }
            else
            {
                // Normal binding
                if (!TryBindVariable(_pattern.Subject, triple.Subject, ref bindings))
                    continue;
                if (!TryBindVariable(_pattern.Predicate, triple.Predicate, ref bindings))
                    continue;
                if (!TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                    continue;
            }

            return true;
        }

        // For zero-or-one, also emit reflexive case if subject == object and not yet emitted
        if (_isZeroOrOne && !_emittedReflexive)
        {
            _emittedReflexive = true;
            bindings.TruncateTo(_initialBindingsCount);

            // Only emit reflexive if subject variable is bound or is concrete
            var subjectSpan = ResolveTermForQuery(_pattern.Subject);
            if (!subjectSpan.IsEmpty)
            {
                // Bind subject to both subject and object positions
                if (TryBindVariable(_pattern.Subject, subjectSpan, ref bindings) &&
                    TryBindVariable(_pattern.Object, subjectSpan, ref bindings))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool MoveNextTransitive(ref BindingTable bindings)
    {
        // BFS for transitive closure
        while (true)
        {
            // Try to get next result from current frontier node
            while (_enumerator.MoveNext())
            {
                var triple = _enumerator.Current;
                var targetNode = triple.Object.ToString();

                if (!_visited!.Contains(targetNode))
                {
                    _visited.Add(targetNode);
                    _frontier!.Enqueue(targetNode);

                    bindings.TruncateTo(_initialBindingsCount);
                    if (TryBindVariable(_pattern.Subject, _source.Slice(_pattern.Subject.Start, _pattern.Subject.Length), ref bindings) &&
                        TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                    {
                        return true;
                    }
                }
            }

            // Move to next frontier node
            _enumerator.Dispose();
            if (_frontier!.Count == 0)
                return false;

            _currentNode = _frontier.Dequeue();
            var predicate = _isInverse
                ? ResolveTermForQuery(_pattern.Path.Iri)
                : ResolveTermForQuery(_pattern.Predicate);

            _enumerator = _store.QueryCurrent(
                _currentNode.AsSpan(),
                predicate,
                ReadOnlySpan<char>.Empty,
                _graph);
        }
    }

    private void Initialize()
    {
        if (_isZeroOrMore || _isOneOrMore)
        {
            InitializeTransitive();
            return;
        }

        // Resolve terms to spans for querying
        var subject = ResolveTermForQuery(_pattern.Subject);
        var obj = ResolveTermForQuery(_pattern.Object);

        ReadOnlySpan<char> predicate;
        if (_isInverse)
        {
            // For inverse path, query with swapped subject/object
            predicate = ResolveTermForQuery(_pattern.Path.Iri);
            _enumerator = _store.QueryCurrent(obj, predicate, subject, _graph);
        }
        else
        {
            predicate = ResolveTermForQuery(_pattern.Predicate);
            _enumerator = _store.QueryCurrent(subject, predicate, obj, _graph);
        }
    }

    private void InitializeTransitive()
    {
        _visited = new HashSet<string>();
        _frontier = new Queue<string>();

        var subject = ResolveTermForQuery(_pattern.Subject);
        var startNode = subject.ToString();

        // For zero-or-more, emit reflexive first
        if (_isZeroOrMore && !_emittedReflexive)
        {
            _emittedReflexive = true;
        }

        _visited.Add(startNode);
        _currentNode = startNode;

        var predicate = _pattern.HasPropertyPath
            ? ResolveTermForQuery(_pattern.Path.Iri)
            : ResolveTermForQuery(_pattern.Predicate);

        _enumerator = _store.QueryCurrent(
            startNode.AsSpan(),
            predicate,
            ReadOnlySpan<char>.Empty,
            _graph);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForQuery(Term term)
    {
        if (term.IsVariable)
        {
            // Check if variable is already bound in initial bindings
            var varName = _source.Slice(term.Start, term.Length);
            var idx = _initialBindings.FindBinding(varName);
            if (idx >= 0)
                return _initialBindings.GetString(idx);

            // Unbound variable becomes wildcard (empty span)
            return ReadOnlySpan<char>.Empty;
        }

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
    private readonly bool _unionMode;
    private readonly ReadOnlySpan<char> _graph;

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

    public MultiPatternScan(TripleStore store, ReadOnlySpan<char> source, GraphPattern pattern,
        bool unionMode = false, ReadOnlySpan<char> graph = default)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _unionMode = unionMode;
        _graph = graph;
        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _exhausted = false;
        _enum0 = default;
        _enum1 = default;
        _enum2 = default;
        _enum3 = default;
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;
    }

    /// <summary>
    /// Get pattern at index, respecting union mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TriplePattern GetPatternAt(int index)
    {
        return _unionMode ? _pattern.GetUnionPattern(index) : _pattern.GetPattern(index);
    }

    /// <summary>
    /// Get the number of patterns to process.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetPatternCount()
    {
        return _unionMode ? _pattern.UnionBranchPatternCount : _pattern.RequiredPatternCount;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        // Get pattern count based on mode
        var patternCount = Math.Min(GetPatternCount(), 4); // Support up to 4 patterns
        if (patternCount == 0)
            return false;

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

        return TryAdvanceEnumerator(ref _enum0, pattern, ref bindings);
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
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount1);
        }

        return TryAdvanceEnumerator(ref _enum1, pattern, ref bindings);
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
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount2);
        }

        return TryAdvanceEnumerator(ref _enum2, pattern, ref bindings);
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

        enumerator = _store.QueryCurrent(subject, predicate, obj, _graph);
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

/// <summary>
/// Scans triple patterns across all named graphs, binding a graph variable.
/// For queries like: SELECT ?g ?s ?p ?o WHERE { GRAPH ?g { ?s ?p ?o } }
/// </summary>
public ref struct VariableGraphScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly GraphClause _graphClause;
    private NamedGraphEnumerator _graphEnum;
    private MultiPatternScan _currentScan;
    private GraphPattern _innerPattern;
    private ReadOnlySpan<char> _currentGraph;
    private readonly int _graphVarHash;
    private bool _initialized;
    private bool _exhausted;

    public VariableGraphScan(TripleStore store, ReadOnlySpan<char> source, GraphClause graphClause)
    {
        _store = store;
        _source = source;
        _graphClause = graphClause;
        _graphEnum = store.GetNamedGraphs();
        _currentScan = default;
        _innerPattern = default;
        _currentGraph = default;
        _initialized = false;
        _exhausted = false;

        // Compute hash for graph variable name for binding
        var graphVarName = source.Slice(graphClause.Graph.Start, graphClause.Graph.Length);
        _graphVarHash = ComputeHash(graphVarName);

        // Build inner pattern from graph clause patterns
        _innerPattern = new GraphPattern();
        for (int i = 0; i < graphClause.PatternCount; i++)
        {
            _innerPattern.AddPattern(graphClause.GetPattern(i));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeHash(ReadOnlySpan<char> s)
    {
        unchecked
        {
            int hash = (int)2166136261;
            foreach (var c in s)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        while (true)
        {
            // Try to get next result from current graph's scan
            if (_initialized && _currentScan.MoveNext(ref bindings))
            {
                // Bind the graph variable
                var graphVarName = _source.Slice(_graphClause.Graph.Start, _graphClause.Graph.Length);
                bindings.Bind(graphVarName, _currentGraph);
                return true;
            }

            // Move to next graph
            if (!_graphEnum.MoveNext())
            {
                _exhausted = true;
                return false;
            }

            // Initialize scan for new graph
            _currentGraph = _graphEnum.Current;
            _currentScan = new MultiPatternScan(_store, _source, _innerPattern, false, _currentGraph);
            _initialized = true;
        }
    }

    public void Dispose()
    {
        _currentScan.Dispose();
    }
}

/// <summary>
/// Executes a subquery and yields only projected variable bindings.
/// Handles variable scoping: only SELECT-ed variables are visible to outer query.
/// </summary>
public ref struct SubQueryScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly SubSelect _subSelect;
    private MultiPatternScan _innerScan;
    private TriplePatternScan _singleScan;
    private bool _isMultiPattern;
    private Binding[] _innerBindingStorage;
    private char[] _innerStringBuffer;
    private BindingTable _innerBindings;
    private GraphPattern _innerPattern;
    private bool _initialized;
    private bool _exhausted;
    private int _skipped;
    private int _returned;
    private HashSet<int>? _seenHashes;

    public SubQueryScan(TripleStore store, ReadOnlySpan<char> source, SubSelect subSelect)
    {
        _store = store;
        _source = source;
        _subSelect = subSelect;
        _innerScan = default;
        _singleScan = default;
        _isMultiPattern = false;
        _innerBindingStorage = new Binding[16];
        _innerStringBuffer = new char[512];
        _innerBindings = new BindingTable(_innerBindingStorage, _innerStringBuffer);
        _innerPattern = default;
        _initialized = false;
        _exhausted = false;
        _skipped = 0;
        _returned = 0;
        _seenHashes = subSelect.Distinct ? new HashSet<int>() : null;
    }

    public bool MoveNext(ref BindingTable outerBindings)
    {
        if (_exhausted)
            return false;

        if (!_initialized)
        {
            Initialize();
            _initialized = true;
        }

        // Check LIMIT
        if (_subSelect.Limit > 0 && _returned >= _subSelect.Limit)
        {
            _exhausted = true;
            return false;
        }

        while (MoveNextInner())
        {
            // Apply filters if any
            if (_subSelect.FilterCount > 0 && !EvaluateFilters())
            {
                _innerBindings.Clear();
                continue;
            }

            // Apply OFFSET
            if (_skipped < _subSelect.Offset)
            {
                _skipped++;
                _innerBindings.Clear();
                continue;
            }

            // Apply DISTINCT
            if (_seenHashes != null)
            {
                var hash = ComputeProjectedBindingsHash();
                if (!_seenHashes.Add(hash))
                {
                    _innerBindings.Clear();
                    continue;
                }
            }

            // Clear outer bindings before projecting new values
            outerBindings.Clear();

            // Project only selected variables to outer bindings
            ProjectVariables(ref outerBindings);
            _innerBindings.Clear();
            _returned++;
            return true;
        }

        _exhausted = true;
        return false;
    }

    private bool MoveNextInner()
    {
        if (_isMultiPattern)
            return _innerScan.MoveNext(ref _innerBindings);
        else
            return _singleScan.MoveNext(ref _innerBindings);
    }

    private bool EvaluateFilters()
    {
        for (int i = 0; i < _subSelect.FilterCount; i++)
        {
            var filter = _subSelect.GetFilter(i);
            var filterExpr = _source.Slice(filter.Start, filter.Length);

            var evaluator = new FilterEvaluator(filterExpr);
            var result = evaluator.Evaluate(
                _innerBindings.GetBindings(),
                _innerBindings.Count,
                _innerBindings.GetStringBuffer());

            if (!result) return false;
        }
        return true;
    }

    private void Initialize()
    {
        // Build a GraphPattern from the SubSelect's patterns
        _innerPattern = new GraphPattern();
        for (int i = 0; i < _subSelect.PatternCount; i++)
        {
            _innerPattern.AddPattern(_subSelect.GetPattern(i));
        }

        // Use single scan for one pattern, multi scan for multiple
        if (_subSelect.PatternCount == 1)
        {
            _isMultiPattern = false;
            var tp = _subSelect.GetPattern(0);
            _singleScan = new TriplePatternScan(_store, _source, tp, _innerBindings);
        }
        else
        {
            _isMultiPattern = true;
            _innerScan = new MultiPatternScan(_store, _source, _innerPattern);
        }
    }

    private void ProjectVariables(ref BindingTable outerBindings)
    {
        if (_subSelect.SelectAll)
        {
            // Project all variables from inner to outer using hash-based binding
            for (int i = 0; i < _innerBindings.Count; i++)
            {
                var varHash = _innerBindings.GetVariableHash(i);
                var value = _innerBindings.GetString(i);
                outerBindings.BindWithHash(varHash, value);
            }
        }
        else
        {
            // Project only explicitly selected variables
            for (int i = 0; i < _subSelect.ProjectedVarCount; i++)
            {
                var (start, len) = _subSelect.GetProjectedVariable(i);
                var varName = _source.Slice(start, len);
                var idx = _innerBindings.FindBinding(varName);
                if (idx >= 0)
                {
                    outerBindings.Bind(varName, _innerBindings.GetString(idx));
                }
            }
        }
    }

    private int ComputeProjectedBindingsHash()
    {
        unchecked
        {
            int hash = (int)2166136261;

            if (_subSelect.SelectAll)
            {
                for (int i = 0; i < _innerBindings.Count; i++)
                {
                    var value = _innerBindings.GetString(i);
                    foreach (var c in value)
                        hash = (hash ^ c) * 16777619;
                }
            }
            else
            {
                for (int i = 0; i < _subSelect.ProjectedVarCount; i++)
                {
                    var (start, len) = _subSelect.GetProjectedVariable(i);
                    var varName = _source.Slice(start, len);
                    var idx = _innerBindings.FindBinding(varName);
                    if (idx >= 0)
                    {
                        var value = _innerBindings.GetString(idx);
                        foreach (var c in value)
                            hash = (hash ^ c) * 16777619;
                    }
                }
            }

            return hash;
        }
    }

    public void Dispose()
    {
        _innerScan.Dispose();
        _singleScan.Dispose();
    }
}

/// <summary>
/// Joins subquery results with outer triple patterns using nested loop join.
/// For queries like: SELECT * WHERE { ?s ?p ?o . { SELECT ?s WHERE { ... } } }
/// Subquery is the driving (outer) relation; outer patterns are filtered using bound variables.
/// </summary>
public ref struct SubQueryJoinScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly GraphPattern _pattern;
    private readonly SubSelect _subSelect;

    // Subquery execution state
    private SubQueryScan _subQueryScan;
    private Binding[] _subBindingStorage;
    private char[] _subStringBuffer;
    private BindingTable _subBindings;

    // Outer pattern execution state
    private MultiPatternScan _outerScan;
    private TriplePatternScan _singleOuterScan;
    private GraphPattern _outerPattern;
    private bool _outerInitialized;
    private bool _useMultiPattern;

    // Current binding checkpoint for rollback
    private int _bindingCheckpoint;

    public SubQueryJoinScan(TripleStore store, ReadOnlySpan<char> source,
        GraphPattern pattern, SubSelect subSelect)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _subSelect = subSelect;

        // Initialize subquery state
        _subQueryScan = new SubQueryScan(store, source, subSelect);
        _subBindingStorage = new Binding[16];
        _subStringBuffer = new char[512];
        _subBindings = new BindingTable(_subBindingStorage, _subStringBuffer);

        // Build outer pattern from non-subquery patterns
        _outerPattern = new GraphPattern();
        for (int i = 0; i < pattern.PatternCount; i++)
        {
            _outerPattern.AddPattern(pattern.GetPattern(i));
        }
        _useMultiPattern = _outerPattern.PatternCount > 1;

        _outerScan = default;
        _singleOuterScan = default;
        _outerInitialized = false;
        _bindingCheckpoint = 0;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        // If no outer patterns, just return subquery results directly
        if (_outerPattern.PatternCount == 0)
        {
            if (!_subQueryScan.MoveNext(ref _subBindings))
                return false;
            CopySubQueryBindings(ref bindings);
            return true;
        }

        while (true)
        {
            // Try to get next result from current outer pattern scan
            if (_outerInitialized)
            {
                bool hasOuter;
                if (_useMultiPattern)
                    hasOuter = _outerScan.MoveNext(ref bindings);
                else
                    hasOuter = _singleOuterScan.MoveNext(ref bindings);

                if (hasOuter)
                {
                    // We have a joined result: subquery bindings + outer pattern bindings
                    return true;
                }

                // Outer patterns exhausted for this subquery row
                // Reset for next subquery row
                _outerInitialized = false;
                if (_useMultiPattern)
                    _outerScan.Dispose();
                else
                    _singleOuterScan.Dispose();
                bindings.TruncateTo(_bindingCheckpoint);
            }

            // Get next subquery result
            if (!_subQueryScan.MoveNext(ref _subBindings))
            {
                return false;
            }

            // Save checkpoint and copy subquery bindings to outer bindings
            _bindingCheckpoint = bindings.Count;
            CopySubQueryBindings(ref bindings);

            // Initialize outer pattern scan with current bindings
            InitializeOuterScan(ref bindings);
            _outerInitialized = true;
        }
    }

    private void CopySubQueryBindings(ref BindingTable bindings)
    {
        // Project subquery variables to outer binding table
        for (int i = 0; i < _subBindings.Count; i++)
        {
            var hash = _subBindings.GetVariableHash(i);
            var value = _subBindings.GetString(i);
            bindings.BindWithHash(hash, value);
        }
        _subBindings.Clear();
    }

    private void InitializeOuterScan(ref BindingTable bindings)
    {
        if (_useMultiPattern)
        {
            // MultiPatternScan with initial bindings
            // MultiPatternScan resolves variables from bindings in ResolveAndQuery
            _outerScan = new MultiPatternScan(_store, _source, _outerPattern);
        }
        else if (_outerPattern.PatternCount == 1)
        {
            var tp = _outerPattern.GetPattern(0);
            _singleOuterScan = new TriplePatternScan(_store, _source, tp, bindings);
        }
    }

    public void Dispose()
    {
        _subQueryScan.Dispose();
        if (_outerInitialized)
        {
            if (_useMultiPattern)
                _outerScan.Dispose();
            else
                _singleOuterScan.Dispose();
        }
    }
}
