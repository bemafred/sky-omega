using System.Collections.Generic;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Joins subquery results with outer triple patterns using nested loop join.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// For queries like: SELECT * WHERE { ?s ?p ?o . { SELECT ?s WHERE { ... } } }
/// Subquery is the driving (outer) relation; outer patterns are filtered using bound variables.
/// </summary>
internal ref struct SubQueryJoinScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly GraphPattern _pattern;
    private readonly SubSelect _subSelect;
    private readonly PrefixMapping[]? _prefixes;

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

    // Store subquery bindings so we can restore them if caller clears bindings externally
    // This is needed because ExecuteSubQueryJoinCore calls bindingTable.Clear() after each result
    private int _subBindingsCount;
    private int[] _storedSubBindingHashes;
    private string[] _storedSubBindingValues;

    public SubQueryJoinScan(QuadStore store, ReadOnlySpan<char> source,
        GraphPattern pattern, SubSelect subSelect)
        : this(store, source, pattern, subSelect, null)
    {
    }

    public SubQueryJoinScan(QuadStore store, ReadOnlySpan<char> source,
        GraphPattern pattern, SubSelect subSelect, PrefixMapping[]? prefixes)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _subSelect = subSelect;
        _prefixes = prefixes;

        // Initialize subquery state with prefix mappings
        _subQueryScan = new SubQueryScan(store, source, subSelect, prefixes);
        _subBindingStorage = new Binding[16];
        _subStringBuffer = PooledBufferManager.Shared.Rent<char>(512).Array!;
        _subBindings = new BindingTable(_subBindingStorage, _subStringBuffer);

        // Build outer pattern from non-subquery patterns
        // Preserve optional flags so OPTIONAL patterns are handled correctly
        _outerPattern = new GraphPattern();
        for (int i = 0; i < pattern.PatternCount; i++)
        {
            if (pattern.IsOptional(i))
                _outerPattern.AddOptionalPattern(pattern.GetPattern(i));
            else
                _outerPattern.AddPattern(pattern.GetPattern(i));
        }
        _useMultiPattern = _outerPattern.PatternCount > 1;

        _outerScan = default;
        _singleOuterScan = default;
        _outerInitialized = false;
        _bindingCheckpoint = 0;

        // Initialize storage for subquery bindings (max 16 bindings)
        _subBindingsCount = 0;
        _storedSubBindingHashes = new int[16];
        _storedSubBindingValues = new string[16];
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
            QueryCancellation.ThrowIfCancellationRequested();

            // Try to get next result from current outer pattern scan
            if (_outerInitialized)
            {
                // BUGFIX: Restore subquery bindings if caller cleared bindings externally
                // This happens when ExecuteSubQueryJoinCore calls bindingTable.Clear() after each result
                // Without this, TruncateTo in MultiPatternScan fails because count > _count after Clear()
                if (bindings.Count < _subBindingsCount)
                {
                    RestoreSubQueryBindings(ref bindings);
                }

                bool hasOuter;
                if (_useMultiPattern)
                    hasOuter = _outerScan.MoveNext(ref bindings);
                else
                    hasOuter = _singleOuterScan.MoveNext(ref bindings);

                if (hasOuter)
                {
                    // Try to match OPTIONAL patterns (left outer join semantics)
                    TryMatchOptionalPatterns(ref bindings);

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
        // Store subquery bindings so we can restore them if caller clears bindings
        _subBindingsCount = _subBindings.Count;
        for (int i = 0; i < _subBindings.Count; i++)
        {
            var hash = _subBindings.GetVariableHash(i);
            var value = _subBindings.GetString(i);

            // Store for potential restoration
            _storedSubBindingHashes[i] = hash;
            _storedSubBindingValues[i] = value.ToString();

            // Copy to outer binding table
            bindings.BindWithHash(hash, value);
        }
        _subBindings.Clear();
    }

    private void RestoreSubQueryBindings(ref BindingTable bindings)
    {
        // Restore previously stored subquery bindings
        for (int i = 0; i < _subBindingsCount; i++)
        {
            bindings.BindWithHash(_storedSubBindingHashes[i], _storedSubBindingValues[i].AsSpan());
        }
    }

    private void InitializeOuterScan(ref BindingTable bindings)
    {
        if (_useMultiPattern)
        {
            // MultiPatternScan with initial bindings and prefix mappings
            // MultiPatternScan resolves variables from bindings in ResolveAndQuery
            _outerScan = new MultiPatternScan(_store, _source, _outerPattern,
                unionMode: false, graph: default, prefixes: _prefixes);
        }
        else if (_outerPattern.PatternCount == 1)
        {
            var tp = _outerPattern.GetPattern(0);
            // Pass prefix mappings to enable prefix expansion (e.g., ex:p → <http://...>)
            _singleOuterScan = new TriplePatternScan(_store, _source, tp, bindings, default,
                TemporalQueryMode.Current, default, default, default, _prefixes);
        }
    }

    /// <summary>
    /// Try to match OPTIONAL patterns (left outer join semantics).
    /// Called after required patterns have been matched.
    /// </summary>
    private void TryMatchOptionalPatterns(ref BindingTable bindings)
    {
        // Check each pattern to see if it's optional
        for (int i = 0; i < _outerPattern.PatternCount; i++)
        {
            if (!_outerPattern.IsOptional(i)) continue;

            var tp = _outerPattern.GetPattern(i);
            TryMatchSingleOptionalPattern(ref tp, ref bindings);
        }
    }

    /// <summary>
    /// Try to match a single optional pattern against the store.
    /// If it matches, bind any unbound variables.
    /// If it doesn't match, the row is still returned (left outer join).
    /// Uses TriplePatternScan for proper prefix expansion and variable resolution.
    /// </summary>
    private void TryMatchSingleOptionalPattern(ref TriplePattern tp, ref BindingTable bindings)
    {
        // Use TriplePatternScan which handles:
        // - Prefix expansion (foaf:mbox → <http://xmlns.com/foaf/0.1/mbox>)
        // - Variable binding resolution
        // - Proper term resolution
        var scan = new TriplePatternScan(_store, _source, tp, bindings, default,
            TemporalQueryMode.Current, default, default, default, _prefixes);

        try
        {
            // Try to match the pattern
            if (scan.MoveNext(ref bindings))
            {
                // Pattern matched - bindings are already extended by MoveNext
                // Nothing more to do
            }
            // If no match, variables remain unbound (left outer join semantics)
        }
        finally
        {
            scan.Dispose();
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
        if (_subStringBuffer != null)
            PooledBufferManager.Shared.Return(_subStringBuffer);
    }
}
