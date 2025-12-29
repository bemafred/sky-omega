using System;
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

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, Query query)
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
    }

    /// <summary>
    /// Alternative constructor that takes a pre-allocated QueryBuffer directly.
    /// The caller transfers ownership of the buffer to the executor.
    /// </summary>
    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, QueryBuffer buffer)
    {
        _store = store;
        _source = source.ToString();
        _buffer = buffer;
        _query = default;  // Not used when buffer is provided directly

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

            return new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer,
                _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, _query.SelectClause.Distinct,
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

                return new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer,
                    _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, _query.SelectClause.Distinct,
                    _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
                    _query.SolutionModifier.Having);
            }

            if (requiredCount == 0)
                return QueryResults.Empty();

            // Multiple patterns - use MultiPatternScan with graph
            return new QueryResults(
                new MultiPatternScan(_store, _source, pattern, false, graphIri),
                pattern, _source, _store, bindings, stringBuffer,
                _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, _query.SelectClause.Distinct,
                _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
                _query.SolutionModifier.Having);
        }

        // Multiple FROM clauses - use DefaultGraphUnionScan for streaming
        var unionScan = new DefaultGraphUnionScan(_store, _source, pattern, _defaultGraphs);
        return new QueryResults(unionScan, pattern, _source, _store, bindings, stringBuffer,
            _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, _query.SelectClause.Distinct,
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
            var results = new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer);
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
        var multiResults = new QueryResults(multiScan, pattern, _source, _store, bindings, stringBuffer);
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
            var queryResults = new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer);

            return new ConstructResults(queryResults, template, _source, bindings, stringBuffer);
        }

        // No required patterns
        if (requiredCount == 0)
        {
            return ConstructResults.Empty();
        }

        // Multiple required patterns - need join
        var multiScan = new MultiPatternScan(_store, _source, pattern);
        var multiResults = new QueryResults(multiScan, pattern, _source, _store, bindings, stringBuffer);

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
            queryResults = new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer);
        }
        else if (requiredCount == 0)
        {
            return DescribeResults.Empty();
        }
        else
        {
            var multiScan = new MultiPatternScan(_store, _source, pattern);
            queryResults = new QueryResults(multiScan, pattern, _source, _store, bindings, stringBuffer);
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
            pattern,
            _source,
            _store,
            bindings,
            stringBuffer,
            _query.SolutionModifier.Limit,
            _query.SolutionModifier.Offset,
            _query.SelectClause.Distinct,
            _query.SolutionModifier.OrderBy,
            _query.SolutionModifier.GroupBy,
            _query.SelectClause,
            _query.SolutionModifier.Having);
    }

    /// <summary>
    /// Execute a query with only GRAPH clauses (no default graph patterns).
    /// For queries like: SELECT * WHERE { GRAPH &lt;g&gt; { ?s ?p ?o } }
    /// NOTE: This method is carefully structured to minimize stack usage.
    /// </summary>
    private QueryResults ExecuteGraphClauses()
    {
        // Check conditions WITHOUT creating local struct copies
        if (_query.WhereClause.Pattern.GraphClauseCount != 1)
            return QueryResults.Empty(); // Multiple GRAPH clauses need join - not yet supported

        // Check if it's a variable graph - use pattern.GetGraphClause(0).IsVariable directly
        // to avoid creating a local copy of GraphClause
        if (_query.WhereClause.Pattern.GetGraphClause(0).IsVariable)
        {
            if (_query.WhereClause.Pattern.GetGraphClause(0).PatternCount == 0)
                return QueryResults.Empty();

            // Delegate to static method that creates the config directly
            // This avoids having large struct locals on THIS method's stack
            return ExecuteVariableGraphClauses();
        }

        // For fixed IRI graph - proceed with smaller stack usage
        return ExecuteFixedGraphClause();
    }

    // Holder class for QueryResults since ref structs can't be captured in closures
    private sealed class ResultHolder
    {
        public System.Collections.Generic.List<MaterializedRow>? Results;
        public System.Exception? Exception;
    }

    /// <summary>
    /// Helper method for variable graph execution.
    /// Runs on a separate thread with larger stack to avoid stack overflow
    /// from deep call chains combined with large struct locals.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteVariableGraphClauses()
    {
        // Create config object - copies structs to heap
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];

        var config = new VariableGraphExecutor.ExecutionConfig
        {
            Store = _store,
            Source = _source,
            GraphClause = _query.WhereClause.Pattern.GetGraphClause(0),
            Pattern = _query.WhereClause.Pattern,
            NamedGraphs = _namedGraphs,
            Bindings = bindings,
            StringBuffer = stringBuffer,
            Limit = _query.SolutionModifier.Limit,
            Offset = _query.SolutionModifier.Offset,
            Distinct = _query.SelectClause.Distinct,
            OrderBy = _query.SolutionModifier.OrderBy,
            GroupBy = _query.SolutionModifier.GroupBy,
            SelectClause = _query.SelectClause,
            Having = _query.SolutionModifier.Having
        };

        // Execute on a thread with a larger stack (4MB instead of default ~1MB)
        // This avoids stack overflow from combined large struct locals and xUnit framework overhead
        var holder = new ResultHolder();

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                // Execute and collect results as materialized rows
                holder.Results = VariableGraphExecutor.ExecuteAndCollect(config);
            }
            catch (System.Exception ex)
            {
                holder.Exception = ex;
            }
        }, maxStackSize: 4 * 1024 * 1024); // 4MB stack

        thread.Start();
        thread.Join();

        if (holder.Exception != null)
            throw holder.Exception;

        if (holder.Results == null || holder.Results.Count == 0)
            return QueryResults.Empty();

        // Return results via FromMaterializedSimple to avoid stack overflow
        // from passing large GraphPattern struct
        return QueryResults.FromMaterializedSimple(holder.Results, _source, _store,
            bindings, stringBuffer, config.Limit, config.Offset, config.Distinct,
            config.OrderBy, config.GroupBy, config.SelectClause, config.Having);
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
        var distinct = _query.SelectClause.Distinct;
        var orderBy = _query.SolutionModifier.OrderBy;
        var groupBy = _query.SolutionModifier.GroupBy;
        var selectClause = _query.SelectClause;
        var having = _query.SolutionModifier.Having;

        // Single pattern in GRAPH clause
        if (patternCount == 1)
        {
            var tp = graphClause.GetPattern(0);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graphIri);

            return new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer,
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
            pattern,
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
    /// </summary>
    private QueryResults ExecuteWithSubQueries()
    {
        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _query.WhereClause.Pattern;

        // For now, handle single subquery case
        if (pattern.SubQueryCount != 1)
            return QueryResults.Empty(); // Multiple subqueries need join - not yet supported

        var subSelect = pattern.GetSubQuery(0);

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];

        // Create SubQueryScan operator
        var subQueryScan = new SubQueryScan(_store, _source, subSelect);

        // If there are outer patterns, we'd need to join them
        // For now, just execute the subquery directly
        if (pattern.PatternCount > 0)
        {
            // Execute join using SubQueryJoinScan
            // Note: This scan needs to be consumed before QueryResults to avoid stack overflow
            // due to ref struct size. We collect results eagerly then return a MultiPatternScan.
            return ExecuteSubQueryJoin(subSelect, bindings, stringBuffer);
        }

        return new QueryResults(subQueryScan, pattern, _source, _store, bindings, stringBuffer,
            _query.SolutionModifier.Limit, _query.SolutionModifier.Offset, _query.SelectClause.Distinct,
            _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy, _query.SelectClause,
            _query.SolutionModifier.Having);
    }

    /// <summary>
    /// Execute a subquery join by running on a separate thread with larger stack
    /// to avoid stack overflow from large struct locals.
    /// </summary>
    private QueryResults ExecuteSubQueryJoin(SubSelect subSelect, Binding[] bindings, char[] stringBuffer)
    {
        // Execute on a thread with a larger stack (4MB instead of default ~1MB)
        // This avoids stack overflow from large structs (GraphPattern, SubQueryJoinScan)
        var holder = new ResultHolder();

        // Capture needed values for the thread
        var store = _store;
        var source = _source;
        var query = _query;

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                holder.Results = ExecuteSubQueryJoinCore(store, source, query, subSelect);
            }
            catch (System.Exception ex)
            {
                holder.Exception = ex;
            }
        }, maxStackSize: 4 * 1024 * 1024); // 4MB stack

        thread.Start();
        thread.Join();

        if (holder.Exception != null)
            throw holder.Exception;

        if (holder.Results == null || holder.Results.Count == 0)
            return QueryResults.Empty();

        // Return results via FromMaterializedSimple to avoid stack overflow
        return QueryResults.FromMaterializedSimple(holder.Results, _source, _store,
            bindings, stringBuffer, _query.SolutionModifier.Limit, _query.SolutionModifier.Offset,
            _query.SelectClause.Distinct, _query.SolutionModifier.OrderBy, _query.SolutionModifier.GroupBy,
            _query.SelectClause, _query.SolutionModifier.Having);
    }

    /// <summary>
    /// Core execution of subquery join. Called on separate thread with larger stack.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static System.Collections.Generic.List<MaterializedRow> ExecuteSubQueryJoinCore(
        QuadStore store, string source, Query query, SubSelect subSelect)
    {
        var pattern = query.WhereClause.Pattern;

        // Execute the join and collect all results
        var results = new System.Collections.Generic.List<MaterializedRow>();

        var joinBindings = new Binding[16];
        var joinStringBuffer = new char[1024];
        var bindingTable = new BindingTable(joinBindings, joinStringBuffer);

        var hasFilters = pattern.FilterCount > 0;

        var joinScan = new SubQueryJoinScan(store, source, pattern, subSelect);
        while (joinScan.MoveNext(ref bindingTable))
        {
            // Apply outer FILTER clauses
            if (hasFilters)
            {
                bool passesFilters = true;
                for (int i = 0; i < pattern.FilterCount; i++)
                {
                    var filter = pattern.GetFilter(i);
                    var filterExpr = source.AsSpan(filter.Start, filter.Length);
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
