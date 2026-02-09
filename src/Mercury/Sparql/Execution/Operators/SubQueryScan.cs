using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// Executes a subquery and yields only projected variable bindings.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// Handles variable scoping: only SELECT-ed variables are visible to outer query.
///
/// Design note: Results are materialized eagerly during Initialize() by delegating
/// to BoxedSubQueryExecutor, which isolates large scan operator stack usage.
/// </summary>
internal ref struct SubQueryScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly SubSelect _subSelect;
    private readonly PrefixMapping[]? _prefixes;
    private readonly string[]? _namedGraphs;  // FROM NAMED restriction from outer query
    // Materialized results - populated during Initialize() via BoxedSubQueryExecutor
    private List<MaterializedRow>? _materializedResults;
    private int _currentIndex;
    private bool _initialized;
    private bool _exhausted;

    public SubQueryScan(QuadStore store, ReadOnlySpan<char> source, SubSelect subSelect)
        : this(store, source, subSelect, null, null)
    {
    }

    public SubQueryScan(QuadStore store, ReadOnlySpan<char> source, SubSelect subSelect, PrefixMapping[]? prefixes)
        : this(store, source, subSelect, prefixes, null)
    {
    }

    public SubQueryScan(QuadStore store, ReadOnlySpan<char> source, SubSelect subSelect, PrefixMapping[]? prefixes, string[]? namedGraphs)
    {
        _store = store;
        _source = source;
        _subSelect = subSelect;
        _prefixes = prefixes;
        _namedGraphs = namedGraphs;
        _materializedResults = null;
        _currentIndex = 0;
        _initialized = false;
        _exhausted = false;
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

        if (_materializedResults == null || _currentIndex >= _materializedResults.Count)
        {
            _exhausted = true;
            return false;
        }

        // Clear outer bindings before projecting new values
        outerBindings.Clear();

        // Copy bindings from materialized row to outer bindings
        var row = _materializedResults[_currentIndex++];
        for (int i = 0; i < row.BindingCount; i++)
        {
            outerBindings.BindWithHash(row.GetHash(i), row.GetValue(i));
        }

        return true;
    }

    /// <summary>
    /// Materializes all subquery results during initialization.
    /// Delegates to BoxedSubQueryExecutor to isolate large scan operator stack usage.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void Initialize()
    {
        // Convert span to string for boxed executor (one allocation per subquery is acceptable)
        var sourceString = _source.ToString();

        // Use boxed executor to isolate scan operator stack usage
        // The class methods have fresh stack frames without accumulated ref struct overhead
        // Pass prefix mappings for expanding prefixed names in subquery patterns
        // Pass namedGraphs for FROM NAMED restriction (may include empty graphs)
        var executor = new BoxedSubQueryExecutor(_store, sourceString, _subSelect, _prefixes, _namedGraphs);
        _materializedResults = executor.Execute();
    }

    public void Dispose()
    {
        // No-op: scans are created and disposed in BoxedSubQueryExecutor
        // Results are stored in _materializedResults (managed List<T>)
    }
}
