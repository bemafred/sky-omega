using System;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql;

/// <summary>
/// Zero-allocation SPARQL query executor using specialized operators.
///
/// Execution model:
/// 1. Parse query → Query struct with triple patterns + filters
/// 2. Build execution plan → Stack of operators
/// 3. Execute → Pull-based iteration through operator pipeline
/// </summary>
public ref struct QueryExecutor
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly Query _query;

    public QueryExecutor(TripleStore store, ReadOnlySpan<char> source, Query query)
    {
        _store = store;
        _source = source;
        _query = query;
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

        // Variable graph - iterate all named graphs
        if (graphClause.IsVariable)
        {
            if (graphClause.PatternCount == 0)
                return QueryResults.Empty();

            var scan = new VariableGraphScan(_store, _source, graphClause);

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
            // TODO: Join outer patterns with subquery results
            // For now, return just the subquery results
        }

        return new QueryResults(subQueryScan, pattern, _source, _store, bindings, stringBuffer,
            limit, offset, distinct, orderBy, groupBy, selectClause, having);
    }
}
