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
    /// Returns List instead of QueryResults to avoid ~22KB return value on stack.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteGraphClausesToList()
    {
        // Get patterns from buffer - this returns a ref struct view (16 bytes)
        var patterns = _buffer.GetPatterns();
        var graphCount = _buffer.GraphClauseCount;

        // Multiple GRAPH clauses - join results
        if (graphCount > 1)
        {
            return CollectAndJoinGraphResultsSlotBased(patterns);
        }

        // Single GRAPH clause - check if variable or fixed
        if (_buffer.FirstGraphIsVariable)
        {
            if (_buffer.FirstGraphPatternCount == 0)
                return null;

            // Variable graph execution
            return ExecuteVariableGraphSlotBased(patterns);
        }

        // Fixed IRI graph execution
        return ExecuteFixedGraphSlotBasedList(patterns);
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
    /// Execute patterns using iterative nested loop join.
    /// Uses explicit state management instead of recursion to avoid stack overflow.
    /// Supports up to 12 pattern levels (matching _maxJoinDepth).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteNestedLoopJoin(
        TriplePattern[] patterns, int patternCount, int patternIndex,
        ref BindingTable bindingTable, string? graph, List<MaterializedRow> results)
    {
        // Early exit for empty pattern set
        if (patternIndex >= patternCount)
        {
            results.Add(new MaterializedRow(bindingTable));
            return;
        }

        // Inline scan storage (ref structs can't be in arrays/collections)
        // Using 12 slots to match MaxJoinDepth constant
        TriplePatternScan scan0 = default, scan1 = default, scan2 = default, scan3 = default;
        TriplePatternScan scan4 = default, scan5 = default, scan6 = default, scan7 = default;
        TriplePatternScan scan8 = default, scan9 = default, scan10 = default, scan11 = default;
        bool init0 = false, init1 = false, init2 = false, init3 = false;
        bool init4 = false, init5 = false, init6 = false, init7 = false;
        bool init8 = false, init9 = false, init10 = false, init11 = false;

        var graphSpan = graph.AsSpan();
        int level = patternIndex;

        try
        {
            while (level >= patternIndex)
            {
                // All patterns matched - emit result and backtrack
                if (level >= patternCount)
                {
                    results.Add(new MaterializedRow(bindingTable));
                    level--;
                    continue;
                }

                // Get/create scan for current level and try to advance
                bool advanced = level switch
                {
                    0 => AdvanceScan(ref scan0, ref init0, patterns[0], ref bindingTable, graphSpan),
                    1 => AdvanceScan(ref scan1, ref init1, patterns[1], ref bindingTable, graphSpan),
                    2 => AdvanceScan(ref scan2, ref init2, patterns[2], ref bindingTable, graphSpan),
                    3 => AdvanceScan(ref scan3, ref init3, patterns[3], ref bindingTable, graphSpan),
                    4 => AdvanceScan(ref scan4, ref init4, patterns[4], ref bindingTable, graphSpan),
                    5 => AdvanceScan(ref scan5, ref init5, patterns[5], ref bindingTable, graphSpan),
                    6 => AdvanceScan(ref scan6, ref init6, patterns[6], ref bindingTable, graphSpan),
                    7 => AdvanceScan(ref scan7, ref init7, patterns[7], ref bindingTable, graphSpan),
                    8 => AdvanceScan(ref scan8, ref init8, patterns[8], ref bindingTable, graphSpan),
                    9 => AdvanceScan(ref scan9, ref init9, patterns[9], ref bindingTable, graphSpan),
                    10 => AdvanceScan(ref scan10, ref init10, patterns[10], ref bindingTable, graphSpan),
                    11 => AdvanceScan(ref scan11, ref init11, patterns[11], ref bindingTable, graphSpan),
                    _ => throw new System.InvalidOperationException($"Join depth {level} exceeds maximum of 12")
                };

                if (advanced)
                {
                    // Move to next pattern level
                    level++;
                }
                else
                {
                    // Exhausted at this level - dispose and backtrack
                    DisposeScanAtLevel(level,
                        ref scan0, ref scan1, ref scan2, ref scan3,
                        ref scan4, ref scan5, ref scan6, ref scan7,
                        ref scan8, ref scan9, ref scan10, ref scan11,
                        ref init0, ref init1, ref init2, ref init3,
                        ref init4, ref init5, ref init6, ref init7,
                        ref init8, ref init9, ref init10, ref init11);
                    level--;
                }
            }
        }
        finally
        {
            // Dispose any remaining initialized scans
            if (init0) scan0.Dispose();
            if (init1) scan1.Dispose();
            if (init2) scan2.Dispose();
            if (init3) scan3.Dispose();
            if (init4) scan4.Dispose();
            if (init5) scan5.Dispose();
            if (init6) scan6.Dispose();
            if (init7) scan7.Dispose();
            if (init8) scan8.Dispose();
            if (init9) scan9.Dispose();
            if (init10) scan10.Dispose();
            if (init11) scan11.Dispose();
        }
    }

    /// <summary>
    /// Initialize scan if needed and advance it. Returns true if advanced successfully.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool AdvanceScan(
        ref TriplePatternScan scan, ref bool initialized,
        TriplePattern pattern, ref BindingTable bindingTable, ReadOnlySpan<char> graph)
    {
        if (!initialized)
        {
            scan = new TriplePatternScan(_store, _source, pattern, bindingTable, graph,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd);
            initialized = true;
        }
        return scan.MoveNext(ref bindingTable);
    }

    /// <summary>
    /// Dispose scan at specified level and mark as uninitialized.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void DisposeScanAtLevel(int level,
        ref TriplePatternScan scan0, ref TriplePatternScan scan1, ref TriplePatternScan scan2, ref TriplePatternScan scan3,
        ref TriplePatternScan scan4, ref TriplePatternScan scan5, ref TriplePatternScan scan6, ref TriplePatternScan scan7,
        ref TriplePatternScan scan8, ref TriplePatternScan scan9, ref TriplePatternScan scan10, ref TriplePatternScan scan11,
        ref bool init0, ref bool init1, ref bool init2, ref bool init3,
        ref bool init4, ref bool init5, ref bool init6, ref bool init7,
        ref bool init8, ref bool init9, ref bool init10, ref bool init11)
    {
        switch (level)
        {
            case 0: if (init0) { scan0.Dispose(); init0 = false; } break;
            case 1: if (init1) { scan1.Dispose(); init1 = false; } break;
            case 2: if (init2) { scan2.Dispose(); init2 = false; } break;
            case 3: if (init3) { scan3.Dispose(); init3 = false; } break;
            case 4: if (init4) { scan4.Dispose(); init4 = false; } break;
            case 5: if (init5) { scan5.Dispose(); init5 = false; } break;
            case 6: if (init6) { scan6.Dispose(); init6 = false; } break;
            case 7: if (init7) { scan7.Dispose(); init7 = false; } break;
            case 8: if (init8) { scan8.Dispose(); init8 = false; } break;
            case 9: if (init9) { scan9.Dispose(); init9 = false; } break;
            case 10: if (init10) { scan10.Dispose(); init10 = false; } break;
            case 11: if (init11) { scan11.Dispose(); init11 = false; } break;
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

        // Get the graph IRI for EXISTS/MINUS evaluation context
        ref readonly var pattern = ref _cachedPattern;
        var graphClause = pattern.GetGraphClause(0);
        var graphIri = _source.Substring(graphClause.Graph.Start, graphClause.Graph.Length);

        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;

        // Use FromMaterializedWithGraphContext to enable EXISTS/MINUS filters to query the correct graph
        return QueryResults.FromMaterializedWithGraphContext(results, _buffer, _source.AsSpan(), _store, bindings, stringBuffer,
            graphIri, _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Core fixed graph execution - materializes results on thread with larger stack.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteFixedGraphClauseCore()
    {
        var store = _store;
        var source = _source;

        // Access cached pattern (from buffer)
        ref readonly var pattern = ref _cachedPattern;
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
