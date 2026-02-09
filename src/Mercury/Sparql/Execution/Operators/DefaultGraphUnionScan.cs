using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// Scans triple patterns across multiple default graphs (FROM clauses), unioning results.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// This is a streaming operator that avoids materializing all results upfront.
/// </summary>
internal ref struct DefaultGraphUnionScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    // ADR-011: Pattern stored via reference to reduce stack size by ~4KB
    private readonly MultiPatternScan.BoxedPattern _boxedPattern;
    private readonly string[] _defaultGraphs;
    private int _currentGraphIndex;
    private TriplePatternScan _singleScan;
    private MultiPatternScan _multiScan;
    private bool _isMultiPattern;
    private bool _initialized;
    private bool _exhausted;

    public DefaultGraphUnionScan(QuadStore store, ReadOnlySpan<char> source, GraphPattern pattern, string[] defaultGraphs)
    {
        _store = store;
        _source = source;
        // ADR-011: Box pattern to reduce stack size
        _boxedPattern = new MultiPatternScan.BoxedPattern { Pattern = pattern };
        _defaultGraphs = defaultGraphs;
        _currentGraphIndex = 0;
        _singleScan = default;
        _multiScan = default;
        _isMultiPattern = pattern.RequiredPatternCount > 1;
        _initialized = false;
        _exhausted = false;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            // Try to get next result from current graph's scan
            if (_initialized)
            {
                bool hasNext = _isMultiPattern
                    ? _multiScan.MoveNext(ref bindings)
                    : _singleScan.MoveNext(ref bindings);

                if (hasNext)
                    return true;

                // Current graph exhausted, dispose and move to next
                if (_isMultiPattern)
                    _multiScan.Dispose();
                else
                    _singleScan.Dispose();
                _initialized = false;
            }

            // Move to next graph
            if (_currentGraphIndex >= _defaultGraphs.Length)
            {
                _exhausted = true;
                return false;
            }

            // Clear bindings before switching to new graph
            // Each graph should start fresh (FROM semantics = union of independent queries)
            bindings.Clear();

            // Initialize scan for new graph
            var graphIri = _defaultGraphs[_currentGraphIndex++].AsSpan();
            InitializeScan(graphIri, ref bindings);
            _initialized = true;
        }
    }

    private void InitializeScan(ReadOnlySpan<char> graphIri, ref BindingTable bindings)
    {
        ref readonly var pattern = ref _boxedPattern.Pattern;
        if (_isMultiPattern)
        {
            _multiScan = new MultiPatternScan(_store, _source, pattern, false, graphIri);
        }
        else
        {
            // Find the first required pattern
            int requiredIdx = 0;
            for (int i = 0; i < pattern.PatternCount; i++)
            {
                if (!pattern.IsOptional(i)) { requiredIdx = i; break; }
            }

            var tp = pattern.GetPattern(requiredIdx);
            _singleScan = new TriplePatternScan(_store, _source, tp, bindings, graphIri);
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            if (_isMultiPattern)
                _multiScan.Dispose();
            else
                _singleScan.Dispose();
        }
    }
}
