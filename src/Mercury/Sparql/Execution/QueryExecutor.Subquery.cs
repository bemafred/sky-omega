using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Execution.Operators;

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
        ref readonly var pattern = ref _cachedPattern;
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
        ref readonly var pattern = ref _cachedPattern;
        var hasExists = pattern.HasExists;

        var results = new List<MaterializedRow>();
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Create SubQueryScan operator with prefix mappings and named graphs for expansion
        var subQueryScan = new SubQueryScan(_store, _source, subSelect, _prefixMappings, _namedGraphs);
        try
        {
            while (subQueryScan.MoveNext(ref bindingTable))
            {
                // Apply EXISTS/NOT EXISTS filters
                if (hasExists)
                {
                    bool passesExists = true;
                    for (int i = 0; i < pattern.ExistsFilterCount; i++)
                    {
                        var existsFilter = pattern.GetExistsFilter(i);
                        var matches = EvaluateExistsFilterWithBindings(existsFilter, bindingTable);

                        // EXISTS: must match at least once
                        // NOT EXISTS: must not match at all
                        if (existsFilter.Negated)
                        {
                            if (matches) { passesExists = false; break; } // NOT EXISTS failed - found a match
                        }
                        else
                        {
                            if (!matches) { passesExists = false; break; } // EXISTS failed - no match found
                        }
                    }
                    if (!passesExists)
                    {
                        bindingTable.Clear();
                        continue;
                    }
                }

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
    /// Execute a query that contains subqueries.
    /// For queries like: SELECT * WHERE { ?s ?p ?o . { SELECT ?s WHERE { ... } } }
    /// Returns List&lt;MaterializedRow&gt; to avoid stack overflow from 22KB QueryResults struct.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteWithSubQueries_ToList()
    {
        // Check for multiple subqueries FIRST with minimal stack usage
        // Accessing SubQueryCount via cached pattern avoids copying large struct
        if (_cachedPattern.SubQueryCount > 1)
        {
            // Collect results from all subqueries and join them
            var joinedResults = CollectAndJoinSubQueryResults();
            if (joinedResults == null || joinedResults.Count == 0)
                return null;

            // If there are outer triple patterns, join with them too
            ref readonly var pattern = ref _cachedPattern;
            if (pattern.RequiredPatternCount > 0)
            {
                joinedResults = JoinWithOuterPatterns(joinedResults);
            }
            return joinedResults;
        }

        // Single subquery with outer patterns - delegate to join method directly
        if (_cachedPattern.PatternCount > 0)
        {
            ref readonly var pattern = ref _cachedPattern;
            var subSelect = pattern.GetSubQuery(0);
            return ExecuteSubQueryJoinCore(subSelect);
        }

        // Single subquery without outer patterns
        {
            ref readonly var pattern = ref _cachedPattern;
            var subSelect = pattern.GetSubQuery(0);
            return ExecuteSubQuerySimpleCore(subSelect);
        }
    }

    /// <summary>
    /// Execute a query that contains subqueries (legacy entry point).
    /// Creates QueryResults from materialized list at the end to avoid stack overflow.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteWithSubQueries()
    {
        // Collect all results as a list first (returns reference, not 22KB struct)
        var results = ExecuteWithSubQueries_ToList();

        // Create QueryResults ONCE at the end
        if (results == null || results.Count == 0)
            return QueryResults.Empty();

        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;

        // Pass outer query's clauses to enable aggregation (GROUP BY, HAVING, ORDER BY)
        // This is critical for queries like: SELECT (COUNT(*) AS ?c) { subquery FILTER(...) }
        return QueryResults.FromMaterializedSimple(results, _source.AsSpan(), _store, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct,
            _buffer.GetOrderByClause(), _buffer.GetGroupByClause(), _buffer.GetSelectClause(),
            _buffer.GetHavingClause());
    }

    /// <summary>
    /// Collect results from all subqueries and join them.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? CollectAndJoinSubQueryResults()
    {
        ref readonly var pattern = ref _cachedPattern;
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
        ref readonly var pattern = ref _cachedPattern;
        var subSelect = pattern.GetSubQuery(subQueryIndex);

        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        var results = new List<MaterializedRow>();
        var subQueryScan = new SubQueryScan(_store, _source, subSelect, _prefixMappings, _namedGraphs);
        try
        {
            while (subQueryScan.MoveNext(ref bindingTable))
            {
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
        ref readonly var pattern = ref _cachedPattern;
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

        // Pass outer query's clauses to enable aggregation (GROUP BY, HAVING, ORDER BY)
        return QueryResults.FromMaterializedSimple(results, _source.AsSpan(), _store, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct,
            _buffer.GetOrderByClause(), _buffer.GetGroupByClause(), _buffer.GetSelectClause(),
            _buffer.GetHavingClause());
    }

    /// <summary>
    /// Core execution of subquery join. Uses instance fields to avoid copying large structs.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteSubQueryJoinCore(SubSelect subSelect)
    {
        // Access pattern via ref to avoid copying ~4KB struct
        ref readonly var pattern = ref _cachedPattern;

        var results = new List<MaterializedRow>();
        var joinBindings = new Binding[16];
        var bindingTable = new BindingTable(joinBindings, _stringBuffer);

        var hasFilters = pattern.FilterCount > 0;
        var hasExists = pattern.HasExists;

        // Note: SubQueryJoinScan still takes pattern by value.
        // This is acceptable since we're only copying once (not recursively).
        var joinScan = new SubQueryJoinScan(_store, _source, pattern, subSelect, _prefixMappings);
        try
        {
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

                // Apply EXISTS/NOT EXISTS filters
                if (hasExists)
                {
                    bool passesExists = true;
                    for (int i = 0; i < pattern.ExistsFilterCount; i++)
                    {
                        var existsFilter = pattern.GetExistsFilter(i);
                        var matches = EvaluateExistsFilterWithBindings(existsFilter, bindingTable);

                        // EXISTS: must match at least once
                        // NOT EXISTS: must not match at all
                        if (existsFilter.Negated)
                        {
                            if (matches) { passesExists = false; break; } // NOT EXISTS failed - found a match
                        }
                        else
                        {
                            if (!matches) { passesExists = false; break; } // EXISTS failed - no match found
                        }
                    }
                    if (!passesExists)
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

    /// <summary>
    /// Evaluate an EXISTS filter using current bindings.
    /// Returns true if ALL patterns in the EXISTS filter match.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateExistsFilterWithBindings(ExistsFilter existsFilter, BindingTable bindingTable)
    {
        if (!existsFilter.HasPatterns)
            return false;

        // For each pattern in EXISTS, substitute bound variables and query the store
        // All patterns must match for EXISTS to succeed (conjunction)
        for (int p = 0; p < existsFilter.PatternCount; p++)
        {
            var tp = existsFilter.GetPattern(p);

            // Resolve terms - use bound values for variables
            var subject = ResolveExistsTermFromBindings(tp.Subject, bindingTable);
            var predicate = ResolveExistsTermFromBindings(tp.Predicate, bindingTable);
            var obj = ResolveExistsTermFromBindings(tp.Object, bindingTable);

            // Query the store
            var queryResults = _store.QueryCurrent(subject, predicate, obj);
            try
            {
                if (!queryResults.MoveNext())
                    return false; // No match for this pattern
            }
            finally
            {
                queryResults.Dispose();
            }
        }

        return true; // All patterns matched
    }

    /// <summary>
    /// Resolve a term for EXISTS evaluation, substituting bound variables.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private ReadOnlySpan<char> ResolveExistsTermFromBindings(Term term, BindingTable bindingTable)
    {
        var termSpan = _source.AsSpan(term.Start, term.Length);

        if (term.IsVariable)
        {
            // Look up variable in bindings
            var idx = bindingTable.FindBinding(termSpan);
            if (idx >= 0)
            {
                return bindingTable.GetString(idx);
            }
            // Unbound variable - use wildcard
            return ReadOnlySpan<char>.Empty;
        }

        // Handle 'a' shorthand for rdf:type (SPARQL keyword)
        if (termSpan.Length == 1 && termSpan[0] == 'a')
        {
            return SyntheticTermHelper.RdfType.AsSpan();
        }

        // Handle numeric literals (integer or decimal)
        if (IsNumericLiteral(termSpan))
        {
            _existsExpandedTerm = ExpandNumericLiteral(termSpan);
            return _existsExpandedTerm.AsSpan();
        }

        // For constants, expand prefixed names if needed
        if (_prefixMappings != null && termSpan.Length > 0 &&
            termSpan[0] != '<' && termSpan[0] != '"')
        {
            var colonIdx = termSpan.IndexOf(':');
            if (colonIdx >= 0)
            {
                var prefix = termSpan.Slice(0, colonIdx + 1);
                var localName = termSpan.Slice(colonIdx + 1);

                foreach (var mapping in _prefixMappings)
                {
                    var mappedPrefix = _source.AsSpan(mapping.PrefixStart, mapping.PrefixLength);

                    if (prefix.SequenceEqual(mappedPrefix))
                    {
                        var iriNs = _source.AsSpan(mapping.IriStart, mapping.IriLength);
                        var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);

                        // Build expanded IRI
                        _existsExpandedTerm = string.Concat(nsWithoutClose, localName, ">");
                        return _existsExpandedTerm.AsSpan();
                    }
                }
            }
        }

        return termSpan;
    }

    // Temporary storage for expanded EXISTS terms
    private string? _existsExpandedTerm;

    /// <summary>
    /// Check if a term is a numeric literal (integer or decimal).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNumericLiteral(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty) return false;

        var first = term[0];
        // Numeric literals start with digit, +, or -
        if (first == '+' || first == '-')
        {
            return term.Length > 1 && char.IsDigit(term[1]);
        }
        return char.IsDigit(first);
    }

    /// <summary>
    /// Expand a numeric literal to its typed RDF form.
    /// </summary>
    private static string ExpandNumericLiteral(ReadOnlySpan<char> term)
    {
        var hasDecimal = term.Contains('.') || term.Contains('e') || term.Contains('E');
        if (hasDecimal)
        {
            return string.Concat("\"", term, "\"^^<http://www.w3.org/2001/XMLSchema#decimal>");
        }
        return string.Concat("\"", term, "\"^^<http://www.w3.org/2001/XMLSchema#integer>");
    }
}
