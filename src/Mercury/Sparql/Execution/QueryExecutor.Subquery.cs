using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Subquery execution methods.
/// Handles queries with nested SELECT subqueries.
/// </summary>
public partial class QueryExecutor
{
    /// <summary>
    /// Execute a query with subqueries and return lightweight materialized results.
    /// Use this for queries with subqueries to avoid stack overflow from large QueryResults struct.
    /// Caller must hold read lock on store.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal MaterializedQueryResults ExecuteSubQueryToMaterialized()
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
        try
        {
            while (subQueryScan.MoveNext(ref bindingTable))
            {
                ThrowIfCancellationRequested();
                results.Add(new MaterializedRow(bindingTable));
            }
        }
        finally
        {
            subQueryScan.Dispose();
        }

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
        try
        {
            while (subQueryScan.MoveNext(ref bindingTable))
            {
                ThrowIfCancellationRequested();
                results.Add(new MaterializedRow(bindingTable));
                bindingTable.Clear();
            }
        }
        finally
        {
            subQueryScan.Dispose();
        }

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
                ThrowIfCancellationRequested();
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
                        var rowStringBuffer = _bufferManager.Rent<char>(1024).Array!;
                        try
                        {
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
                        finally
                        {
                            _bufferManager.Return(rowStringBuffer);
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
                ThrowIfCancellationRequested();
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
                try
                {
                    while (scan.MoveNext(ref bindingTable))
                    {
                        ThrowIfCancellationRequested();
                        results.Add(new MaterializedRow(bindingTable));
                        bindingTable.TruncateTo(savedCount);
                    }
                }
                finally
                {
                    scan.Dispose();
                }
            }
        }

        return results;
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
    private List<MaterializedRow> ExecuteSubQueryJoinCore(SubSelect subSelect)
    {
        // Access pattern via ref to avoid copying ~4KB struct
        ref readonly var pattern = ref _query.WhereClause.Pattern;

        var results = new List<MaterializedRow>();
        var joinBindings = new Binding[16];
        var bindingTable = new BindingTable(joinBindings, _stringBuffer);

        var hasFilters = pattern.FilterCount > 0;

        // Note: SubQueryJoinScan still takes pattern by value.
        // This is acceptable since we're only copying once (not recursively).
        var joinScan = new SubQueryJoinScan(_store, _source, pattern, subSelect);
        try
        {
            while (joinScan.MoveNext(ref bindingTable))
            {
                ThrowIfCancellationRequested();
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
        }
        finally
        {
            joinScan.Dispose();
        }

        return results;
    }
}
