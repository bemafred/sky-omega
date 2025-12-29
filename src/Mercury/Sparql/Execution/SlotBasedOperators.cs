using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Triple pattern scan that reads from PatternSlot instead of TriplePattern struct.
/// This eliminates the need to copy large structs through method calls.
/// </summary>
public ref struct SlotTriplePatternScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly ReadOnlySpan<char> _graph;

    // Pattern data extracted from slot (avoiding struct copy)
    private readonly TermType _subjectType;
    private readonly int _subjectStart;
    private readonly int _subjectLength;
    private readonly TermType _predicateType;
    private readonly int _predicateStart;
    private readonly int _predicateLength;
    private readonly TermType _objectType;
    private readonly int _objectStart;
    private readonly int _objectLength;
    private readonly PathType _pathType;
    private readonly int _pathIriStart;
    private readonly int _pathIriLength;

    private TemporalResultEnumerator _enumerator;
    private bool _initialized;
    private readonly BindingTable _initialBindings;
    private readonly int _initialBindingsCount;

    // Property path flags
    private readonly bool _isInverse;
    private readonly bool _isZeroOrMore;
    private readonly bool _isOneOrMore;
    private readonly bool _isZeroOrOne;

    // State for transitive path traversal
    private HashSet<string>? _visited;
    private Queue<string>? _frontier;
    private string? _currentNode;
    private bool _emittedReflexive;

    /// <summary>
    /// Create scan from a PatternSlot (must be Triple kind)
    /// </summary>
    public SlotTriplePatternScan(QuadStore store, ReadOnlySpan<char> source,
        PatternSlot slot, BindingTable initialBindings, ReadOnlySpan<char> graph = default)
    {
        if (slot.Kind != PatternKind.Triple)
            throw new ArgumentException("Slot must be Triple kind", nameof(slot));

        _store = store;
        _source = source;
        _graph = graph;
        _initialBindings = initialBindings;
        _initialBindingsCount = initialBindings.Count;
        _initialized = false;
        _enumerator = default;

        // Extract pattern data from slot
        _subjectType = slot.SubjectType;
        _subjectStart = slot.SubjectStart;
        _subjectLength = slot.SubjectLength;
        _predicateType = slot.PredicateType;
        _predicateStart = slot.PredicateStart;
        _predicateLength = slot.PredicateLength;
        _objectType = slot.ObjectType;
        _objectStart = slot.ObjectStart;
        _objectLength = slot.ObjectLength;
        _pathType = slot.PathKind;
        _pathIriStart = slot.PathIriStart;
        _pathIriLength = slot.PathIriLength;

        // Check property path type
        _isInverse = _pathType == PathType.Inverse;
        _isZeroOrMore = _pathType == PathType.ZeroOrMore;
        _isOneOrMore = _pathType == PathType.OneOrMore;
        _isZeroOrOne = _pathType == PathType.ZeroOrOne;

        _visited = null;
        _frontier = null;
        _currentNode = null;
        _emittedReflexive = false;
    }

    /// <summary>
    /// Create scan from explicit term components (for internal use)
    /// </summary>
    public SlotTriplePatternScan(QuadStore store, ReadOnlySpan<char> source,
        TermType sType, int sStart, int sLen,
        TermType pType, int pStart, int pLen,
        TermType oType, int oStart, int oLen,
        BindingTable initialBindings, ReadOnlySpan<char> graph = default,
        PathType pathType = PathType.None, int pathStart = 0, int pathLen = 0)
    {
        _store = store;
        _source = source;
        _graph = graph;
        _initialBindings = initialBindings;
        _initialBindingsCount = initialBindings.Count;
        _initialized = false;
        _enumerator = default;

        _subjectType = sType;
        _subjectStart = sStart;
        _subjectLength = sLen;
        _predicateType = pType;
        _predicateStart = pStart;
        _predicateLength = pLen;
        _objectType = oType;
        _objectStart = oStart;
        _objectLength = oLen;
        _pathType = pathType;
        _pathIriStart = pathStart;
        _pathIriLength = pathLen;

        _isInverse = pathType == PathType.Inverse;
        _isZeroOrMore = pathType == PathType.ZeroOrMore;
        _isOneOrMore = pathType == PathType.OneOrMore;
        _isZeroOrOne = pathType == PathType.ZeroOrOne;

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

            // Preserve initial bindings, only clear added bindings
            bindings.TruncateTo(_initialBindingsCount);

            if (_isInverse)
            {
                // For inverse, swap subject and object bindings
                if (!TryBindVariable(_subjectType, _subjectStart, _subjectLength, triple.Object, ref bindings))
                    continue;
                if (!TryBindVariable(_objectType, _objectStart, _objectLength, triple.Subject, ref bindings))
                    continue;
            }
            else
            {
                // Normal binding
                if (!TryBindVariable(_subjectType, _subjectStart, _subjectLength, triple.Subject, ref bindings))
                    continue;
                if (!TryBindVariable(_predicateType, _predicateStart, _predicateLength, triple.Predicate, ref bindings))
                    continue;
                if (!TryBindVariable(_objectType, _objectStart, _objectLength, triple.Object, ref bindings))
                    continue;
            }

            return true;
        }

        // For zero-or-one, also emit reflexive case
        if (_isZeroOrOne && !_emittedReflexive)
        {
            _emittedReflexive = true;
            bindings.TruncateTo(_initialBindingsCount);

            var subjectSpan = ResolveTermForQuery(_subjectType, _subjectStart, _subjectLength);
            if (!subjectSpan.IsEmpty)
            {
                if (TryBindVariable(_subjectType, _subjectStart, _subjectLength, subjectSpan, ref bindings) &&
                    TryBindVariable(_objectType, _objectStart, _objectLength, subjectSpan, ref bindings))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool MoveNextTransitive(ref BindingTable bindings)
    {
        while (true)
        {
            while (_enumerator.MoveNext())
            {
                var triple = _enumerator.Current;
                var targetNode = triple.Object.ToString();

                if (!_visited!.Contains(targetNode))
                {
                    _visited.Add(targetNode);
                    _frontier!.Enqueue(targetNode);

                    bindings.TruncateTo(_initialBindingsCount);
                    if (TryBindVariable(_subjectType, _subjectStart, _subjectLength,
                            _source.Slice(_subjectStart, _subjectLength), ref bindings) &&
                        TryBindVariable(_objectType, _objectStart, _objectLength, triple.Object, ref bindings))
                    {
                        return true;
                    }
                }
            }

            _enumerator.Dispose();
            if (_frontier!.Count == 0)
                return false;

            _currentNode = _frontier.Dequeue();
            var predicate = _pathType != PathType.None
                ? _source.Slice(_pathIriStart, _pathIriLength)
                : _source.Slice(_predicateStart, _predicateLength);

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

        var subject = ResolveTermForQuery(_subjectType, _subjectStart, _subjectLength);
        var obj = ResolveTermForQuery(_objectType, _objectStart, _objectLength);
        ReadOnlySpan<char> predicate;

        if (_isInverse)
        {
            predicate = _pathType != PathType.None
                ? _source.Slice(_pathIriStart, _pathIriLength)
                : _source.Slice(_predicateStart, _predicateLength);
            _enumerator = _store.QueryCurrent(obj, predicate, subject, _graph);
        }
        else
        {
            predicate = ResolveTermForQuery(_predicateType, _predicateStart, _predicateLength);
            _enumerator = _store.QueryCurrent(subject, predicate, obj, _graph);
        }
    }

    private void InitializeTransitive()
    {
        _visited = new HashSet<string>();
        _frontier = new Queue<string>();

        var subject = ResolveTermForQuery(_subjectType, _subjectStart, _subjectLength);
        var startNode = subject.ToString();

        if (_isZeroOrMore && !_emittedReflexive)
        {
            _emittedReflexive = true;
        }

        _visited.Add(startNode);
        _currentNode = startNode;

        var predicate = _pathType != PathType.None
            ? _source.Slice(_pathIriStart, _pathIriLength)
            : _source.Slice(_predicateStart, _predicateLength);

        _enumerator = _store.QueryCurrent(
            startNode.AsSpan(),
            predicate,
            ReadOnlySpan<char>.Empty,
            _graph);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForQuery(TermType type, int start, int length)
    {
        // Handle synthetic terms (negative offsets from SPARQL-star expansion)
        if (SyntheticTermHelper.IsSynthetic(start))
        {
            if (type == TermType.Variable)
            {
                var varName = SyntheticTermHelper.GetSyntheticVarName(start);
                var idx = _initialBindings.FindBinding(varName);
                return idx >= 0 ? _initialBindings.GetString(idx) : ReadOnlySpan<char>.Empty;
            }
            return SyntheticTermHelper.GetSyntheticIri(start);
        }

        if (type == TermType.Variable)
        {
            var varName = _source.Slice(start, length);
            var idx = _initialBindings.FindBinding(varName);
            if (idx >= 0)
                return _initialBindings.GetString(idx);
            return ReadOnlySpan<char>.Empty;
        }

        return _source.Slice(start, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(TermType type, int start, int length,
        ReadOnlySpan<char> value, ref BindingTable bindings)
    {
        if (type != TermType.Variable)
            return true;

        // Handle synthetic variables (from SPARQL-star expansion)
        ReadOnlySpan<char> varName;
        if (SyntheticTermHelper.IsSynthetic(start))
            varName = SyntheticTermHelper.GetSyntheticVarName(start);
        else
            varName = _source.Slice(start, length);

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
        _enumerator.Dispose();
    }
}

/// <summary>
/// Multi-pattern scan that reads from PatternArray instead of GraphPattern struct.
/// Uses nested loop join for up to 4 patterns.
/// </summary>
public ref struct SlotMultiPatternScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly ReadOnlySpan<char> _graph;
    private readonly byte[] _patternBuffer;  // Reference to the pattern buffer
    private readonly int _patternCount;
    private readonly int _tripleCount;  // Number of triple patterns (vs filters, binds, etc.)

    // Inline storage for up to 4 triple pattern indices
    private int _tripleIdx0, _tripleIdx1, _tripleIdx2, _tripleIdx3;

    // Per-level state
    private int _currentLevel;
    private bool _init0, _init1, _init2, _init3;
    private bool _exhausted;
    private TemporalResultEnumerator _enum0, _enum1, _enum2, _enum3;
    private int _bindingCount0, _bindingCount1, _bindingCount2, _bindingCount3;

    // Bindings
    private Binding[] _bindings;
    private char[] _stringBuffer;
    private BindingTable _bindingTable;

    public SlotMultiPatternScan(QuadStore store, ReadOnlySpan<char> source,
        byte[] patternBuffer, int patternCount, ReadOnlySpan<char> graph = default)
    {
        _store = store;
        _source = source;
        _graph = graph;
        _patternBuffer = patternBuffer;
        _patternCount = patternCount;

        // Count triple patterns and record their indices
        _tripleCount = 0;
        _tripleIdx0 = _tripleIdx1 = _tripleIdx2 = _tripleIdx3 = -1;

        for (int i = 0; i < patternCount && _tripleCount < 4; i++)
        {
            var kind = (PatternKind)patternBuffer[i * PatternSlot.Size];
            if (kind == PatternKind.Triple)
            {
                switch (_tripleCount)
                {
                    case 0: _tripleIdx0 = i; break;
                    case 1: _tripleIdx1 = i; break;
                    case 2: _tripleIdx2 = i; break;
                    case 3: _tripleIdx3 = i; break;
                }
                _tripleCount++;
            }
        }

        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _exhausted = false;
        _enum0 = _enum1 = _enum2 = _enum3 = default;
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;

        _bindings = new Binding[16];
        _stringBuffer = new char[1024];
        _bindingTable = new BindingTable(_bindings, _stringBuffer);
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted || _tripleCount == 0)
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
                if (_currentLevel == _tripleCount - 1)
                {
                    // Found a complete match
                    return true;
                }
                else
                {
                    // Move to next level
                    _currentLevel++;
                }
            }
            else
            {
                // Backtrack
                DisposeLevel(_currentLevel);
                ResetLevel(_currentLevel);

                if (_currentLevel == 0)
                {
                    _exhausted = true;
                    return false;
                }

                _currentLevel--;
                bindings.TruncateTo(GetBindingCount(_currentLevel));
            }
        }
    }

    private bool TryAdvanceLevel0(ref BindingTable bindings)
    {
        if (!_init0)
        {
            _bindingCount0 = bindings.Count;
            ResolveAndQuery(_tripleIdx0, ref bindings, out _enum0);
            _init0 = true;
        }
        else
        {
            bindings.TruncateTo(_bindingCount0);
        }
        return TryAdvanceEnumerator(ref _enum0, _tripleIdx0, ref bindings);
    }

    private bool TryAdvanceLevel1(ref BindingTable bindings)
    {
        if (!_init1)
        {
            _bindingCount1 = bindings.Count;
            ResolveAndQuery(_tripleIdx1, ref bindings, out _enum1);
            _init1 = true;
        }
        else
        {
            bindings.TruncateTo(_bindingCount1);
        }
        return TryAdvanceEnumerator(ref _enum1, _tripleIdx1, ref bindings);
    }

    private bool TryAdvanceLevel2(ref BindingTable bindings)
    {
        if (!_init2)
        {
            _bindingCount2 = bindings.Count;
            ResolveAndQuery(_tripleIdx2, ref bindings, out _enum2);
            _init2 = true;
        }
        else
        {
            bindings.TruncateTo(_bindingCount2);
        }
        return TryAdvanceEnumerator(ref _enum2, _tripleIdx2, ref bindings);
    }

    private bool TryAdvanceLevel3(ref BindingTable bindings)
    {
        if (!_init3)
        {
            _bindingCount3 = bindings.Count;
            ResolveAndQuery(_tripleIdx3, ref bindings, out _enum3);
            _init3 = true;
        }
        else
        {
            bindings.TruncateTo(_bindingCount3);
        }
        return TryAdvanceEnumerator(ref _enum3, _tripleIdx3, ref bindings);
    }

    private void ResolveAndQuery(int slotIndex, scoped ref BindingTable bindings, out TemporalResultEnumerator enumerator)
    {
        var slot = GetSlot(slotIndex);
        ReadOnlySpan<char> subject, predicate, obj;

        // Resolve subject (handles synthetic terms from SPARQL-star expansion)
        subject = ResolveTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, ref bindings);

        // Resolve predicate
        predicate = ResolveTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, ref bindings);

        // Resolve object
        obj = ResolveTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, ref bindings);

        enumerator = _store.QueryCurrent(subject, predicate, obj, _graph);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTerm(TermType type, int start, int length, scoped ref BindingTable bindings)
    {
        // Handle synthetic terms (negative offsets from SPARQL-star expansion)
        if (SyntheticTermHelper.IsSynthetic(start))
        {
            if (type == TermType.Variable)
            {
                var varName = SyntheticTermHelper.GetSyntheticVarName(start);
                var idx = bindings.FindBinding(varName);
                return idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
            }
            return SyntheticTermHelper.GetSyntheticIri(start);
        }

        if (type != TermType.Variable)
            return _source.Slice(start, length);

        var name = _source.Slice(start, length);
        var index = bindings.FindBinding(name);
        return index >= 0 ? bindings.GetString(index) : ReadOnlySpan<char>.Empty;
    }

    private bool TryAdvanceEnumerator(ref TemporalResultEnumerator enumerator, int slotIndex, ref BindingTable bindings)
    {
        while (enumerator.MoveNext())
        {
            var triple = enumerator.Current;
            int savedCount = bindings.Count;

            var slot = GetSlot(slotIndex);
            if (TryBindTriple(slot, triple, ref bindings))
            {
                return true;
            }

            bindings.TruncateTo(savedCount);
        }
        return false;
    }

    private PatternSlot GetSlot(int index)
    {
        return new PatternSlot(_patternBuffer.AsSpan().Slice(index * PatternSlot.Size, PatternSlot.Size));
    }

    private bool TryBindTriple(PatternSlot slot, in ResolvedTemporalQuad triple, ref BindingTable bindings)
    {
        if (!TryBindVariable(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, triple.Subject, ref bindings))
            return false;
        if (!TryBindVariable(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, triple.Predicate, ref bindings))
            return false;
        if (!TryBindVariable(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, triple.Object, ref bindings))
            return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(TermType type, int start, int length,
        ReadOnlySpan<char> value, ref BindingTable bindings)
    {
        if (type != TermType.Variable)
            return true;

        // Handle synthetic variables (from SPARQL-star expansion)
        ReadOnlySpan<char> varName;
        if (SyntheticTermHelper.IsSynthetic(start))
            varName = SyntheticTermHelper.GetSyntheticVarName(start);
        else
            varName = _source.Slice(start, length);

        var existingIndex = bindings.FindBinding(varName);
        if (existingIndex >= 0)
        {
            return value.SequenceEqual(bindings.GetString(existingIndex));
        }

        bindings.Bind(varName, value);
        return true;
    }

    private int GetBindingCount(int level) => level switch
    {
        0 => _bindingCount0,
        1 => _bindingCount1,
        2 => _bindingCount2,
        3 => _bindingCount3,
        _ => 0
    };

    private void DisposeLevel(int level)
    {
        switch (level)
        {
            case 0: if (_init0) _enum0.Dispose(); break;
            case 1: if (_init1) _enum1.Dispose(); break;
            case 2: if (_init2) _enum2.Dispose(); break;
            case 3: if (_init3) _enum3.Dispose(); break;
        }
    }

    private void ResetLevel(int level)
    {
        switch (level)
        {
            case 0: _init0 = false; break;
            case 1: _init1 = false; break;
            case 2: _init2 = false; break;
            case 3: _init3 = false; break;
        }
    }

    public void Dispose()
    {
        if (_init0) _enum0.Dispose();
        if (_init1) _enum1.Dispose();
        if (_init2) _enum2.Dispose();
        if (_init3) _enum3.Dispose();
    }
}
