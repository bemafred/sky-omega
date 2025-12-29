using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// SPARQL query executor using specialized operators.
///
/// Execution model:
/// 1. Parse query → Query struct with triple patterns + filters
/// 2. Build execution plan → Stack of operators
/// 3. Execute → Pull-based iteration through operator pipeline
///
/// Dataset clauses (FROM/FROM NAMED):
/// - FROM clauses define default graph(s) - patterns without GRAPH query these
/// - FROM NAMED clauses define named graphs available to GRAPH patterns
/// - Without dataset clauses, default graph = atom 0, all named graphs available
///
/// Note: This is a class (not ref struct) to enable heap-based execution context,
/// which reduces stack pressure for complex queries. The string source is copied
/// once at construction time.
///
/// Implements IDisposable to clean up the internal QueryBuffer (pooled storage).
/// </summary>
public class QueryExecutor : IDisposable
{
    private readonly QuadStore _store;
    private readonly string _source;
    private readonly Query _query;
    private readonly QueryBuffer _buffer;  // New: heap-allocated pattern storage
    private bool _disposed;

    // Dataset context: default graph IRIs (FROM) and named graph IRIs (FROM NAMED)
    private readonly string[]? _defaultGraphs;
    private readonly string[]? _namedGraphs;

    // SERVICE clause execution
    private readonly ISparqlServiceExecutor? _serviceExecutor;

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, Query query)
        : this(store, source, query, null) { }

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, Query query,
        ISparqlServiceExecutor? serviceExecutor)
    {
        _store = store;
        _source = source.ToString();  // Copy to heap - enables class-based execution
        _query = query;

        // Convert Query to QueryBuffer for heap-based pattern storage
        // This avoids stack overflow when accessing patterns in nested calls
        _buffer = QueryBufferAdapter.FromQuery(in query, source);

        _defaultGraphs = null;
        _namedGraphs = null;

        // Extract dataset clauses into arrays
        if (query.Datasets != null && query.Datasets.Length > 0)
        {
            var defaultList = new System.Collections.Generic.List<string>();
            var namedList = new System.Collections.Generic.List<string>();

            foreach (var ds in query.Datasets)
            {
                var iri = source.Slice(ds.GraphIri.Start, ds.GraphIri.Length).ToString();
                if (ds.IsNamed)
                    namedList.Add(iri);
                else
                    defaultList.Add(iri);
            }

            if (defaultList.Count > 0) _defaultGraphs = defaultList.ToArray();
            if (namedList.Count > 0) _namedGraphs = namedList.ToArray();
        }

        _serviceExecutor = serviceExecutor;
    }

    /// <summary>
    /// Alternative constructor that takes a pre-allocated QueryBuffer directly.
    /// The caller transfers ownership of the buffer to the executor.
    /// </summary>
    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, QueryBuffer buffer)
        : this(store, source, buffer, null) { }

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, QueryBuffer buffer,
        ISparqlServiceExecutor? serviceExecutor)
    {
        _store = store;
        _source = source.ToString();
        _buffer = buffer;
        _query = default;  // Not used when buffer is provided directly
        _serviceExecutor = serviceExecutor;

        // Extract datasets from buffer
        if (buffer.Datasets != null && buffer.Datasets.Length > 0)
        {
            var defaultList = new System.Collections.Generic.List<string>();
            var namedList = new System.Collections.Generic.List<string>();

            foreach (var ds in buffer.Datasets)
            {
                var iri = source.Slice(ds.GraphIri.Start, ds.GraphIri.Length).ToString();
                if (ds.IsNamed)
                    namedList.Add(iri);
                else
                    defaultList.Add(iri);
            }

            if (defaultList.Count > 0) _defaultGraphs = defaultList.ToArray();
            if (namedList.Count > 0) _namedGraphs = namedList.ToArray();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer?.Dispose();
    }

    /// <summary>
    /// Execute a parsed query and return results.
    /// Caller must hold read lock on store and call Dispose on results.
    /// Note: Avoid extracting large structs as locals here to prevent stack overflow.
    /// Access _query fields directly when possible.
    /// </summary>
    public QueryResults Execute()
    {
        // Check for GRAPH clauses first - uses separate method to avoid stack overflow
        // ExecuteGraphClauses accesses _query directly
        if (_query.WhereClause.Pattern.HasGraph && _query.WhereClause.Pattern.PatternCount == 0)
        {
            return ExecuteGraphClauses();
        }

        // Check for subqueries
        if (_query.WhereClause.Pattern.HasSubQueries)
        {
            return ExecuteWithSubQueries();
        }

        // Check for SERVICE clauses
        if (_query.WhereClause.Pattern.HasService)
        {
            return ExecuteWithService();
        }

        if (_query.WhereClause.Pattern.PatternCount == 0)
            return QueryResults.Empty();

        // Check for FROM clauses (default graph dataset)
        if (_defaultGraphs != null && _defaultGraphs.Length > 0)
        {
            return ExecuteWithDefaultGraphs();
        }

        // For regular queries, access _query fields directly to build result
        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Access pattern directly from _query
        ref readonly var pattern = ref _query.WhereClause.Pattern;
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

            return new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer,
                _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, (_query.SelectClause.Distinct || _query.SelectClause.Reduced),
                _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
                _query.SolutionModifier.Having);
        }

        // No required patterns but have optional - need special handling
        if (requiredCount == 0)
        {
            // All patterns are optional - start with empty bindings and try to match optionals
            // For now, just return empty (proper implementation would need different semantics)
            return QueryResults.Empty();
        }

        // Multiple required patterns - need join
        return ExecuteWithJoins();
    }

    /// <summary>
    /// Execute query against specified default graphs (FROM clauses).
    /// For single FROM: query that graph directly.
    /// For multiple FROM: use DefaultGraphUnionScan for streaming results.
    /// </summary>
    private QueryResults ExecuteWithDefaultGraphs()
    {
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var requiredCount = pattern.RequiredPatternCount;

        // Single FROM clause - query that specific graph
        if (_defaultGraphs!.Length == 1)
        {
            var graphIri = _defaultGraphs[0].AsSpan();

            if (requiredCount == 1)
            {
                int requiredIdx = 0;
                for (int i = 0; i < pattern.PatternCount; i++)
                {
                    if (!pattern.IsOptional(i)) { requiredIdx = i; break; }
                }

                var tp = pattern.GetPattern(requiredIdx);
                var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graphIri);

                return new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer,
                    _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, (_query.SelectClause.Distinct || _query.SelectClause.Reduced),
                    _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
                    _query.SolutionModifier.Having);
            }

            if (requiredCount == 0)
                return QueryResults.Empty();

            // Multiple patterns - use MultiPatternScan with graph
            return new QueryResults(
                new MultiPatternScan(_store, _source, pattern, false, graphIri),
                _buffer, _source, _store, bindings, stringBuffer,
                _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, (_query.SelectClause.Distinct || _query.SelectClause.Reduced),
                _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
                _query.SolutionModifier.Having);
        }

        // Multiple FROM clauses - use CrossGraphMultiPatternScan for cross-graph joins
        // This allows joins where pattern1 matches in graph1 and pattern2 matches in graph2
        var crossGraphScan = new CrossGraphMultiPatternScan(_store, _source, pattern, _defaultGraphs);
        return new QueryResults(crossGraphScan, _buffer, _source, _store, bindings, stringBuffer,
            _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, (_query.SelectClause.Distinct || _query.SelectClause.Reduced),
            _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
            _query.SolutionModifier.Having);
    }

    /// <summary>
    /// Execute an ASK query and return true if any result exists.
    /// Caller must hold read lock on store.
    /// </summary>
    public bool ExecuteAsk()
    {
        var pattern = _query.WhereClause.Pattern;

        if (pattern.PatternCount == 0)
            return false;

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        var requiredCount = pattern.RequiredPatternCount;

        // Single required pattern - just scan
        if (requiredCount == 1)
        {
            int requiredIdx = 0;
            for (int i = 0; i < pattern.PatternCount; i++)
            {
                if (!pattern.IsOptional(i)) { requiredIdx = i; break; }
            }

            var tp = pattern.GetPattern(requiredIdx);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable);

            // For ASK, we just need to know if any result exists
            // No need for LIMIT/OFFSET/DISTINCT/ORDER BY
            var results = new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer);
            try
            {
                return results.MoveNext();
            }
            finally
            {
                results.Dispose();
            }
        }

        // No required patterns
        if (requiredCount == 0)
        {
            return false;
        }

        // Multiple required patterns - need join
        var multiScan = new MultiPatternScan(_store, _source, pattern);
        var multiResults = new QueryResults(multiScan, _buffer, _source, _store, bindings, stringBuffer);
        try
        {
            return multiResults.MoveNext();
        }
        finally
        {
            multiResults.Dispose();
        }
    }

    /// <summary>
    /// Execute a CONSTRUCT query and return constructed triples.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public ConstructResults ExecuteConstruct()
    {
        var pattern = _query.WhereClause.Pattern;
        var template = _query.ConstructTemplate;

        if (pattern.PatternCount == 0 || !template.HasPatterns)
            return ConstructResults.Empty();

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        var requiredCount = pattern.RequiredPatternCount;

        // Single required pattern - just scan
        if (requiredCount == 1)
        {
            int requiredIdx = 0;
            for (int i = 0; i < pattern.PatternCount; i++)
            {
                if (!pattern.IsOptional(i)) { requiredIdx = i; break; }
            }

            var tp = pattern.GetPattern(requiredIdx);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable);
            var queryResults = new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer);

            return new ConstructResults(queryResults, template, _source, bindings, stringBuffer);
        }

        // No required patterns
        if (requiredCount == 0)
        {
            return ConstructResults.Empty();
        }

        // Multiple required patterns - need join
        var multiScan = new MultiPatternScan(_store, _source, pattern);
        var multiResults = new QueryResults(multiScan, _buffer, _source, _store, bindings, stringBuffer);

        return new ConstructResults(multiResults, template, _source, bindings, stringBuffer);
    }

    /// <summary>
    /// Execute a DESCRIBE query and return triples describing the matched resources.
    /// Returns all triples where described resources appear as subject or object.
    /// Caller must hold read lock on store.
    /// </summary>
    public DescribeResults ExecuteDescribe()
    {
        var pattern = _query.WhereClause.Pattern;
        var describeAll = _query.DescribeAll;

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // If no WHERE clause, return empty
        if (pattern.PatternCount == 0)
            return DescribeResults.Empty();

        var requiredCount = pattern.RequiredPatternCount;

        // Execute WHERE clause to get resources to describe
        QueryResults queryResults;
        if (requiredCount == 1)
        {
            int requiredIdx = 0;
            for (int i = 0; i < pattern.PatternCount; i++)
            {
                if (!pattern.IsOptional(i)) { requiredIdx = i; break; }
            }

            var tp = pattern.GetPattern(requiredIdx);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable);
            queryResults = new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer);
        }
        else if (requiredCount == 0)
        {
            return DescribeResults.Empty();
        }
        else
        {
            var multiScan = new MultiPatternScan(_store, _source, pattern);
            queryResults = new QueryResults(multiScan, _buffer, _source, _store, bindings, stringBuffer);
        }

        return new DescribeResults(_store, queryResults, bindings, stringBuffer, describeAll);
    }

    private QueryResults ExecuteWithJoins()
    {
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];

        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _query.WhereClause.Pattern;

        // Use nested loop join for required patterns only
        return new QueryResults(
            new MultiPatternScan(_store, _source, pattern),
            _buffer,
            _source,
            _store,
            bindings,
            stringBuffer,
            _query.SolutionModifier.Limit,
            _query.SolutionModifier.Offset,
            (_query.SelectClause.Distinct || _query.SelectClause.Reduced),
            _query.SolutionModifier.OrderBy,
            _query.SolutionModifier.GroupBy,
            _query.SelectClause,
            _query.SolutionModifier.Having);
    }

    /// <summary>
    /// Execute a query with SERVICE clauses (federated queries).
    /// For queries like: SELECT * WHERE { SERVICE &lt;endpoint&gt; { ?s ?p ?o } }
    /// Also supports: SERVICE SILENT &lt;endpoint&gt; { ... }
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteWithService()
    {
        if (_serviceExecutor == null)
        {
            throw new InvalidOperationException(
                "SERVICE clause requires an ISparqlServiceExecutor. " +
                "Use the QueryExecutor constructor that accepts ISparqlServiceExecutor.");
        }

        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var serviceCount = pattern.ServiceClauseCount;

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // For now, only handle the simple case: SERVICE clause only, no local patterns
        if (pattern.PatternCount == 0 && serviceCount == 1)
        {
            // Execute single SERVICE clause
            var serviceClause = pattern.GetServiceClause(0);
            var serviceScan = new ServiceScan(_serviceExecutor, _source, serviceClause, bindingTable);

            // Materialize results and return
            var results = new List<MaterializedRow>();
            while (serviceScan.MoveNext(ref bindingTable))
            {
                results.Add(new MaterializedRow(bindingTable));
            }
            serviceScan.Dispose();

            if (results.Count == 0)
                return QueryResults.Empty();

            return QueryResults.FromMaterializedList(results, bindings, stringBuffer,
                _query.SolutionModifier.Limit, _query.SolutionModifier.Offset,
                (_query.SelectClause.Distinct || _query.SelectClause.Reduced));
        }

        // TODO: Handle SERVICE with local patterns (requires join)
        // TODO: Handle multiple SERVICE clauses
        // For now, return empty for unsupported cases
        return QueryResults.Empty();
    }

    /// <summary>
    /// Execute a query with only GRAPH clauses (no default graph patterns).
    /// For queries like: SELECT * WHERE { GRAPH &lt;g&gt; { ?s ?p ?o } }
    /// NOTE: This method is carefully structured to minimize stack usage.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteGraphClauses()
    {
        var graphCount = _query.WhereClause.Pattern.GraphClauseCount;

        // Multiple GRAPH clauses - join results
        if (graphCount > 1)
            return ExecuteMultipleGraphClauses();

        // Check if it's a variable graph - use pattern.GetGraphClause(0).IsVariable directly
        // to avoid creating a local copy of GraphClause
        if (_query.WhereClause.Pattern.GetGraphClause(0).IsVariable)
        {
            if (_query.WhereClause.Pattern.GetGraphClause(0).PatternCount == 0)
                return QueryResults.Empty();

            // Variable graph execution returns materialized results
            return ExecuteVariableGraph();
        }

        // For fixed IRI graph - proceed with smaller stack usage
        return ExecuteFixedGraphClause();
    }

    /// <summary>
    /// Execute multiple GRAPH clauses and join their results.
    /// Returns materialized rows directly since QueryResults is too large for normal stack.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteMultipleGraphClauses()
    {
        // Collect results from all GRAPH clauses and join them
        var joinedResults = CollectAndJoinGraphResults();
        if (joinedResults == null || joinedResults.Count == 0)
            return QueryResults.Empty();

        // Use the same approach as ExecuteVariableGraph - create QueryResults from materialized list
        // Note: This works because FromMaterializedList has NoInlining and creates a smaller QueryResults
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        return QueryResults.FromMaterializedList(joinedResults, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Collect results from all GRAPH clauses and join them.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? CollectAndJoinGraphResults()
    {
        var graphCount = _query.WhereClause.Pattern.GraphClauseCount;

        // Execute first GRAPH clause
        var firstResults = ExecuteSingleGraphClauseForJoin(0);
        if (firstResults == null || firstResults.Count == 0)
            return null;

        // Join with subsequent GRAPH clauses
        var joinedResults = firstResults;
        for (int i = 1; i < graphCount; i++)
        {
            var nextResults = ExecuteSingleGraphClauseForJoin(i);
            if (nextResults == null || nextResults.Count == 0)
                return null;

            joinedResults = JoinMaterializedRows(joinedResults, nextResults);
            if (joinedResults.Count == 0)
                return null;
        }

        return joinedResults;
    }

    /// <summary>
    /// Execute a single GRAPH clause and return materialized rows for joining.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteSingleGraphClauseForJoin(int graphIndex)
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var graphClause = pattern.GetGraphClause(graphIndex);

        if (graphClause.PatternCount == 0)
            return null;

        if (graphClause.IsVariable)
        {
            // Variable graph - use VariableGraphExecutor
            return ExecuteVariableGraphClauseForJoin(graphIndex);
        }
        else
        {
            // Fixed IRI graph - execute patterns directly
            return ExecuteFixedGraphClauseForJoin(graphIndex);
        }
    }

    /// <summary>
    /// Execute a variable graph clause for joining (GRAPH ?g { ... }).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteVariableGraphClauseForJoin(int graphIndex)
    {
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];

        // Find the nth GRAPH header slot in the buffer
        var patterns = _buffer.GetPatterns();
        int graphHeaderIdx = -1;
        int foundCount = 0;
        for (int i = 0; i < _buffer.PatternCount; i++)
        {
            if (patterns[i].Kind == Patterns.PatternKind.GraphHeader)
            {
                if (foundCount == graphIndex)
                {
                    graphHeaderIdx = i;
                    break;
                }
                foundCount++;
            }
        }

        if (graphHeaderIdx < 0)
            return null;

        var graphHeader = patterns[graphHeaderIdx];

        var config = new VariableGraphExecutor.BufferExecutionConfig
        {
            Store = _store,
            Source = _source,
            Buffer = _buffer,
            NamedGraphs = _namedGraphs,
            Bindings = bindings,
            StringBuffer = stringBuffer,
            GraphTermType = graphHeader.GraphTermType,
            GraphTermStart = graphHeader.GraphTermStart,
            GraphTermLength = graphHeader.GraphTermLength,
            GraphHeaderIndex = graphHeaderIdx
        };

        return VariableGraphExecutor.ExecuteFromBuffer(config);
    }

    /// <summary>
    /// Execute a fixed IRI graph clause for joining (GRAPH <iri> { ... }).
    /// Dispatches to specialized methods to avoid large structs on same stack frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteFixedGraphClauseForJoin(int graphIndex)
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var graphClause = pattern.GetGraphClause(graphIndex);
        var patternCount = graphClause.PatternCount;

        if (patternCount == 0)
            return null;

        if (patternCount == 1)
            return ExecuteFixedGraphClauseSinglePattern(graphIndex);
        else
            return ExecuteFixedGraphClauseMultiPattern(graphIndex);
    }

    /// <summary>
    /// Execute a fixed GRAPH clause with a single pattern.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteFixedGraphClauseSinglePattern(int graphIndex)
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var graphClause = pattern.GetGraphClause(graphIndex);

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        var graphIri = _source.AsSpan(graphClause.Graph.Start, graphClause.Graph.Length);
        var tp = graphClause.GetPattern(0);
        var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graphIri);

        var results = new List<MaterializedRow>();
        while (scan.MoveNext(ref bindingTable))
        {
            results.Add(new MaterializedRow(bindingTable));
            bindingTable.Clear();
        }
        scan.Dispose();
        return results;
    }

    /// <summary>
    /// Execute a fixed GRAPH clause with multiple patterns.
    /// Uses a separate thread with larger stack to handle the large MultiPatternScan struct.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteFixedGraphClauseMultiPattern(int graphIndex)
    {
        List<MaterializedRow>? results = null;
        var store = _store;
        var source = _source;
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var graphClause = pattern.GetGraphClause(graphIndex);

        // Run on thread with larger stack to handle large GraphPattern/MultiPatternScan structs
        var thread = new System.Threading.Thread(() =>
        {
            var bindings = new Binding[16];
            var stringBuffer = new char[1024];
            var bindingTable = new BindingTable(bindings, stringBuffer);

            var graphIri = source.AsSpan(graphClause.Graph.Start, graphClause.Graph.Length);

            var graphPattern = new GraphPattern();
            for (int i = 0; i < graphClause.PatternCount; i++)
            {
                graphPattern.AddPattern(graphClause.GetPattern(i));
            }

            var scan = new MultiPatternScan(store, source, graphPattern, false, graphIri);
            results = new List<MaterializedRow>();
            while (scan.MoveNext(ref bindingTable))
            {
                results.Add(new MaterializedRow(bindingTable));
                bindingTable.Clear();
            }
            scan.Dispose();
        }, 4 * 1024 * 1024); // 4MB stack

        thread.Start();
        thread.Join();

        return results ?? new List<MaterializedRow>();
    }

    /// <summary>
    /// Join two lists of materialized rows based on shared variable bindings.
    /// Uses nested loop join - for each row in left, find matching rows in right.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<MaterializedRow> JoinMaterializedRows(List<MaterializedRow> left, List<MaterializedRow> right)
    {
        var results = new List<MaterializedRow>();

        foreach (var leftRow in left)
        {
            foreach (var rightRow in right)
            {
                // Check if rows can be joined (shared variables must have same value)
                if (CanJoinRows(leftRow, rightRow))
                {
                    // Merge the rows
                    var merged = MergeRows(leftRow, rightRow);
                    results.Add(merged);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Check if two rows can be joined - shared variables must have same values.
    /// </summary>
    private static bool CanJoinRows(MaterializedRow left, MaterializedRow right)
    {
        // For each binding in left, if it also exists in right, values must match
        for (int i = 0; i < left.BindingCount; i++)
        {
            var leftHash = left.GetHash(i);
            var leftValue = left.GetValue(i);

            // Check if right has this variable
            for (int j = 0; j < right.BindingCount; j++)
            {
                if (right.GetHash(j) == leftHash)
                {
                    // Found same variable - values must match
                    if (!leftValue.SequenceEqual(right.GetValue(j)))
                        return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Merge two rows into one, combining all bindings.
    /// </summary>
    private static MaterializedRow MergeRows(MaterializedRow left, MaterializedRow right)
    {
        // Create a binding table with all bindings from both rows
        var bindings = new Binding[32];
        var stringBuffer = new char[2048];
        var table = new BindingTable(bindings, stringBuffer);

        // Add all bindings from left
        for (int i = 0; i < left.BindingCount; i++)
        {
            table.BindWithHash(left.GetHash(i), left.GetValue(i));
        }

        // Add bindings from right that aren't already present
        for (int i = 0; i < right.BindingCount; i++)
        {
            var hash = right.GetHash(i);
            if (table.FindBindingByHash(hash) < 0)
            {
                table.BindWithHash(hash, right.GetValue(i));
            }
        }

        return new MaterializedRow(table);
    }

    /// <summary>
    /// Execute variable graph query.
    /// Now that QueryResults uses buffer-based patterns (~100 bytes) instead of inline GraphPattern (~4KB),
    /// no thread workaround is needed.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteVariableGraph()
    {
        // Execute directly - no thread workaround needed with buffer-based patterns
        var results = ExecuteVariableGraphCore(_store, _source, _buffer, _namedGraphs);

        if (results == null || results.Count == 0)
            return QueryResults.Empty();

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        return QueryResults.FromMaterializedList(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Core variable graph execution.
    /// </summary>
    private static List<MaterializedRow>? ExecuteVariableGraphCore(
        QuadStore store, string source, Patterns.QueryBuffer buffer, string[]? namedGraphs)
    {
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];

        // Find the GRAPH header slot in the buffer
        var patterns = buffer.GetPatterns();
        int graphHeaderIdx = -1;
        for (int i = 0; i < buffer.PatternCount; i++)
        {
            if (patterns[i].Kind == Patterns.PatternKind.GraphHeader)
            {
                graphHeaderIdx = i;
                break;
            }
        }

        if (graphHeaderIdx < 0)
            return null;

        var graphHeader = patterns[graphHeaderIdx];

        var config = new VariableGraphExecutor.BufferExecutionConfig
        {
            Store = store,
            Source = source,
            Buffer = buffer,
            NamedGraphs = namedGraphs,
            Bindings = bindings,
            StringBuffer = stringBuffer,
            GraphTermType = graphHeader.GraphTermType,
            GraphTermStart = graphHeader.GraphTermStart,
            GraphTermLength = graphHeader.GraphTermLength,
            GraphHeaderIndex = graphHeaderIdx
        };

        return VariableGraphExecutor.ExecuteFromBuffer(config);
    }

    // Helper to create OrderByClause from buffer's OrderByEntry array
    private static OrderByClause CreateOrderByClause(Patterns.OrderByEntry[] entries)
    {
        var clause = new OrderByClause();
        foreach (var entry in entries)
        {
            clause.AddCondition(entry.VariableStart, entry.VariableLength,
                entry.Descending ? OrderDirection.Descending : OrderDirection.Ascending);
        }
        return clause;
    }

    // Helper to create GroupByClause from buffer's GroupByEntry array
    private static GroupByClause CreateGroupByClause(Patterns.GroupByEntry[] entries)
    {
        var clause = new GroupByClause();
        foreach (var entry in entries)
        {
            clause.AddVariable(entry.VariableStart, entry.VariableLength);
        }
        return clause;
    }

    // Helper to create minimal SelectClause from buffer
    private SelectClause CreateSelectClause()
    {
        return new SelectClause
        {
            Distinct = _buffer.SelectDistinct,
            SelectAll = _buffer.SelectAll
        };
    }

    /// <summary>
    /// Helper method for fixed IRI graph execution.
    /// Isolated to separate stack frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteFixedGraphClause()
    {
        // Now we can create local copies since we're in a separate frame
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var graphClause = pattern.GetGraphClause(0);

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Get the graph IRI
        var graphIri = _source.AsSpan(graphClause.Graph.Start, graphClause.Graph.Length);

        var patternCount = graphClause.PatternCount;
        if (patternCount == 0)
            return QueryResults.Empty();

        // Extract modifiers from _query
        var limit = _query.SolutionModifier.Limit;
        var offset = _query.SolutionModifier.Offset;
        var distinct = (_query.SelectClause.Distinct || _query.SelectClause.Reduced);
        var orderBy = _query.SolutionModifier.OrderBy;
        var groupBy = _query.SolutionModifier.GroupBy;
        var selectClause = _query.SelectClause;
        var having = _query.SolutionModifier.Having;

        // Single pattern in GRAPH clause
        if (patternCount == 1)
        {
            var tp = graphClause.GetPattern(0);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graphIri);

            return new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer,
                limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        // Multiple patterns in GRAPH clause - need join with graph constraint
        // Create a temporary GraphPattern from the GraphClause patterns
        var graphPattern = new GraphPattern();
        for (int i = 0; i < patternCount; i++)
        {
            graphPattern.AddPattern(graphClause.GetPattern(i));
        }

        return new QueryResults(
            new MultiPatternScan(_store, _source, graphPattern, false, graphIri),
            _buffer,
            _source,
            _store,
            bindings,
            stringBuffer,
            limit,
            offset,
            distinct,
            orderBy,
            groupBy,
            selectClause,
            having);
    }

    /// <summary>
    /// Execute a query that contains subqueries.
    /// For queries like: SELECT * WHERE { ?s ?p ?o . { SELECT ?s WHERE { ... } } }
    /// NOTE: NoInlining prevents stack overflow from QueryResults struct size.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteWithSubQueries()
    {
        // Check for multiple subqueries FIRST with minimal stack usage
        // Accessing SubQueryCount directly avoids copying large pattern struct
        if (_query.WhereClause.Pattern.SubQueryCount > 1)
            return ExecuteMultipleSubQueries();

        // Single subquery with outer patterns - delegate to join method directly
        // to minimize call chain depth
        if (_query.WhereClause.Pattern.PatternCount > 0)
            return ExecuteSingleSubQueryWithJoin();

        // Single subquery without outer patterns
        return ExecuteSingleSubQuerySimple();
    }

    /// <summary>
    /// Execute a single subquery with no outer patterns (simple case).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteSingleSubQuerySimple()
    {
        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var subSelect = pattern.GetSubQuery(0);

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];

        // Create SubQueryScan operator
        var subQueryScan = new SubQueryScan(_store, _source, subSelect);

        return new QueryResults(subQueryScan, _buffer, _source, _store, bindings, stringBuffer,
            _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, (_query.SelectClause.Distinct || _query.SelectClause.Reduced),
            _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
            _query.SolutionModifier.Having);
    }

    /// <summary>
    /// Execute a single subquery with outer patterns (join case).
    /// Minimal stack usage - collects results eagerly.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteSingleSubQueryWithJoin()
    {
        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var subSelect = pattern.GetSubQuery(0);

        // Execute join and materialize results
        var results = ExecuteSubQueryJoinCore(subSelect);
        if (results == null || results.Count == 0)
            return QueryResults.Empty();

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        return QueryResults.FromMaterializedList(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Execute multiple subqueries and join their results.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteMultipleSubQueries()
    {
        // Collect results from all subqueries and join them
        var joinedResults = CollectAndJoinSubQueryResults();
        if (joinedResults == null || joinedResults.Count == 0)
            return QueryResults.Empty();

        // If there are outer triple patterns, join with them too
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        if (pattern.RequiredPatternCount > 0)
        {
            joinedResults = JoinWithOuterPatterns(joinedResults);
            if (joinedResults == null || joinedResults.Count == 0)
                return QueryResults.Empty();
        }

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        return QueryResults.FromMaterializedList(joinedResults, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Collect results from all subqueries and join them.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? CollectAndJoinSubQueryResults()
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var subQueryCount = pattern.SubQueryCount;

        // Execute first subquery
        var firstResults = ExecuteSingleSubQueryForJoin(0);
        if (firstResults == null || firstResults.Count == 0)
            return null;

        // Join with subsequent subqueries
        var joinedResults = firstResults;
        for (int i = 1; i < subQueryCount; i++)
        {
            var nextResults = ExecuteSingleSubQueryForJoin(i);
            if (nextResults == null || nextResults.Count == 0)
                return null;

            joinedResults = JoinMaterializedRows(joinedResults, nextResults);
            if (joinedResults.Count == 0)
                return null;
        }

        return joinedResults;
    }

    /// <summary>
    /// Execute a single subquery and return materialized rows for joining.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteSingleSubQueryForJoin(int subQueryIndex)
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var subSelect = pattern.GetSubQuery(subQueryIndex);

        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        var results = new List<MaterializedRow>();
        var subQueryScan = new SubQueryScan(_store, _source, subSelect);

        while (subQueryScan.MoveNext(ref bindingTable))
        {
            results.Add(new MaterializedRow(bindingTable));
            bindingTable.Clear();
        }
        subQueryScan.Dispose();

        return results;
    }

    /// <summary>
    /// Join materialized subquery results with outer triple patterns.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? JoinWithOuterPatterns(List<MaterializedRow> subQueryResults)
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var results = new List<MaterializedRow>();
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Create pattern scan for outer patterns
        if (pattern.RequiredPatternCount == 1)
        {
            var tp = pattern.GetPattern(0);
            foreach (var subRow in subQueryResults)
            {
                // Load subquery bindings
                bindingTable.Clear();
                for (int i = 0; i < subRow.BindingCount; i++)
                {
                    bindingTable.BindWithHash(subRow.GetHash(i), subRow.GetValue(i));
                }

                // Query with bound values
                var subject = ResolveTermFromBindings(tp.Subject, bindingTable);
                var predicate = ResolveTermFromBindings(tp.Predicate, bindingTable);
                var obj = ResolveTermFromBindings(tp.Object, bindingTable);

                var enumerator = _store.QueryCurrent(subject, predicate, obj);
                try
                {
                    while (enumerator.MoveNext())
                    {
                        var triple = enumerator.Current;
                        var rowBindings = new Binding[16];
                        var rowStringBuffer = new char[1024];
                        var rowTable = new BindingTable(rowBindings, rowStringBuffer);

                        // Copy subquery bindings
                        for (int i = 0; i < subRow.BindingCount; i++)
                        {
                            rowTable.BindWithHash(subRow.GetHash(i), subRow.GetValue(i));
                        }

                        // Bind new variables from triple
                        if (TryBindTermFromTriple(tp.Subject, triple.Subject, ref rowTable) &&
                            TryBindTermFromTriple(tp.Predicate, triple.Predicate, ref rowTable) &&
                            TryBindTermFromTriple(tp.Object, triple.Object, ref rowTable))
                        {
                            results.Add(new MaterializedRow(rowTable));
                        }
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
        }
        else
        {
            // Multiple outer patterns - use MultiPatternScan per subquery result
            foreach (var subRow in subQueryResults)
            {
                // Load subquery bindings
                bindingTable.Clear();
                for (int i = 0; i < subRow.BindingCount; i++)
                {
                    bindingTable.BindWithHash(subRow.GetHash(i), subRow.GetValue(i));
                }

                // Create a temporary pattern with pre-bound variables
                var outerPattern = new GraphPattern();
                for (int i = 0; i < pattern.RequiredPatternCount; i++)
                {
                    if (!pattern.IsOptional(i))
                    {
                        outerPattern.AddPattern(pattern.GetPattern(i));
                    }
                }

                var scan = new MultiPatternScan(_store, _source, outerPattern, false);
                // Reset binding table but keep subquery bindings
                var savedCount = bindingTable.Count;

                while (scan.MoveNext(ref bindingTable))
                {
                    results.Add(new MaterializedRow(bindingTable));
                    bindingTable.TruncateTo(savedCount);
                }
                scan.Dispose();
            }
        }

        return results;
    }

    /// <summary>
    /// Resolve a term using current bindings.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermFromBindings(Term term, BindingTable bindings)
    {
        if (!term.IsVariable)
            return _source.AsSpan(term.Start, term.Length);

        var varName = _source.AsSpan(term.Start, term.Length);
        var idx = bindings.FindBinding(varName);
        return idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// Try to bind a term from a triple value.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool TryBindTermFromTriple(Term term, ReadOnlySpan<char> value, ref BindingTable bindings)
    {
        if (!term.IsVariable)
            return true;

        var varName = _source.AsSpan(term.Start, term.Length);
        var idx = bindings.FindBinding(varName);
        if (idx >= 0)
        {
            // Already bound - check if values match
            return value.SequenceEqual(bindings.GetString(idx));
        }

        bindings.Bind(varName, value);
        return true;
    }

    /// <summary>
    /// Execute a subquery join directly.
    /// Now that QueryResults uses buffer-based patterns (~100 bytes) instead of inline GraphPattern (~4KB),
    /// no thread workaround is needed.
    /// </summary>
    private QueryResults ExecuteSubQueryJoin(SubSelect subSelect, Binding[] bindings, char[] stringBuffer)
    {
        // Execute directly - no thread workaround needed with buffer-based patterns
        var results = ExecuteSubQueryJoinCore(subSelect);

        if (results == null || results.Count == 0)
            return QueryResults.Empty();

        // Return via minimal materialized list wrapper
        return QueryResults.FromMaterializedList(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Core execution of subquery join. Uses instance fields to avoid copying large structs.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private System.Collections.Generic.List<MaterializedRow> ExecuteSubQueryJoinCore(SubSelect subSelect)
    {
        // Access pattern via ref to avoid copying ~4KB struct
        ref readonly var pattern = ref _query.WhereClause.Pattern;

        var results = new System.Collections.Generic.List<MaterializedRow>();
        var joinBindings = new Binding[16];
        var joinStringBuffer = new char[1024];
        var bindingTable = new BindingTable(joinBindings, joinStringBuffer);

        var hasFilters = pattern.FilterCount > 0;

        // Note: SubQueryJoinScan still takes pattern by value.
        // This is acceptable since we're only copying once (not recursively).
        var joinScan = new SubQueryJoinScan(_store, _source, pattern, subSelect);
        while (joinScan.MoveNext(ref bindingTable))
        {
            // Apply outer FILTER clauses
            if (hasFilters)
            {
                bool passesFilters = true;
                for (int i = 0; i < pattern.FilterCount; i++)
                {
                    var filter = pattern.GetFilter(i);
                    var filterExpr = _source.AsSpan(filter.Start, filter.Length);
                    var evaluator = new FilterEvaluator(filterExpr);
                    if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
                    {
                        passesFilters = false;
                        break;
                    }
                }
                if (!passesFilters)
                {
                    bindingTable.Clear();
                    continue;
                }
            }

            // Materialize this row
            results.Add(new MaterializedRow(bindingTable));
            bindingTable.Clear();
        }
        joinScan.Dispose();

        return results;
    }
}
