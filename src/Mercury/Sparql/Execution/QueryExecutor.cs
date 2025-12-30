using System;
using System.Buffers;
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
    private readonly char[] _stringBuffer; // Pooled buffer for string operations (replaces scattered new char[1024])
    private bool _disposed;

    // Dataset context: default graph IRIs (FROM) and named graph IRIs (FROM NAMED)
    private readonly string[]? _defaultGraphs;
    private readonly string[]? _namedGraphs;

    // SERVICE clause execution
    private readonly ISparqlServiceExecutor? _serviceExecutor;

    // Temporal query parameters
    private readonly TemporalQueryMode _temporalMode;
    private readonly DateTimeOffset _asOfTime;
    private readonly DateTimeOffset _rangeStart;
    private readonly DateTimeOffset _rangeEnd;

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, in Query query)
        : this(store, source, in query, null) { }

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, in Query query,
        ISparqlServiceExecutor? serviceExecutor)
    {
        _store = store;
        _source = source.ToString();  // Copy to heap - enables class-based execution
        _query = query;  // Copy here is unavoidable since we store it
        _stringBuffer = ArrayPool<char>.Shared.Rent(1024);  // Pooled buffer for string operations

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

        // Extract temporal clause parameters
        _temporalMode = query.SolutionModifier.Temporal.Mode;
        if (_temporalMode == TemporalQueryMode.AsOf)
        {
            var timeStr = source.Slice(
                query.SolutionModifier.Temporal.TimeStartStart,
                query.SolutionModifier.Temporal.TimeStartLength);
            _asOfTime = ParseDateTimeOffset(timeStr);
        }
        else if (_temporalMode == TemporalQueryMode.During)
        {
            var startStr = source.Slice(
                query.SolutionModifier.Temporal.TimeStartStart,
                query.SolutionModifier.Temporal.TimeStartLength);
            var endStr = source.Slice(
                query.SolutionModifier.Temporal.TimeEndStart,
                query.SolutionModifier.Temporal.TimeEndLength);
            _rangeStart = ParseDateTimeOffset(startStr);
            _rangeEnd = ParseDateTimeOffset(endStr);
        }
    }

    /// <summary>
    /// Parse a datetime literal like "2023-06-15"^^xsd:date or "2023-06-15T10:30:00"^^xsd:dateTime
    /// </summary>
    private static DateTimeOffset ParseDateTimeOffset(ReadOnlySpan<char> literal)
    {
        // Extract value between quotes
        int start = 0;
        int end = literal.Length;

        // Find opening quote
        for (int i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '"') { start = i + 1; break; }
        }

        // Find closing quote
        for (int i = start; i < literal.Length; i++)
        {
            if (literal[i] == '"') { end = i; break; }
        }

        var value = literal.Slice(start, end - start);

        // Try parse as DateTimeOffset
        if (DateTimeOffset.TryParse(value, out var result))
            return result;

        // Try parse as date only (assume start of day)
        if (DateTime.TryParse(value.ToString(), out var dt))
            return new DateTimeOffset(dt, TimeSpan.Zero);

        return DateTimeOffset.MinValue;
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
        _stringBuffer = ArrayPool<char>.Shared.Rent(1024);  // Pooled buffer for string operations
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
        if (_stringBuffer != null)
            ArrayPool<char>.Shared.Return(_stringBuffer);
    }

    /// <summary>
    /// Execute a GRAPH-only query and return lightweight materialized results.
    /// Use this for queries with GRAPH clauses to avoid stack overflow from large QueryResults struct.
    /// The returned MaterializedQueryResults is ~200 bytes vs ~22KB for full QueryResults.
    /// Caller must hold read lock on store.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public MaterializedQueryResults ExecuteGraphToMaterialized()
    {
        if (!_buffer.HasGraph || _buffer.TriplePatternCount != 0)
        {
            // Not a pure GRAPH query - return empty
            return MaterializedQueryResults.Empty();
        }

        var patterns = _buffer.GetPatterns();
        var graphCount = _buffer.GraphClauseCount;
        List<MaterializedRow>? results;

        if (graphCount > 1)
        {
            results = CollectAndJoinGraphResultsSlotBased(patterns);
        }
        else if (_buffer.FirstGraphIsVariable)
        {
            if (_buffer.FirstGraphPatternCount == 0)
                return MaterializedQueryResults.Empty();
            results = ExecuteVariableGraphSlotBased(patterns);
        }
        else
        {
            results = ExecuteFixedGraphSlotBasedList(patterns);
        }

        if (results == null || results.Count == 0)
            return MaterializedQueryResults.Empty();

        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        return new MaterializedQueryResults(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Execute a query with FROM clauses and return lightweight materialized results.
    /// Use this for queries with FROM dataset clauses to avoid stack overflow from large QueryResults struct.
    /// Caller must hold read lock on store.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public MaterializedQueryResults ExecuteFromToMaterialized()
    {
        if (_defaultGraphs == null || _defaultGraphs.Length == 0)
        {
            // Not a FROM query - return empty
            return MaterializedQueryResults.Empty();
        }

        var results = new List<MaterializedRow>();
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var requiredCount = pattern.RequiredPatternCount;

        if (requiredCount == 0)
            return MaterializedQueryResults.Empty();

        // Single FROM clause - query that specific graph
        if (_defaultGraphs.Length == 1)
        {
            var graphIri = _defaultGraphs[0];

            if (requiredCount == 1)
            {
                int requiredIdx = 0;
                for (int i = 0; i < pattern.PatternCount; i++)
                {
                    if (!pattern.IsOptional(i)) { requiredIdx = i; break; }
                }

                var tp = pattern.GetPattern(requiredIdx);
                var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graphIri.AsSpan(),
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd);

                while (scan.MoveNext(ref bindingTable))
                {
                    if (!PassesFilters(in pattern, ref bindingTable))
                    {
                        bindingTable.Clear();
                        continue;
                    }
                    results.Add(new MaterializedRow(bindingTable));
                }
                scan.Dispose();
            }
            else
            {
                // Multiple patterns - use MultiPatternScan with graph
                var multiScan = new MultiPatternScan(_store, _source, pattern, false, graphIri.AsSpan(),
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd);

                while (multiScan.MoveNext(ref bindingTable))
                {
                    if (!PassesFilters(in pattern, ref bindingTable))
                    {
                        bindingTable.Clear();
                        continue;
                    }
                    results.Add(new MaterializedRow(bindingTable));
                }
                multiScan.Dispose();
            }
        }
        else
        {
            // Multiple FROM clauses - use CrossGraphMultiPatternScan
            var crossGraphScan = new CrossGraphMultiPatternScan(_store, _source, pattern, _defaultGraphs);

            while (crossGraphScan.MoveNext(ref bindingTable))
            {
                if (!PassesFilters(in pattern, ref bindingTable))
                {
                    bindingTable.Clear();
                    continue;
                }
                results.Add(new MaterializedRow(bindingTable));
            }
            crossGraphScan.Dispose();
        }

        if (results.Count == 0)
            return MaterializedQueryResults.Empty();

        return new MaterializedQueryResults(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Execute a query with subqueries and return lightweight materialized results.
    /// Use this for queries with subqueries to avoid stack overflow from large QueryResults struct.
    /// Caller must hold read lock on store.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public MaterializedQueryResults ExecuteSubQueryToMaterialized()
    {
        if (!_buffer.HasSubQueries)
        {
            // Not a subquery - return empty
            return MaterializedQueryResults.Empty();
        }

        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        List<MaterializedRow>? results;

        // Check for multiple subqueries
        if (pattern.SubQueryCount > 1)
        {
            // Collect results from all subqueries and join them
            results = CollectAndJoinSubQueryResults();
            if (results == null || results.Count == 0)
                return MaterializedQueryResults.Empty();

            // If there are outer triple patterns, join with them too
            if (pattern.RequiredPatternCount > 0)
            {
                results = JoinWithOuterPatterns(results);
            }
        }
        else if (pattern.PatternCount > 0)
        {
            // Single subquery with outer patterns - use join
            var subSelect = pattern.GetSubQuery(0);
            results = ExecuteSubQueryJoinCore(subSelect);
        }
        else
        {
            // Single subquery without outer patterns
            var subSelect = pattern.GetSubQuery(0);
            results = ExecuteSubQuerySimpleCore(subSelect);
        }

        if (results == null || results.Count == 0)
            return MaterializedQueryResults.Empty();

        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        return new MaterializedQueryResults(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Core logic for executing a single subquery without outer patterns.
    /// Returns materialized rows.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteSubQuerySimpleCore(SubSelect subSelect)
    {
        var results = new List<MaterializedRow>();
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Create SubQueryScan operator
        var subQueryScan = new SubQueryScan(_store, _source, subSelect);

        while (subQueryScan.MoveNext(ref bindingTable))
        {
            results.Add(new MaterializedRow(bindingTable));
        }
        subQueryScan.Dispose();

        return results;
    }

    /// <summary>
    /// Execute a query with SERVICE clause and return lightweight materialized results.
    /// Use this for queries with SERVICE clauses to avoid stack overflow from large QueryResults struct.
    /// Caller must hold read lock on store.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public MaterializedQueryResults ExecuteServiceToMaterialized()
    {
        if (!_buffer.HasService)
        {
            // Not a SERVICE query - return empty
            return MaterializedQueryResults.Empty();
        }

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
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // For now, only handle the simple case: SERVICE clause only, no local patterns
        if (pattern.PatternCount == 0 && serviceCount == 1)
        {
            // Execute single SERVICE clause
            var serviceClause = pattern.GetServiceClause(0);
            var serviceScan = new ServiceScan(_serviceExecutor, _source, serviceClause, bindingTable);

            // Materialize results
            var results = new List<MaterializedRow>();
            while (serviceScan.MoveNext(ref bindingTable))
            {
                results.Add(new MaterializedRow(bindingTable));
            }
            serviceScan.Dispose();

            if (results.Count == 0)
                return MaterializedQueryResults.Empty();

            return new MaterializedQueryResults(results, bindings, stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
        }

        // TODO: Handle SERVICE with local patterns (requires join)
        // TODO: Handle multiple SERVICE clauses
        // For now, return empty for unsupported cases
        return MaterializedQueryResults.Empty();
    }

    /// <summary>
    /// Execute a parsed query and return results.
    /// Caller must hold read lock on store and call Dispose on results.
    /// Note: Use _buffer for pattern metadata to avoid large struct copies that cause stack overflow.
    /// </summary>
    public QueryResults Execute()
    {
        // Check for GRAPH clauses first - use _buffer to avoid large struct copies
        // A query with GRAPH but no direct triple patterns (only patterns inside GRAPH)
        if (_buffer.HasGraph && _buffer.TriplePatternCount == 0)
        {
            return ExecuteGraphClauses();
        }

        // Check for subqueries - use _buffer
        if (_buffer.HasSubQueries)
        {
            return ExecuteWithSubQueries();
        }

        // Check for SERVICE clauses - use _buffer
        if (_buffer.HasService)
        {
            return ExecuteWithService();
        }

        if (_buffer.TriplePatternCount == 0)
            return QueryResults.Empty();

        // Check for FROM clauses (default graph dataset)
        if (_defaultGraphs != null && _defaultGraphs.Length > 0)
        {
            return ExecuteWithDefaultGraphs();
        }

        // For regular queries, access _query fields directly to build result
        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
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
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd);

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
        var stringBuffer = _stringBuffer;
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
                var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graphIri,
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd);

                return new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer,
                    _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, (_query.SelectClause.Distinct || _query.SelectClause.Reduced),
                    _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
                    _query.SolutionModifier.Having);
            }

            if (requiredCount == 0)
                return QueryResults.Empty();

            // Multiple patterns - use MultiPatternScan with graph
            return new QueryResults(
                new MultiPatternScan(_store, _source, pattern, false, graphIri,
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd),
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
        var stringBuffer = _stringBuffer;
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
        var stringBuffer = _stringBuffer;
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
        var stringBuffer = _stringBuffer;
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
        var stringBuffer = _stringBuffer;

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
        var stringBuffer = _stringBuffer;
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
    /// Uses PatternSlot views to avoid large struct copies and stack overflow.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteGraphClauses()
    {
        // Get patterns from buffer - this returns a ref struct view (16 bytes)
        var patterns = _buffer.GetPatterns();
        var graphCount = _buffer.GraphClauseCount;

        // Multiple GRAPH clauses - join results
        if (graphCount > 1)
        {
            var joinedResults = CollectAndJoinGraphResultsSlotBased(patterns);
            if (joinedResults == null || joinedResults.Count == 0)
                return QueryResults.Empty();

            var bindings = new Binding[16];
            var stringBuffer = _stringBuffer;
            return QueryResults.FromMaterializedList(joinedResults, bindings, stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
        }

        // Single GRAPH clause - check if variable or fixed
        if (_buffer.FirstGraphIsVariable)
        {
            if (_buffer.FirstGraphPatternCount == 0)
                return QueryResults.Empty();

            // Variable graph execution
            var results = ExecuteVariableGraphSlotBased(patterns);
            if (results == null || results.Count == 0)
                return QueryResults.Empty();

            var bindings = new Binding[16];
            var stringBuffer = _stringBuffer;
            return QueryResults.FromMaterializedList(results, bindings, stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
        }

        // Fixed IRI graph execution - get list to avoid returning large QueryResults from nested call
        var fixedResults = ExecuteFixedGraphSlotBasedList(patterns);
        if (fixedResults == null || fixedResults.Count == 0)
            return QueryResults.Empty();

        var fixedBindings = new Binding[16];
        return QueryResults.FromMaterializedList(fixedResults, fixedBindings, _stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Collect results from all GRAPH clauses using PatternSlot views.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? CollectAndJoinGraphResultsSlotBased(Patterns.PatternArray patterns)
    {
        List<MaterializedRow>? joinedResults = null;
        var graphHeaders = patterns.EnumerateGraphHeaders();

        while (graphHeaders.MoveNext())
        {
            var header = graphHeaders.Current;
            var headerIndex = graphHeaders.CurrentIndex;

            if (header.ChildCount == 0)
                continue;

            List<MaterializedRow>? graphResults;
            if (header.GraphTermType == TermType.Variable)
            {
                graphResults = ExecuteVariableGraphSlotBasedSingle(patterns, headerIndex, header);
            }
            else
            {
                graphResults = ExecuteFixedGraphSlotBasedSingle(patterns, headerIndex, header);
            }

            if (graphResults == null || graphResults.Count == 0)
                return null;

            if (joinedResults == null)
            {
                joinedResults = graphResults;
            }
            else
            {
                joinedResults = JoinMaterializedRows(joinedResults, graphResults);
                if (joinedResults.Count == 0)
                    return null;
            }
        }

        return joinedResults;
    }

    /// <summary>
    /// Execute variable graph clause using PatternSlot views (GRAPH ?g { ... }).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteVariableGraphSlotBased(Patterns.PatternArray patterns)
    {
        // Find the first graph header
        var graphHeaders = patterns.EnumerateGraphHeaders();
        if (!graphHeaders.MoveNext())
            return null;

        return ExecuteVariableGraphSlotBasedSingle(patterns, graphHeaders.CurrentIndex, graphHeaders.Current);
    }

    /// <summary>
    /// Execute a single variable graph clause from slot data.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteVariableGraphSlotBasedSingle(
        Patterns.PatternArray patterns, int headerIndex, Patterns.PatternSlot header)
    {
        var results = new List<MaterializedRow>();
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Get variable name from source
        var varName = _source.AsSpan().Slice(header.GraphTermStart, header.GraphTermLength);

        // Iterate all named graphs (or FROM NAMED restricted graphs)
        if (_namedGraphs != null && _namedGraphs.Length > 0)
        {
            // FROM NAMED specified - only iterate those graphs
            foreach (var graphStr in _namedGraphs)
            {
                var graphIri = graphStr.AsSpan();

                // Bind graph variable
                bindingTable.Clear();
                bindingTable.Bind(varName, graphIri);

                // Execute patterns in this graph
                var children = patterns.GetChildren(headerIndex);

                if (children.Count == 1)
                {
                    var childSlot = children[0];
                    if (childSlot.IsTriple)
                    {
                        ExecuteSinglePatternSlotBased(childSlot, ref bindingTable, graphStr, results);
                    }
                }
                else
                {
                    ExecuteMultiPatternSlotBased(children, ref bindingTable, graphStr, results);
                }
            }
            return results;
        }

        // No FROM NAMED restriction - iterate all named graphs
        foreach (var graphIri in _store.GetNamedGraphs())
        {
            // Bind graph variable
            bindingTable.Clear();
            bindingTable.Bind(varName, graphIri);

            // Execute patterns in this graph
            var children = patterns.GetChildren(headerIndex);
            var graphStr = graphIri.ToString();

            if (children.Count == 1)
            {
                // Single pattern - simple scan
                var childSlot = children[0];
                if (childSlot.IsTriple)
                {
                    ExecuteSinglePatternSlotBased(childSlot, ref bindingTable, graphStr, results);
                }
            }
            else
            {
                // Multiple patterns - use nested loops
                ExecuteMultiPatternSlotBased(children, ref bindingTable, graphStr, results);
            }
        }

        return results;
    }

    /// <summary>
    /// Execute a single triple pattern from a slot.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteSinglePatternSlotBased(
        Patterns.PatternSlot slot, ref BindingTable bindingTable, string? graph, List<MaterializedRow> results)
    {
        var tp = new TriplePattern
        {
            Subject = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength },
            Predicate = new Term { Type = slot.PredicateType, Start = slot.PredicateStart, Length = slot.PredicateLength },
            Object = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength }
        };

        var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graph.AsSpan(),
            _temporalMode, _asOfTime, _rangeStart, _rangeEnd);

        while (scan.MoveNext(ref bindingTable))
        {
            results.Add(new MaterializedRow(bindingTable));
        }

        scan.Dispose();
    }

    /// <summary>
    /// Execute fixed graph clause using PatternSlot views (GRAPH &lt;iri&gt; { ... }).
    /// Returns List to avoid stack overflow from returning large QueryResults by value.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteFixedGraphSlotBasedList(Patterns.PatternArray patterns)
    {
        // Find the first graph header
        var graphHeaders = patterns.EnumerateGraphHeaders();
        if (!graphHeaders.MoveNext())
            return null;

        var header = graphHeaders.Current;
        var headerIndex = graphHeaders.CurrentIndex;

        // Get graph IRI from source
        var graphIri = _source.AsSpan().Slice(header.GraphTermStart, header.GraphTermLength).ToString();
        var children = patterns.GetChildren(headerIndex);

        if (children.Count == 0)
            return null;

        // Materialize results - this avoids issues with returning scan refs
        var results = new List<MaterializedRow>();
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        if (children.Count == 1)
        {
            var childSlot = children[0];
            if (childSlot.IsTriple)
            {
                ExecuteSinglePatternSlotBased(childSlot, ref bindingTable, graphIri, results);
            }
        }
        else
        {
            ExecuteMultiPatternSlotBased(children, ref bindingTable, graphIri, results);
        }

        return results.Count == 0 ? null : results;
    }

    /// <summary>
    /// Execute a single fixed graph clause from slot data.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteFixedGraphSlotBasedSingle(
        Patterns.PatternArray patterns, int headerIndex, Patterns.PatternSlot header)
    {
        var graphIri = _source.AsSpan().Slice(header.GraphTermStart, header.GraphTermLength).ToString();
        var children = patterns.GetChildren(headerIndex);

        if (children.Count == 0)
            return null;

        var results = new List<MaterializedRow>();
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        if (children.Count == 1)
        {
            var childSlot = children[0];
            if (childSlot.IsTriple)
            {
                ExecuteSinglePatternSlotBased(childSlot, ref bindingTable, graphIri, results);
            }
        }
        else
        {
            ExecuteMultiPatternSlotBased(children, ref bindingTable, graphIri, results);
        }

        return results;
    }

    /// <summary>
    /// Execute multiple patterns from a slot slice using nested loop joins.
    /// Avoids creating GraphPattern on stack (~8KB) by using recursive pattern execution.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteMultiPatternSlotBased(
        Patterns.PatternArraySlice children,
        ref BindingTable bindingTable,
        string? graph,
        List<MaterializedRow> results)
    {
        // Build array of TriplePattern structs (each ~70 bytes, total < 1KB for typical queries)
        var patterns = new TriplePattern[children.Count];
        int patternCount = 0;

        for (int i = 0; i < children.Count; i++)
        {
            var slot = children[i];
            if (slot.IsTriple)
            {
                patterns[patternCount++] = new TriplePattern
                {
                    Subject = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength },
                    Predicate = new Term { Type = slot.PredicateType, Start = slot.PredicateStart, Length = slot.PredicateLength },
                    Object = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength }
                };
            }
        }

        if (patternCount == 0)
            return;

        // Execute patterns using nested loop join
        ExecuteNestedLoopJoin(patterns, patternCount, 0, ref bindingTable, graph, results);
    }

    /// <summary>
    /// Execute patterns using recursive nested loop join.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteNestedLoopJoin(
        TriplePattern[] patterns, int patternCount, int patternIndex,
        ref BindingTable bindingTable, string? graph, List<MaterializedRow> results)
    {
        if (patternIndex >= patternCount)
        {
            // All patterns matched - emit result
            results.Add(new MaterializedRow(bindingTable));
            return;
        }

        var tp = patterns[patternIndex];
        var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graph.AsSpan(),
            _temporalMode, _asOfTime, _rangeStart, _rangeEnd);

        while (scan.MoveNext(ref bindingTable))
        {
            // Recursively match remaining patterns with current bindings
            ExecuteNestedLoopJoin(patterns, patternCount, patternIndex + 1, ref bindingTable, graph, results);
        }

        scan.Dispose();
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
        var stringBuffer = _stringBuffer;
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
        var stringBuffer = new char[1024]; // Static method - can't use pooled instance buffer

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
    /// Uses materialization pattern to avoid stack overflow from large QueryResults struct.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteFixedGraphClause()
    {
        // Materialize results to avoid stack overflow from large scan structs
        var results = ExecuteFixedGraphClauseCore();

        if (results == null || results.Count == 0)
            return QueryResults.Empty();

        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        return QueryResults.FromMaterializedList(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Core fixed graph execution - materializes results on thread with larger stack.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteFixedGraphClauseCore()
    {
        List<MaterializedRow>? results = null;
        var store = _store;
        var source = _source;

        // Access pattern data needed for execution
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var graphClause = pattern.GetGraphClause(0);
        var patternCount = graphClause.PatternCount;

        if (patternCount == 0)
            return null;

        var graphStart = graphClause.Graph.Start;
        var graphLength = graphClause.Graph.Length;

        // Single pattern - can execute without thread workaround
        if (patternCount == 1)
        {
            var tp = graphClause.GetPattern(0);
            return ExecuteFixedGraphSinglePattern(store, source, tp, graphStart, graphLength);
        }

        // Multiple patterns - execute on thread with larger stack
        // Copy patterns to array to pass to thread
        var patterns = new TriplePattern[patternCount];
        for (int i = 0; i < patternCount; i++)
        {
            patterns[i] = graphClause.GetPattern(i);
        }

        var thread = new System.Threading.Thread(() =>
        {
            var bindings = new Binding[16];
            var stringBuffer = _stringBuffer;
            var bindingTable = new BindingTable(bindings, stringBuffer);
            var graphIri = source.AsSpan(graphStart, graphLength);

            // Build GraphPattern from copied patterns
            var graphPattern = new GraphPattern();
            for (int i = 0; i < patterns.Length; i++)
            {
                graphPattern.AddPattern(patterns[i]);
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

        return results;
    }

    /// <summary>
    /// Execute single pattern in fixed graph - no thread needed, small stack frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<MaterializedRow> ExecuteFixedGraphSinglePattern(
        QuadStore store, string source, TriplePattern tp, int graphStart, int graphLength)
    {
        var bindings = new Binding[16];
        var stringBuffer = new char[1024]; // Static method - can't use pooled instance buffer
        var bindingTable = new BindingTable(bindings, stringBuffer);
        var graphIri = source.AsSpan(graphStart, graphLength);

        var scan = new TriplePatternScan(store, source, tp, bindingTable, graphIri);
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
        var stringBuffer = _stringBuffer;

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
        var stringBuffer = _stringBuffer;
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
        var stringBuffer = _stringBuffer;
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
        var stringBuffer = _stringBuffer;
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
        var stringBuffer = _stringBuffer;
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
        var bindingTable = new BindingTable(joinBindings, _stringBuffer);

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

    /// <summary>
    /// Evaluates all filter expressions for the given pattern against the current bindings.
    /// Returns true if all filters pass, false if any filter fails.
    /// </summary>
    private bool PassesFilters(in GraphPattern pattern, ref BindingTable bindingTable)
    {
        if (pattern.FilterCount == 0)
            return true;

        for (int i = 0; i < pattern.FilterCount; i++)
        {
            var filter = pattern.GetFilter(i);
            var filterExpr = _source.AsSpan(filter.Start, filter.Length);
            var evaluator = new FilterEvaluator(filterExpr);
            if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
            {
                return false;
            }
        }
        return true;
    }
}
