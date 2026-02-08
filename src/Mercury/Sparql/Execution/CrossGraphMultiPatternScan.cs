using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Scans triple patterns across multiple graphs with cross-graph joins.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// Unlike DefaultGraphUnionScan which runs each complete pattern against each graph independently,
/// this operator allows joins where one pattern matches in graph1 and another in graph2.
///
/// For example, with FROM graph1 FROM graph2 and pattern { ?s name ?name . ?s age ?age }:
/// - First pattern queries both graph1 and graph2
/// - For each match, second pattern also queries both graph1 and graph2
/// This allows joining ?s=Alice from graph1's name with ?s=Alice from graph2's age.
/// </summary>
internal ref struct CrossGraphMultiPatternScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    // ADR-011: Pattern stored via reference to reduce stack size by ~4KB
    private readonly MultiPatternScan.BoxedPattern _boxedPattern;
    private readonly string[] _graphs;

    // Current state for each pattern level
    // ADR-011 Phase 2: Pooled array instead of inline fields
    private TemporalResultEnumerator[]? _enumerators;
    private const int MaxCrossGraphLevels = 4;

    // Current graph index at each level
    private int _graphIndex0, _graphIndex1, _graphIndex2, _graphIndex3;

    private int _currentLevel;
    private bool _init0, _init1, _init2, _init3;
    private bool _exhausted;

    // Track binding count at entry to each level for rollback
    private int _bindingCount0, _bindingCount1, _bindingCount2, _bindingCount3;

    public CrossGraphMultiPatternScan(QuadStore store, ReadOnlySpan<char> source, GraphPattern pattern, string[] graphs)
    {
        _store = store;
        _source = source;
        // ADR-011: Box pattern to reduce stack size by ~4KB
        _boxedPattern = new MultiPatternScan.BoxedPattern { Pattern = pattern };
        _graphs = graphs;
        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _exhausted = false;
        // ADR-011 Phase 2: Rent enumerator array from pool
        _enumerators = System.Buffers.ArrayPool<TemporalResultEnumerator>.Shared.Rent(MaxCrossGraphLevels);
        _graphIndex0 = _graphIndex1 = _graphIndex2 = _graphIndex3 = 0;
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        var patternCount = Math.Min(_boxedPattern.Pattern.RequiredPatternCount, 4);
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
                    SetGraphIndex(_currentLevel, 0);
                }
            }
            else
            {
                // Current level exhausted across all graphs, backtrack
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

    private void SetGraphIndex(int level, int value)
    {
        switch (level)
        {
            case 0: _graphIndex0 = value; break;
            case 1: _graphIndex1 = value; break;
            case 2: _graphIndex2 = value; break;
            case 3: _graphIndex3 = value; break;
        }
    }

    private bool TryAdvanceLevel0(scoped ref BindingTable bindings)
    {
        var pattern = GetRequiredPattern(0);

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            if (!_init0)
            {
                _bindingCount0 = bindings.Count;
                if (_graphIndex0 >= _graphs.Length)
                    return false; // All graphs exhausted at this level
                InitializeEnumerator(pattern, ref bindings, _graphs[_graphIndex0].AsSpan(), out _enumerators![0]);
                _init0 = true;
            }
            else
            {
                // Rolling back bindings from previous attempt
                bindings.TruncateTo(_bindingCount0);
            }

            if (TryAdvanceEnumerator(ref _enumerators![0], pattern, ref bindings))
                return true;

            // Current graph exhausted, try next graph
            _enumerators![0].Dispose();
            _graphIndex0++;
            _init0 = false;

            if (_graphIndex0 >= _graphs.Length)
                return false;
        }
    }

    private bool TryAdvanceLevel1(scoped ref BindingTable bindings)
    {
        var pattern = GetRequiredPattern(1);

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            if (!_init1)
            {
                _bindingCount1 = bindings.Count;
                if (_graphIndex1 >= _graphs.Length)
                    return false;
                InitializeEnumerator(pattern, ref bindings, _graphs[_graphIndex1].AsSpan(), out _enumerators![1]);
                _init1 = true;
            }
            else
            {
                bindings.TruncateTo(_bindingCount1);
            }

            if (TryAdvanceEnumerator(ref _enumerators![1], pattern, ref bindings))
                return true;

            _enumerators![1].Dispose();
            _graphIndex1++;
            _init1 = false;

            if (_graphIndex1 >= _graphs.Length)
                return false;
        }
    }

    private bool TryAdvanceLevel2(scoped ref BindingTable bindings)
    {
        var pattern = GetRequiredPattern(2);

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            if (!_init2)
            {
                _bindingCount2 = bindings.Count;
                if (_graphIndex2 >= _graphs.Length)
                    return false;
                InitializeEnumerator(pattern, ref bindings, _graphs[_graphIndex2].AsSpan(), out _enumerators![2]);
                _init2 = true;
            }
            else
            {
                bindings.TruncateTo(_bindingCount2);
            }

            if (TryAdvanceEnumerator(ref _enumerators![2], pattern, ref bindings))
                return true;

            _enumerators![2].Dispose();
            _graphIndex2++;
            _init2 = false;

            if (_graphIndex2 >= _graphs.Length)
                return false;
        }
    }

    private bool TryAdvanceLevel3(scoped ref BindingTable bindings)
    {
        var pattern = GetRequiredPattern(3);

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            if (!_init3)
            {
                _bindingCount3 = bindings.Count;
                if (_graphIndex3 >= _graphs.Length)
                    return false;
                InitializeEnumerator(pattern, ref bindings, _graphs[_graphIndex3].AsSpan(), out _enumerators![3]);
                _init3 = true;
            }
            else
            {
                bindings.TruncateTo(_bindingCount3);
            }

            if (TryAdvanceEnumerator(ref _enumerators![3], pattern, ref bindings))
                return true;

            _enumerators![3].Dispose();
            _graphIndex3++;
            _init3 = false;

            if (_graphIndex3 >= _graphs.Length)
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TriplePattern GetRequiredPattern(int index)
    {
        // Find the nth required (non-optional) pattern
        ref readonly var pattern = ref _boxedPattern.Pattern;
        int found = 0;
        for (int i = 0; i < pattern.PatternCount; i++)
        {
            if (!pattern.IsOptional(i))
            {
                if (found == index)
                    return pattern.GetPattern(i);
                found++;
            }
        }
        return default;
    }

    private void InitializeEnumerator(TriplePattern pattern, scoped ref BindingTable bindings,
        ReadOnlySpan<char> graph, out TemporalResultEnumerator enumerator)
    {
        ReadOnlySpan<char> subject, predicate, obj;

        // Resolve subject - named blank nodes (_:name) match literal, anonymous ([]) are wildcards
        if (pattern.Subject.IsBlankNode)
        {
            var bnSpan = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
            subject = (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':') ? bnSpan : ReadOnlySpan<char>.Empty;
        }
        else if (!pattern.Subject.IsVariable)
            subject = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
        else
        {
            var varName = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
            var idx = bindings.FindBinding(varName);
            subject = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        // Resolve predicate - named blank nodes match literal, anonymous are wildcards
        if (pattern.Predicate.IsBlankNode)
        {
            var bnSpan = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
            predicate = (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':') ? bnSpan : ReadOnlySpan<char>.Empty;
        }
        else if (!pattern.Predicate.IsVariable)
            predicate = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
        else
        {
            var varName = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
            var idx = bindings.FindBinding(varName);
            predicate = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        // Resolve object - named blank nodes match literal, anonymous are wildcards
        if (pattern.Object.IsBlankNode)
        {
            var bnSpan = _source.Slice(pattern.Object.Start, pattern.Object.Length);
            obj = (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':') ? bnSpan : ReadOnlySpan<char>.Empty;
        }
        else if (!pattern.Object.IsVariable)
            obj = _source.Slice(pattern.Object.Start, pattern.Object.Length);
        else
        {
            var varName = _source.Slice(pattern.Object.Start, pattern.Object.Length);
            var idx = bindings.FindBinding(varName);
            obj = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        enumerator = _store.QueryCurrent(subject, predicate, obj, graph);
    }

    private bool TryAdvanceEnumerator(ref TemporalResultEnumerator enumerator,
        TriplePattern pattern, scoped ref BindingTable bindings)
    {
        while (enumerator.MoveNext())
        {
            var triple = enumerator.Current;

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
        // ADR-011 Phase 2: Dispose enumerators and return array to pool
        if (_enumerators != null && _enumerators.Length > 0)
        {
            if (_init0) _enumerators[0].Dispose();
            if (_init1) _enumerators[1].Dispose();
            if (_init2) _enumerators[2].Dispose();
            if (_init3) _enumerators[3].Dispose();
            System.Buffers.ArrayPool<TemporalResultEnumerator>.Shared.Return(_enumerators);
            _enumerators = null;
        }
    }
}

// NOTE: ServiceScan operator was removed and replaced with ServicePatternScan in ServiceMaterializer.cs.
// The new implementation separates fetching (FetchServiceResults) from iteration (ServicePatternScan),
// enabling optimizations like fetching SERVICE results once for join scenarios.
// See QueryExecutor.Service.cs for the integration.

// NOTE: ServiceJoinScan operator was replaced with materialization pattern.
// The QueryResults ref struct (~22KB) combined with large GraphPattern (~4KB) can
// exceed stack limits in complex query paths. The fix is to materialize SERVICE
// results to List<MaterializedRow> early, returning only heap pointers through the
// call chain. See docs/mercury-adr-buffer-pattern.md for the pattern details.
