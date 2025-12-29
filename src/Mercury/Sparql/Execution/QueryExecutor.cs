using System;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Zero-allocation SPARQL query executor using specialized operators.
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
/// </summary>
public ref struct QueryExecutor
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly Query _query;

    // Dataset context: default graph IRIs (FROM) and named graph IRIs (FROM NAMED)
    // Stored as heap-allocated arrays to minimize ref struct size
    private readonly string[]? _defaultGraphs;
    private readonly string[]? _namedGraphs;

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, Query query)
    {
        _store = store;
        _source = source;
        _query = query;
        _defaultGraphs = null;
        _namedGraphs = null;

        // Extract dataset clauses into arrays
        if (query.Datasets.Length > 0)
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
    /// Execute a parsed query and return results.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public QueryResults Execute()
    {
        var pattern = _query.WhereClause.Pattern;
        var limit = _query.SolutionModifier.Limit;
        var offset = _query.SolutionModifier.Offset;
        var distinct = _query.SelectClause.Distinct;
        var orderBy = _query.SolutionModifier.OrderBy;
        var groupBy = _query.SolutionModifier.GroupBy;
        var having = _query.SolutionModifier.Having;
        var selectClause = _query.SelectClause;

        // Check for GRAPH clauses
        if (pattern.HasGraph && pattern.PatternCount == 0)
        {
            // Only GRAPH clause(s), no default graph patterns
            return ExecuteGraphClauses(pattern, limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        // Check for subqueries
        if (pattern.HasSubQueries)
        {
            return ExecuteWithSubQueries(pattern, limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        if (pattern.PatternCount == 0)
            return QueryResults.Empty();

        // Check for FROM clauses (default graph dataset)
        if (_defaultGraphs != null && _defaultGraphs.Length > 0)
        {
            return ExecuteWithDefaultGraphs(pattern, limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

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

            return new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer, limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        // No required patterns but have optional - need special handling
        if (requiredCount == 0)
        {
            // All patterns are optional - start with empty bindings and try to match optionals
            // For now, just return empty (proper implementation would need different semantics)
            return QueryResults.Empty();
        }

        // Multiple required patterns - need join
        return ExecuteWithJoins(pattern, bindings, stringBuffer, limit, offset, distinct, orderBy, groupBy, selectClause, having);
    }

    /// <summary>
    /// Execute query against specified default graphs (FROM clauses).
    /// For single FROM: query that graph directly.
    /// For multiple FROM: use DefaultGraphUnionScan for streaming results.
    /// </summary>
    private QueryResults ExecuteWithDefaultGraphs(GraphPattern pattern,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having)
    {
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);
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
                    limit, offset, distinct, orderBy, groupBy, selectClause, having);
            }

            if (requiredCount == 0)
                return QueryResults.Empty();

            // Multiple patterns - use MultiPatternScan with graph
            return new QueryResults(
                new MultiPatternScan(_store, _source, pattern, false, graphIri),
                pattern, _source, _store, bindings, stringBuffer,
                limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        // Multiple FROM clauses - use DefaultGraphUnionScan for streaming
        var unionScan = new DefaultGraphUnionScan(_store, _source, pattern, _defaultGraphs);
        return new QueryResults(unionScan, pattern, _source, _store, bindings, stringBuffer,
            limit, offset, distinct, orderBy, groupBy, selectClause, having);
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

    private QueryResults ExecuteWithJoins(GraphPattern pattern, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        // Use nested loop join for required patterns only
        return new QueryResults(
            new MultiPatternScan(_store, _source, pattern),
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
    /// Execute a query with only GRAPH clauses (no default graph patterns).
    /// For queries like: SELECT * WHERE { GRAPH &lt;g&gt; { ?s ?p ?o } }
    /// </summary>
    private QueryResults ExecuteGraphClauses(GraphPattern pattern,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having)
    {
        // For now, handle single GRAPH clause
        if (pattern.GraphClauseCount != 1)
            return QueryResults.Empty(); // Multiple GRAPH clauses need join - not yet supported

        var graphClause = pattern.GetGraphClause(0);

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Variable graph - iterate named graphs (filtered by FROM NAMED if present)
        if (graphClause.IsVariable)
        {
            if (graphClause.PatternCount == 0)
                return QueryResults.Empty();

            // Apply FROM NAMED restriction if present
            var scan = new VariableGraphScan(_store, _source, graphClause, _namedGraphs);

            return new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer,
                limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        // Get the graph IRI
        var graphIri = _source.Slice(graphClause.Graph.Start, graphClause.Graph.Length);

        var patternCount = graphClause.PatternCount;
        if (patternCount == 0)
            return QueryResults.Empty();

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
    private QueryResults ExecuteWithSubQueries(GraphPattern pattern,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having)
    {
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
            return ExecuteSubQueryJoin(pattern, subSelect, bindings, stringBuffer,
                limit, offset, distinct, orderBy, groupBy, selectClause, having);
        }

        return new QueryResults(subQueryScan, pattern, _source, _store, bindings, stringBuffer,
            limit, offset, distinct, orderBy, groupBy, selectClause, having);
    }

    /// <summary>
    /// Execute a subquery join by collecting results eagerly to avoid stack overflow from nested ref structs.
    /// </summary>
    private QueryResults ExecuteSubQueryJoin(GraphPattern pattern, SubSelect subSelect,
        Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having)
    {
        // Build outer pattern (without the subquery)
        var outerPattern = new GraphPattern();
        for (int i = 0; i < pattern.PatternCount; i++)
        {
            outerPattern.AddPattern(pattern.GetPattern(i));
        }

        // Execute the join and collect all results
        var results = new System.Collections.Generic.List<MaterializedRow>();

        var joinBindings = new Binding[16];
        var joinStringBuffer = new char[1024];
        var bindingTable = new BindingTable(joinBindings, joinStringBuffer);

        var hasFilters = pattern.FilterCount > 0;

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
                    var filterExpr = _source.Slice(filter.Start, filter.Length);
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

        if (results.Count == 0)
            return QueryResults.Empty();

        // Return via materialized results
        return QueryResults.FromMaterialized(results, outerPattern, _source, _store,
            bindings, stringBuffer, limit, offset, distinct, orderBy, groupBy, selectClause, having);
    }
}
