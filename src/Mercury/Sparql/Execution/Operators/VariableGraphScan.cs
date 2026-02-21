using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// Scans triple patterns across all named graphs, binding a graph variable.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// For queries like: SELECT ?g ?s ?p ?o WHERE { GRAPH ?g { ?s ?p ?o } }
/// Supports FROM NAMED restriction: only iterate specified named graphs.
/// </summary>
internal ref struct VariableGraphScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly GraphClause _graphClause;
    private NamedGraphEnumerator _graphEnum;
    private MultiPatternScan _currentScan;
    private GraphPattern _innerPattern;
    private ReadOnlySpan<char> _currentGraph;
    private readonly int _graphVarHash;
    private bool _initialized;
    private bool _exhausted;

    // FROM NAMED restriction: if non-null, only iterate these graphs
    private readonly string[]? _allowedNamedGraphs;
    private int _allowedGraphIndex;  // For iterating _allowedNamedGraphs directly

    public VariableGraphScan(QuadStore store, ReadOnlySpan<char> source, GraphClause graphClause,
        string[]? allowedNamedGraphs = null)
    {
        _store = store;
        _source = source;
        _graphClause = graphClause;
        _graphEnum = allowedNamedGraphs == null ? store.GetNamedGraphs() : default;
        _currentScan = default;
        _innerPattern = default;
        _currentGraph = default;
        _initialized = false;
        _exhausted = false;
        _allowedNamedGraphs = allowedNamedGraphs;
        _allowedGraphIndex = 0;

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

    private static int ComputeHash(ReadOnlySpan<char> s) => Fnv1a.Hash(s);

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            // Try to get next result from current graph's scan
            if (_initialized && _currentScan.MoveNext(ref bindings))
            {
                // Bind the graph variable
                var graphVarName = _source.Slice(_graphClause.Graph.Start, _graphClause.Graph.Length);
                bindings.Bind(graphVarName, _currentGraph);
                return true;
            }

            // Move to next graph
            if (!MoveToNextGraph())
            {
                _exhausted = true;
                return false;
            }

            // Initialize scan for new graph
            _currentScan = new MultiPatternScan(_store, _source, _innerPattern, false, _currentGraph);
            _initialized = true;
        }
    }

    private bool MoveToNextGraph()
    {
        if (_allowedNamedGraphs != null)
        {
            // Iterate through allowed graphs list
            if (_allowedGraphIndex >= _allowedNamedGraphs.Length)
                return false;
            _currentGraph = _allowedNamedGraphs[_allowedGraphIndex++].AsSpan();
            return true;
        }
        else
        {
            // Iterate all named graphs
            if (!_graphEnum.MoveNext())
                return false;
            _currentGraph = _graphEnum.Current;
            return true;
        }
    }

    public void Dispose()
    {
        _currentScan.Dispose();
    }
}
