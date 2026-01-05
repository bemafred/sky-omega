using System.Collections.Generic;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// GRAPH clause execution methods.
/// Handles queries with GRAPH patterns for named graph access.
/// </summary>
public partial class QueryExecutor
{
    /// <summary>
    /// Execute a GRAPH-only query and return lightweight materialized results.
    /// Use this for queries with GRAPH clauses to avoid stack overflow from large QueryResults struct.
    /// The returned MaterializedQueryResults is ~200 bytes vs ~22KB for full QueryResults.
    /// Caller must hold read lock on store.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal MaterializedQueryResults ExecuteGraphToMaterialized()
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
    private List<MaterializedRow>? CollectAndJoinGraphResultsSlotBased(PatternArray patterns)
    {
        List<MaterializedRow>? joinedResults = null;
        var graphHeaders = patterns.EnumerateGraphHeaders();

        while (graphHeaders.MoveNext())
        {
            ThrowIfCancellationRequested();
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
    private List<MaterializedRow>? ExecuteVariableGraphSlotBased(PatternArray patterns)
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
        PatternArray patterns, int headerIndex, PatternSlot header)
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
                ThrowIfCancellationRequested();
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
            ThrowIfCancellationRequested();
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
        PatternSlot slot, ref BindingTable bindingTable, string? graph, List<MaterializedRow> results)
    {
        var tp = new TriplePattern
        {
            Subject = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength },
            Predicate = new Term { Type = slot.PredicateType, Start = slot.PredicateStart, Length = slot.PredicateLength },
            Object = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength }
        };

        var scan = new TriplePatternScan(_store, _source, tp, bindingTable, graph.AsSpan(),
            _temporalMode, _asOfTime, _rangeStart, _rangeEnd);
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                results.Add(new MaterializedRow(bindingTable));
            }
        }
        finally
        {
            scan.Dispose();
        }
    }

    /// <summary>
    /// Execute fixed graph clause using PatternSlot views (GRAPH &lt;iri&gt; { ... }).
    /// Returns List to avoid stack overflow from returning large QueryResults by value.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteFixedGraphSlotBasedList(PatternArray patterns)
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
        PatternArray patterns, int headerIndex, PatternSlot header)
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
        PatternArraySlice children,
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

        // Check join depth limit before starting recursive join
        if (patternCount > _maxJoinDepth)
            throw new System.InvalidOperationException(
                $"Query exceeds maximum join depth ({patternCount} patterns, limit is {_maxJoinDepth})");

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
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                // Check for cancellation periodically (on each row)
                ThrowIfCancellationRequested();

                // Recursively match remaining patterns with current bindings
                ExecuteNestedLoopJoin(patterns, patternCount, patternIndex + 1, ref bindingTable, graph, results);
            }
        }
        finally
        {
            scan.Dispose();
        }
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
        QuadStore store, string source, QueryBuffer buffer, string[]? namedGraphs)
    {
        var bindings = new Binding[16];
        var stringBuffer = PooledBufferManager.Shared.Rent<char>(1024).Array!;
        try
        {
            // Find the GRAPH header slot in the buffer
            var patterns = buffer.GetPatterns();
            int graphHeaderIdx = -1;
            for (int i = 0; i < buffer.PatternCount; i++)
            {
                if (patterns[i].Kind == PatternKind.GraphHeader)
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
        finally
        {
            PooledBufferManager.Shared.Return(stringBuffer);
        }
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

        // Multiple patterns - copy to heap array and execute in isolated stack frame
        var patterns = new TriplePattern[patternCount];
        for (int i = 0; i < patternCount; i++)
        {
            patterns[i] = graphClause.GetPattern(i);
        }

        return ExecuteFixedGraphMultiPatterns(store, source, patterns, graphStart, graphLength);
    }

    /// <summary>
    /// Execute single pattern in fixed graph - no thread needed, small stack frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<MaterializedRow> ExecuteFixedGraphSinglePattern(
        QuadStore store, string source, TriplePattern tp, int graphStart, int graphLength)
    {
        var bindings = new Binding[16];
        var stringBuffer = PooledBufferManager.Shared.Rent<char>(1024).Array!;
        var bindingTable = new BindingTable(bindings, stringBuffer);
        var graphIri = source.AsSpan(graphStart, graphLength);

        var scan = new TriplePatternScan(store, source, tp, bindingTable, graphIri);
        var results = new List<MaterializedRow>();
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                results.Add(new MaterializedRow(bindingTable));
                bindingTable.Clear();
            }
        }
        finally
        {
            scan.Dispose();
            PooledBufferManager.Shared.Return(stringBuffer);
        }

        return results;
    }

    /// <summary>
    /// Execute multiple patterns in fixed graph - isolated stack frame via NoInlining.
    /// Patterns are passed via heap-allocated array to avoid large struct copies.
    /// </summary>
    /// <remarks>
    /// ADR-009: This method replaces the previous Thread-based workaround.
    /// The [NoInlining] attribute prevents stack frame merging, keeping
    /// the GraphPattern (~4KB) isolated to this frame only.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<MaterializedRow> ExecuteFixedGraphMultiPatterns(
        QuadStore store, string source, TriplePattern[] patterns, int graphStart, int graphLength)
    {
        var bindings = new Binding[16];
        var stringBuffer = PooledBufferManager.Shared.Rent<char>(1024).Array!;
        var bindingTable = new BindingTable(bindings, stringBuffer);
        var graphIri = source.AsSpan(graphStart, graphLength);

        // Build GraphPattern from heap-allocated patterns array
        var graphPattern = new GraphPattern();
        for (int i = 0; i < patterns.Length; i++)
        {
            graphPattern.AddPattern(patterns[i]);
        }

        var scan = new MultiPatternScan(store, source, graphPattern, false, graphIri);
        var results = new List<MaterializedRow>();
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                results.Add(new MaterializedRow(bindingTable));
                bindingTable.Clear();
            }
        }
        finally
        {
            scan.Dispose();
            PooledBufferManager.Shared.Return(stringBuffer);
        }

        return results;
    }
}
