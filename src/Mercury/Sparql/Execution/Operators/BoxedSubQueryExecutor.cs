using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// Boxed executor for subqueries that isolates large scan operator stack usage.
/// This class exists to break the ref struct stack chain: when SubQueryScan (ref struct)
/// calls this class's methods, the scan operators are created in a fresh stack frame
/// that doesn't inherit the accumulated ref struct field sizes from the call chain.
/// </summary>
internal sealed class BoxedSubQueryExecutor
{
    private readonly QuadStore _store;
    private readonly string _source;
    private readonly SubSelect _subSelect;
    private readonly bool _distinct;
    private readonly PrefixMapping[]? _prefixes;
    private readonly string? _graphContext;  // Graph context for subqueries inside GRAPH clauses
    private readonly string[]? _namedGraphs;  // FROM NAMED restriction from outer query

    public BoxedSubQueryExecutor(QuadStore store, string source, SubSelect subSelect, PrefixMapping[]? prefixes = null)
        : this(store, source, subSelect, prefixes, null)
    {
    }

    public BoxedSubQueryExecutor(QuadStore store, string source, SubSelect subSelect, PrefixMapping[]? prefixes, string[]? namedGraphs)
    {
        _store = store;
        _source = source;
        _subSelect = subSelect;
        _distinct = subSelect.Distinct;
        _prefixes = prefixes;
        _namedGraphs = namedGraphs;

        // Extract graph context if the subquery is inside a GRAPH clause
        // Expand prefixed names (e.g., :g2 -> <http://example.org/g2>)
        if (subSelect.HasGraphContext && !subSelect.GraphContext.IsVariable)
        {
            var graphTerm = subSelect.GraphContext;
            var rawGraphIri = source.AsSpan(graphTerm.Start, graphTerm.Length);
            _graphContext = ExpandPrefixedName(rawGraphIri, source, prefixes);
        }
    }

    /// <summary>
    /// Expand a prefixed name to its full IRI using prefix mappings.
    /// Returns the original term as a string if not a prefixed name or no matching prefix.
    /// </summary>
    private static string ExpandPrefixedName(ReadOnlySpan<char> term, string source, PrefixMapping[]? prefixes)
    {
        // Skip if already a full IRI, literal, or blank node
        if (term.Length == 0 || term[0] == '<' || term[0] == '"' || term[0] == '_')
            return term.ToString();

        // Handle 'a' shorthand for rdf:type
        if (term.Length == 1 && term[0] == 'a')
            return SyntheticTermHelper.RdfType;

        // Look for colon indicating prefixed name
        var colonIdx = term.IndexOf(':');
        if (colonIdx < 0 || prefixes == null || prefixes.Length == 0)
            return term.ToString();

        // Include the colon in the prefix (stored prefixes include trailing colon, e.g., "ex:")
        var prefixWithColon = term.Slice(0, colonIdx + 1);
        var localPart = term.Slice(colonIdx + 1);

        // Find matching prefix in mappings
        var sourceSpan = source.AsSpan();
        for (int i = 0; i < prefixes.Length; i++)
        {
            var mapping = prefixes[i];
            var mappingPrefix = sourceSpan.Slice(mapping.PrefixStart, mapping.PrefixLength);
            if (prefixWithColon.SequenceEqual(mappingPrefix))
            {
                // Found matching prefix, expand to full IRI
                var iriBase = sourceSpan.Slice(mapping.IriStart, mapping.IriLength);

                // Strip angle brackets from IRI base if present
                var iriContent = iriBase;
                if (iriContent.Length >= 2 && iriContent[0] == '<' && iriContent[^1] == '>')
                    iriContent = iriContent.Slice(1, iriContent.Length - 2);

                // Build full IRI: <base + localPart>
                return $"<{iriContent.ToString()}{localPart.ToString()}>";
            }
        }

        return term.ToString();
    }

    /// <summary>
    /// Execute the subquery and return materialized results.
    /// This method creates scan operators locally - their stack usage is isolated.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public List<MaterializedRow> Execute()
    {
        // Handle UNION with mixed GRAPH/default patterns
        // Some UNION branches have GRAPH ?var { }, others don't
        // Need to execute non-GRAPH branches against default graph, GRAPH branches against named graphs
        if (_subSelect.HasMixedGraphUnion)
        {
            return ExecuteWithMixedGraphUnion();
        }

        // Handle variable graph context (GRAPH ?g { subquery })
        // When graph context is a variable, we need to iterate over all named graphs
        if (_subSelect.HasGraphContext && _subSelect.GraphContext.IsVariable)
        {
            return ExecuteWithVariableGraphContext();
        }

        var results = new List<MaterializedRow>();
        var innerBindings = new Binding[16];
        var innerStringBuffer = PooledBufferManager.Shared.Rent<char>(512).Array!;
        var bindingTable = new BindingTable(innerBindings, innerStringBuffer);
        HashSet<int>? seenHashes = _distinct ? new HashSet<int>() : null;

        try
        {
            // If subquery has REAL aggregates (COUNT, SUM, etc.), use aggregation path
            // Non-aggregate computed expressions (CONCAT, STR, etc.) are handled in ProcessAndAddResult
            if (_subSelect.HasRealAggregates)
            {
                return ExecuteWithAggregation(ref bindingTable);
            }

            // Handle nested subqueries (subquery within subquery)
            if (_subSelect.HasSubQueries && _subSelect.PatternCount == 0)
            {
                return ExecuteNestedSubQueries(ref bindingTable);
            }

            int skipped = 0;
            int returned = 0;

            if (_subSelect.PatternCount == 1)
            {
                MaterializeSinglePattern(ref bindingTable, results, seenHashes, ref skipped, ref returned);
            }
            else
            {
                MaterializeMultiPattern(ref bindingTable, results, seenHashes, ref skipped, ref returned);
            }
        }
        finally
        {
            PooledBufferManager.Shared.Return(innerStringBuffer);
        }

        return results;
    }

    /// <summary>
    /// Execute subquery with variable graph context (GRAPH ?g { subquery }).
    /// Iterates over all named graphs and executes the subquery against each.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteWithVariableGraphContext()
    {
        // If subquery has real aggregates (COUNT, SUM, etc.), use per-graph aggregation path
        // This handles cases like: GRAPH ?g { SELECT (count(*) AS ?c) WHERE { ?s :p ?x } }
        if (_subSelect.HasRealAggregates)
        {
            return ExecuteWithAggregationPerGraph();
        }

        var results = new List<MaterializedRow>();
        var innerBindings = new Binding[16];
        var innerStringBuffer = PooledBufferManager.Shared.Rent<char>(512).Array!;
        var bindingTable = new BindingTable(innerBindings, innerStringBuffer);
        HashSet<int>? seenHashes = _distinct ? new HashSet<int>() : null;

        // Get the graph variable name from source
        var graphTerm = _subSelect.GraphContext;
        var graphVarName = _source.AsSpan(graphTerm.Start, graphTerm.Length);
        var graphVarNameStr = graphVarName.ToString();

        try
        {
            int skipped = 0;
            int returned = 0;

            // Iterate over named graphs - use FROM NAMED restriction if provided, otherwise all store graphs
            if (_namedGraphs != null && _namedGraphs.Length > 0)
            {
                // FROM NAMED specified - iterate those graphs (may include empty graphs)
                foreach (var graphIriStr in _namedGraphs)
                {
                    // Execute patterns against this graph
                    if (_subSelect.PatternCount == 1)
                    {
                        MaterializeSinglePatternWithGraph(ref bindingTable, results, seenHashes,
                            ref skipped, ref returned, graphIriStr, graphVarNameStr);
                    }
                    else
                    {
                        MaterializeMultiPatternWithGraph(ref bindingTable, results, seenHashes,
                            ref skipped, ref returned, graphIriStr, graphVarNameStr);
                    }

                    // Check limit
                    if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                        break;
                }
            }
            else
            {
                // No FROM NAMED restriction - iterate all named graphs from store
                var graphEnum = _store.GetNamedGraphs();
                while (graphEnum.MoveNext())
                {
                    var graphIri = graphEnum.Current;
                    var graphIriStr = graphIri.ToString();

                    // Execute patterns against this graph
                    if (_subSelect.PatternCount == 1)
                    {
                        MaterializeSinglePatternWithGraph(ref bindingTable, results, seenHashes,
                            ref skipped, ref returned, graphIriStr, graphVarNameStr);
                    }
                    else
                    {
                        MaterializeMultiPatternWithGraph(ref bindingTable, results, seenHashes,
                            ref skipped, ref returned, graphIriStr, graphVarNameStr);
                    }

                    // Check limit
                    if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                        break;
                }
            }
        }
        finally
        {
            PooledBufferManager.Shared.Return(innerStringBuffer);
        }

        return results;
    }

    /// <summary>
    /// Execute subquery with aggregation for each named graph (GRAPH ?g { SELECT (count(*) AS ?c) ... }).
    /// For each named graph, compute aggregates separately, including empty-group handling.
    /// This ensures that even graphs with 0 matches return a result with count=0.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteWithAggregationPerGraph()
    {
        var results = new List<MaterializedRow>();
        var innerBindings = new Binding[16];
        var innerStringBuffer = PooledBufferManager.Shared.Rent<char>(512).Array!;
        var bindingTable = new BindingTable(innerBindings, innerStringBuffer);
        var source = _source;

        // Get the graph variable name from source
        var graphTerm = _subSelect.GraphContext;
        var graphVarName = source.AsSpan(graphTerm.Start, graphTerm.Length);
        var graphVarNameStr = graphVarName.ToString();

        try
        {
            // Iterate over named graphs - use FROM NAMED restriction if provided, otherwise all store graphs
            IEnumerable<string> graphIris = _namedGraphs != null && _namedGraphs.Length > 0
                ? _namedGraphs
                : CollectNamedGraphs();

            foreach (var graphIriStr in graphIris)
            {
                // Collect raw rows for this graph only
                var groups = new Dictionary<string, SubQueryGroupedRow>();

                if (_subSelect.PatternCount == 1)
                {
                    CollectRawResultsSingleWithGraph(ref bindingTable, groups, source, graphIriStr);
                }
                else
                {
                    CollectRawResultsMultiWithGraph(ref bindingTable, groups, source, graphIriStr);
                }

                // Handle implicit aggregation with empty result set for this graph:
                // When there are aggregates but no GROUP BY and no matching rows,
                // SPARQL requires returning one row with default aggregate values (e.g., COUNT=0)
                if (groups.Count == 0 && _subSelect.HasAggregates && !_subSelect.HasGroupBy)
                {
                    bindingTable.Clear();
                    var emptyGroup = new SubQueryGroupedRow(_subSelect, bindingTable, source);
                    groups[""] = emptyGroup;
                }

                // Finalize aggregates and add to results with graph binding
                foreach (var group in groups.Values)
                {
                    group.FinalizeAggregates();
                    var row = group.ToMaterializedRowWithGraphBinding(graphVarNameStr, graphIriStr);
                    results.Add(row);
                }
            }
        }
        finally
        {
            PooledBufferManager.Shared.Return(innerStringBuffer);
        }

        // Apply LIMIT/OFFSET if specified
        if (_subSelect.Offset > 0 || _subSelect.Limit > 0)
        {
            int skip = _subSelect.Offset;
            int take = _subSelect.Limit > 0 ? _subSelect.Limit : results.Count;
            if (skip >= results.Count)
            {
                results.Clear();
            }
            else
            {
                int available = results.Count - skip;
                int actualTake = Math.Min(take, available);
                results = results.GetRange(skip, actualTake);
            }
        }

        return results;
    }

    /// <summary>
    /// Helper to collect named graphs from the store as a list of IRI strings.
    /// Cannot use yield return with ref struct NamedGraphEnumerator.
    /// </summary>
    private List<string> CollectNamedGraphs()
    {
        var result = new List<string>();
        var graphEnum = _store.GetNamedGraphs();
        while (graphEnum.MoveNext())
        {
            result.Add(graphEnum.Current.ToString());
        }
        return result;
    }

    /// <summary>
    /// Collect raw results from single pattern for aggregation, against a specific graph.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectRawResultsSingleWithGraph(ref BindingTable bindingTable,
        Dictionary<string, SubQueryGroupedRow> groups, string source, string graphIri)
    {
        var tp = _subSelect.GetPattern(0);
        var sourceSpan = source.AsSpan();
        var graphSpan = graphIri.AsSpan();
        var scan = new TriplePatternScan(_store, sourceSpan, tp, bindingTable, graphSpan,
            TemporalQueryMode.Current, default, default, default, _prefixes);
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                // Apply filters if any
                if (_subSelect.FilterCount > 0)
                {
                    bool passedFilters = true;
                    for (int i = 0; i < _subSelect.FilterCount; i++)
                    {
                        var filter = _subSelect.GetFilter(i);
                        var filterExpr = sourceSpan.Slice(filter.Start, filter.Length);
                        var evaluator = new FilterEvaluator(filterExpr);
                        if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
                        {
                            passedFilters = false;
                            break;
                        }
                    }
                    if (!passedFilters)
                    {
                        bindingTable.Clear();
                        continue;
                    }
                }

                // Build group key from GROUP BY variables
                var groupKey = BuildGroupKey(ref bindingTable, source);

                // Get or create group
                if (!groups.TryGetValue(groupKey, out var group))
                {
                    group = new SubQueryGroupedRow(_subSelect, bindingTable, source);
                    groups[groupKey] = group;
                }

                // Accumulate aggregate values
                group.UpdateAggregates(bindingTable, source);

                bindingTable.Clear();
            }
        }
        finally
        {
            scan.Dispose();
        }
    }

    /// <summary>
    /// Collect raw results from multiple patterns for aggregation, against a specific graph.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectRawResultsMultiWithGraph(ref BindingTable bindingTable,
        Dictionary<string, SubQueryGroupedRow> groups, string source, string graphIri)
    {
        var graphSpan = graphIri.AsSpan();
        var sourceSpan = source.AsSpan();

        // Build pattern for multi-pattern scan
        var graphPattern = new GraphPattern();
        for (int i = 0; i < _subSelect.PatternCount; i++)
        {
            graphPattern.AddPattern(_subSelect.GetPattern(i));
        }

        var scan = new MultiPatternScan(_store, source, graphPattern, false, graphSpan, prefixes: _prefixes);
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                // Apply filters if any
                if (_subSelect.FilterCount > 0)
                {
                    bool passedFilters = true;
                    for (int i = 0; i < _subSelect.FilterCount; i++)
                    {
                        var filter = _subSelect.GetFilter(i);
                        var filterExpr = sourceSpan.Slice(filter.Start, filter.Length);
                        var evaluator = new FilterEvaluator(filterExpr);
                        if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
                        {
                            passedFilters = false;
                            break;
                        }
                    }
                    if (!passedFilters)
                    {
                        bindingTable.Clear();
                        continue;
                    }
                }

                // Build group key from GROUP BY variables
                var groupKey = BuildGroupKey(ref bindingTable, source);

                // Get or create group
                if (!groups.TryGetValue(groupKey, out var group))
                {
                    group = new SubQueryGroupedRow(_subSelect, bindingTable, source);
                    groups[groupKey] = group;
                }

                // Accumulate aggregate values
                group.UpdateAggregates(bindingTable, source);

                bindingTable.Clear();
            }
        }
        finally
        {
            scan.Dispose();
        }
    }

    /// <summary>
    /// Execute subquery with mixed GRAPH/default patterns in UNION.
    /// Some UNION branches are inside GRAPH ?var { }, others query the default graph.
    /// Example: { ?s ?p ?o } UNION { GRAPH ?g { ?s ?p ?o } }
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteWithMixedGraphUnion()
    {
        var results = new List<MaterializedRow>();
        var innerBindings = new Binding[16];
        var innerStringBuffer = PooledBufferManager.Shared.Rent<char>(512).Array!;
        var bindingTable = new BindingTable(innerBindings, innerStringBuffer);
        HashSet<int>? seenHashes = _distinct ? new HashSet<int>() : null;

        // Get the graph variable name for GRAPH patterns
        var graphTerm = _subSelect.GraphContext;
        var graphVarName = _source.AsSpan(graphTerm.Start, graphTerm.Length);
        var graphVarNameStr = graphVarName.ToString();

        try
        {
            int skipped = 0;
            int returned = 0;
            var source = _source.AsSpan();

            // Execute first branch (patterns before UNION) against default graph
            // These patterns are NOT inside GRAPH blocks
            if (_subSelect.FirstBranchPatternCount > 0)
            {
                for (int i = 0; i < _subSelect.FirstBranchPatternCount; i++)
                {
                    if (_subSelect.IsPatternInGraphBlock(i))
                        continue; // Skip GRAPH patterns in first branch (handled below)

                    var tp = _subSelect.GetPattern(i);
                    var scan = new TriplePatternScan(_store, source, tp, bindingTable, default,
                        TemporalQueryMode.Current, default, default, default, _prefixes);
                    try
                    {
                        while (scan.MoveNext(ref bindingTable))
                        {
                            if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                            {
                                if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                                    return results;
                            }
                            bindingTable.Clear();
                        }
                    }
                    finally
                    {
                        scan.Dispose();
                    }
                }
            }

            // Execute UNION branches - split by GRAPH vs default graph
            for (int i = _subSelect.UnionStartIndex; i < _subSelect.PatternCount; i++)
            {
                var tp = _subSelect.GetPattern(i);

                if (_subSelect.IsPatternInGraphBlock(i))
                {
                    // GRAPH pattern - execute against each named graph
                    var graphEnum = _store.GetNamedGraphs();
                    while (graphEnum.MoveNext())
                    {
                        var graphIri = graphEnum.Current;
                        var graphIriStr = graphIri.ToString();
                        var graphSpan = graphIriStr.AsSpan();

                        var scan = new TriplePatternScan(_store, source, tp, bindingTable, graphSpan,
                            TemporalQueryMode.Current, default, default, default, _prefixes);
                        try
                        {
                            while (scan.MoveNext(ref bindingTable))
                            {
                                // Bind the graph variable if projected
                                if (IsVariableProjected(graphVarNameStr))
                                {
                                    bindingTable.Bind(graphVarName, graphIriStr.AsSpan());
                                }

                                if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                                {
                                    if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                                        return results;
                                }
                                bindingTable.Clear();
                            }
                        }
                        finally
                        {
                            scan.Dispose();
                        }
                    }
                }
                else
                {
                    // Default graph pattern
                    var scan = new TriplePatternScan(_store, source, tp, bindingTable, default,
                        TemporalQueryMode.Current, default, default, default, _prefixes);
                    try
                    {
                        while (scan.MoveNext(ref bindingTable))
                        {
                            if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                            {
                                if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                                    return results;
                            }
                            bindingTable.Clear();
                        }
                    }
                    finally
                    {
                        scan.Dispose();
                    }
                }
            }
        }
        finally
        {
            PooledBufferManager.Shared.Return(innerStringBuffer);
        }

        return results;
    }

    /// <summary>
    /// Check if a variable is projected by the subquery (either SELECT * or explicitly listed).
    /// </summary>
    private bool IsVariableProjected(string varName)
    {
        // SELECT * projects all variables
        if (_subSelect.SelectAll)
            return true;

        // Check explicitly projected variables
        var source = _source.AsSpan();
        for (int i = 0; i < _subSelect.ProjectedVarCount; i++)
        {
            var (start, len) = _subSelect.GetProjectedVariable(i);
            var projectedVar = source.Slice(start, len);
            if (projectedVar.SequenceEqual(varName.AsSpan()))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Materialize single pattern results for a specific graph, binding the graph variable.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MaterializeSinglePatternWithGraph(ref BindingTable bindingTable, List<MaterializedRow> results,
        HashSet<int>? seenHashes, ref int skipped, ref int returned, string graphIri, string graphVarName)
    {
        var tp = _subSelect.GetPattern(0);
        var source = _source.AsSpan();

        // Only pre-bind the graph variable if the subquery projects it (SELECT * or explicit).
        // This enables proper join semantics:
        // - If projected: filter pattern results where ?g doesn't match graphIri (sq02)
        // - If not projected: no filtering, all results pass through (sq03)
        bool preBindGraph = IsVariableProjected(graphVarName);
        int preBindCount = 0;

        if (preBindGraph)
        {
            bindingTable.Bind(graphVarName.AsSpan(), graphIri.AsSpan());
            preBindCount = bindingTable.Count;
        }

        var scan = new TriplePatternScan(_store, source, tp, bindingTable, graphIri.AsSpan(),
            TemporalQueryMode.Current, default, default, default, _prefixes);
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                // Bind graph variable for output (needed for both cases)
                if (!preBindGraph)
                    bindingTable.Bind(graphVarName.AsSpan(), graphIri.AsSpan());

                if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                {
                    if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                        break;
                }

                if (preBindGraph)
                    bindingTable.TruncateTo(preBindCount);  // Keep graph binding, clear pattern bindings
                else
                    bindingTable.Clear();
            }
        }
        finally
        {
            scan.Dispose();
            bindingTable.Clear();  // Clear for next graph iteration
        }
    }

    /// <summary>
    /// Materialize multi-pattern results for a specific graph, binding the graph variable.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MaterializeMultiPatternWithGraph(ref BindingTable bindingTable, List<MaterializedRow> results,
        HashSet<int>? seenHashes, ref int skipped, ref int returned, string graphIri, string graphVarName)
    {
        if (_subSelect.HasUnion)
        {
            // Execute UNION branches with graph context
            MaterializeUnionBranchesWithGraph(ref bindingTable, results, seenHashes,
                ref skipped, ref returned, graphIri, graphVarName);
        }
        else
        {
            // Build pattern on heap
            var boxedPattern = new MultiPatternScan.BoxedPattern();
            for (int i = 0; i < _subSelect.PatternCount; i++)
            {
                boxedPattern.Pattern.AddPattern(_subSelect.GetPattern(i));
            }

            var source = _source.AsSpan();

            // Only pre-bind the graph variable if the subquery projects it
            bool preBindGraph = IsVariableProjected(graphVarName);
            int preBindCount = 0;

            if (preBindGraph)
            {
                bindingTable.Bind(graphVarName.AsSpan(), graphIri.AsSpan());
                preBindCount = bindingTable.Count;
            }

            var scan = new MultiPatternScan(_store, source, boxedPattern.Pattern, false, graphIri.AsSpan());
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    // Bind graph variable for output (needed for both cases)
                    if (!preBindGraph)
                        bindingTable.Bind(graphVarName.AsSpan(), graphIri.AsSpan());

                    if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                    {
                        if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                            break;
                    }

                    if (preBindGraph)
                        bindingTable.TruncateTo(preBindCount);
                    else
                        bindingTable.Clear();
                }
            }
            finally
            {
                scan.Dispose();
                bindingTable.Clear();  // Clear for next graph iteration
            }
        }
    }

    /// <summary>
    /// Materialize UNION branch results for a specific graph, binding the graph variable.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MaterializeUnionBranchesWithGraph(ref BindingTable bindingTable, List<MaterializedRow> results,
        HashSet<int>? seenHashes, ref int skipped, ref int returned, string graphIri, string graphVarName)
    {
        var source = _source.AsSpan();
        var graphSpan = graphIri.AsSpan();

        // Only pre-bind the graph variable if the subquery projects it
        bool preBindGraph = IsVariableProjected(graphVarName);
        int preBindCount = 0;

        if (preBindGraph)
        {
            bindingTable.Bind(graphVarName.AsSpan(), graphSpan);
            preBindCount = bindingTable.Count;
        }

        // Execute first branch
        if (_subSelect.FirstBranchPatternCount > 0)
        {
            if (_subSelect.FirstBranchPatternCount > 1)
            {
                var firstBranch = new MultiPatternScan.BoxedPattern();
                for (int i = 0; i < _subSelect.FirstBranchPatternCount; i++)
                {
                    firstBranch.Pattern.AddPattern(_subSelect.GetPattern(i));
                }

                var firstScan = new MultiPatternScan(_store, source, firstBranch.Pattern, false, graphSpan);
                try
                {
                    while (firstScan.MoveNext(ref bindingTable))
                    {
                        if (!preBindGraph)
                            bindingTable.Bind(graphVarName.AsSpan(), graphSpan);

                        if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                        {
                            if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                            {
                                bindingTable.Clear();
                                return;
                            }
                        }

                        if (preBindGraph)
                            bindingTable.TruncateTo(preBindCount);
                        else
                            bindingTable.Clear();
                    }
                }
                finally
                {
                    firstScan.Dispose();
                }
            }
            else
            {
                var firstTp = _subSelect.GetPattern(0);
                var firstScan = new TriplePatternScan(_store, source, firstTp, bindingTable, graphSpan,
                    TemporalQueryMode.Current, default, default, default, _prefixes);
                try
                {
                    while (firstScan.MoveNext(ref bindingTable))
                    {
                        if (!preBindGraph)
                            bindingTable.Bind(graphVarName.AsSpan(), graphSpan);

                        if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                        {
                            if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                            {
                                bindingTable.Clear();
                                return;
                            }
                        }

                        if (preBindGraph)
                            bindingTable.TruncateTo(preBindCount);
                        else
                            bindingTable.Clear();
                    }
                }
                finally
                {
                    firstScan.Dispose();
                }
            }
        }

        // Execute union branch
        if (_subSelect.UnionBranchPatternCount > 0)
        {
            if (_subSelect.UnionBranchPatternCount > 1)
            {
                var unionBranch = new MultiPatternScan.BoxedPattern();
                for (int i = 0; i < _subSelect.UnionBranchPatternCount; i++)
                {
                    unionBranch.Pattern.AddPattern(_subSelect.GetPattern(_subSelect.UnionStartIndex + i));
                }

                var unionScan = new MultiPatternScan(_store, source, unionBranch.Pattern, false, graphSpan);
                try
                {
                    while (unionScan.MoveNext(ref bindingTable))
                    {
                        if (!preBindGraph)
                            bindingTable.Bind(graphVarName.AsSpan(), graphSpan);

                        if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                        {
                            if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                            {
                                bindingTable.Clear();
                                return;
                            }
                        }

                        if (preBindGraph)
                            bindingTable.TruncateTo(preBindCount);
                        else
                            bindingTable.Clear();
                    }
                }
                finally
                {
                    unionScan.Dispose();
                }
            }
            else
            {
                var unionTp = _subSelect.GetPattern(_subSelect.UnionStartIndex);
                var unionScan = new TriplePatternScan(_store, source, unionTp, bindingTable, graphSpan,
                    TemporalQueryMode.Current, default, default, default, _prefixes);
                try
                {
                    while (unionScan.MoveNext(ref bindingTable))
                    {
                        if (!preBindGraph)
                            bindingTable.Bind(graphVarName.AsSpan(), graphSpan);

                        if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                        {
                            if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                            {
                                bindingTable.Clear();
                                return;
                            }
                        }

                        if (preBindGraph)
                            bindingTable.TruncateTo(preBindCount);
                        else
                            bindingTable.Clear();
                    }
                }
                finally
                {
                    unionScan.Dispose();
                }
            }
        }

        bindingTable.Clear();  // Clear for next graph iteration
    }

    /// <summary>
    /// Execute nested subqueries (subquery containing another subquery but no triple patterns).
    /// Recursively executes nested subqueries and passes results through.
    /// For queries like: SELECT * WHERE { { SELECT ?x WHERE { ... } } }
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteNestedSubQueries(ref BindingTable bindingTable)
    {
        var results = new List<MaterializedRow>();
        var source = _source;

        // Execute all nested subqueries
        for (int i = 0; i < _subSelect.SubQueryCount; i++)
        {
            var nestedSubSelect = _subSelect.GetSubQuery(i);
            // Pass prefix mappings to nested executor for prefix expansion
            var nestedExecutor = new BoxedSubQueryExecutor(_store, source, nestedSubSelect, _prefixes);
            var nestedResults = nestedExecutor.Execute();

            // Pass through all results from nested subquery
            // Projection is already handled by the nested subquery's SELECT clause
            results.AddRange(nestedResults);
        }

        // Apply DISTINCT if specified
        if (_distinct && results.Count > 1)
        {
            var seen = new HashSet<string>();
            var distinctResults = new List<MaterializedRow>();
            foreach (var row in results)
            {
                // Build a key from all values to identify unique rows
                var key = BuildRowKey(row);
                if (seen.Add(key))
                    distinctResults.Add(row);
            }
            return distinctResults;
        }

        // Apply LIMIT/OFFSET if specified
        if (_subSelect.Offset > 0 || _subSelect.Limit > 0)
        {
            int skip = _subSelect.Offset;
            int take = _subSelect.Limit > 0 ? _subSelect.Limit : results.Count;
            if (skip >= results.Count)
            {
                results.Clear();
            }
            else
            {
                int available = results.Count - skip;
                int actualTake = Math.Min(take, available);
                results = results.GetRange(skip, actualTake);
            }
        }

        return results;
    }

    /// <summary>
    /// Build a string key from all binding values in a row for DISTINCT comparison.
    /// </summary>
    private static string BuildRowKey(MaterializedRow row)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < row.BindingCount; i++)
        {
            if (i > 0) sb.Append('\x1F'); // Unit separator
            sb.Append(row.GetValue(i));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Execute subquery with aggregation support.
    /// Collects raw rows, groups them, computes aggregates, and returns aggregated results.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteWithAggregation(ref BindingTable bindingTable)
    {
        var groups = new Dictionary<string, SubQueryGroupedRow>();
        var source = _source;

        // Collect all raw rows
        if (_subSelect.PatternCount == 1)
        {
            CollectRawResultsSingle(ref bindingTable, groups, source);
        }
        else
        {
            CollectRawResultsMulti(ref bindingTable, groups, source);
        }

        // Handle implicit aggregation with empty result set:
        // When there are aggregates but no GROUP BY and no matching rows,
        // SPARQL requires returning one row with default aggregate values
        if (groups.Count == 0 && _subSelect.HasAggregates && !_subSelect.HasGroupBy)
        {
            bindingTable.Clear();
            var emptyGroup = new SubQueryGroupedRow(_subSelect, bindingTable, source);
            groups[""] = emptyGroup;
        }

        // Finalize aggregates and convert to MaterializedRow
        var results = new List<MaterializedRow>(groups.Count);
        foreach (var group in groups.Values)
        {
            group.FinalizeAggregates();
            results.Add(group.ToMaterializedRow());
        }

        // Apply LIMIT/OFFSET if specified
        if (_subSelect.Offset > 0 || _subSelect.Limit > 0)
        {
            int skip = _subSelect.Offset;
            int take = _subSelect.Limit > 0 ? _subSelect.Limit : results.Count;
            if (skip >= results.Count)
            {
                results.Clear();
            }
            else
            {
                int available = results.Count - skip;
                int actualTake = Math.Min(take, available);
                results = results.GetRange(skip, actualTake);
            }
        }

        return results;
    }

    /// <summary>
    /// Collect raw results from single pattern for aggregation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectRawResultsSingle(ref BindingTable bindingTable,
        Dictionary<string, SubQueryGroupedRow> groups, string source)
    {
        var tp = _subSelect.GetPattern(0);
        var sourceSpan = source.AsSpan();
        // Use graph context if subquery is inside a GRAPH clause
        var graphSpan = _graphContext != null ? _graphContext.AsSpan() : ReadOnlySpan<char>.Empty;
        var scan = new TriplePatternScan(_store, sourceSpan, tp, bindingTable, graphSpan,
            TemporalQueryMode.Current, default, default, default, _prefixes);
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                // Apply filters if any
                if (_subSelect.FilterCount > 0)
                {
                    bool passedFilters = true;
                    for (int i = 0; i < _subSelect.FilterCount; i++)
                    {
                        var filter = _subSelect.GetFilter(i);
                        var filterExpr = sourceSpan.Slice(filter.Start, filter.Length);
                        var evaluator = new FilterEvaluator(filterExpr);
                        if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
                        {
                            passedFilters = false;
                            break;
                        }
                    }
                    if (!passedFilters)
                    {
                        bindingTable.Clear();
                        continue;
                    }
                }

                // Build group key from GROUP BY variables
                var groupKey = BuildGroupKey(ref bindingTable, source);

                // Get or create group
                if (!groups.TryGetValue(groupKey, out var group))
                {
                    group = new SubQueryGroupedRow(_subSelect, bindingTable, source);
                    groups[groupKey] = group;
                }

                // Update aggregates for this group
                group.UpdateAggregates(bindingTable, source);
                bindingTable.Clear();
            }
        }
        finally
        {
            scan.Dispose();
        }
    }

    /// <summary>
    /// Collect raw results from multi-pattern for aggregation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectRawResultsMulti(ref BindingTable bindingTable,
        Dictionary<string, SubQueryGroupedRow> groups, string source)
    {
        if (_subSelect.HasUnion)
        {
            CollectRawResultsUnion(ref bindingTable, groups, source);
        }
        else
        {
            var boxedPattern = new MultiPatternScan.BoxedPattern();
            for (int i = 0; i < _subSelect.PatternCount; i++)
            {
                boxedPattern.Pattern.AddPattern(_subSelect.GetPattern(i));
            }

            var sourceSpan = source.AsSpan();
            // Use graph context if subquery is inside a GRAPH clause
            var graphSpan = _graphContext != null ? _graphContext.AsSpan() : ReadOnlySpan<char>.Empty;
            var scan = new MultiPatternScan(_store, sourceSpan, boxedPattern, graphSpan, _prefixes);
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    // Apply filters if any
                    if (_subSelect.FilterCount > 0)
                    {
                        bool passedFilters = true;
                        for (int i = 0; i < _subSelect.FilterCount; i++)
                        {
                            var filter = _subSelect.GetFilter(i);
                            var filterExpr = sourceSpan.Slice(filter.Start, filter.Length);
                            var evaluator = new FilterEvaluator(filterExpr);
                            if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
                            {
                                passedFilters = false;
                                break;
                            }
                        }
                        if (!passedFilters)
                        {
                            bindingTable.Clear();
                            continue;
                        }
                    }

                    var groupKey = BuildGroupKey(ref bindingTable, source);

                    if (!groups.TryGetValue(groupKey, out var group))
                    {
                        group = new SubQueryGroupedRow(_subSelect, bindingTable, source);
                        groups[groupKey] = group;
                    }

                    group.UpdateAggregates(bindingTable, source);
                    bindingTable.Clear();
                }
            }
            finally
            {
                scan.Dispose();
            }
        }
    }

    /// <summary>
    /// Collect raw results from UNION branches for aggregation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectRawResultsUnion(ref BindingTable bindingTable,
        Dictionary<string, SubQueryGroupedRow> groups, string source)
    {
        var sourceSpan = source.AsSpan();

        // Execute first branch
        if (_subSelect.FirstBranchPatternCount > 0)
        {
            if (_subSelect.FirstBranchPatternCount > 1)
            {
                var firstBranch = new MultiPatternScan.BoxedPattern();
                for (int i = 0; i < _subSelect.FirstBranchPatternCount; i++)
                {
                    firstBranch.Pattern.AddPattern(_subSelect.GetPattern(i));
                }

                var firstScan = new MultiPatternScan(_store, sourceSpan, firstBranch, _prefixes);
                try
                {
                    while (firstScan.MoveNext(ref bindingTable))
                    {
                        ProcessRowForAggregation(ref bindingTable, groups, source, sourceSpan);
                        bindingTable.Clear();
                    }
                }
                finally
                {
                    firstScan.Dispose();
                }
            }
            else
            {
                var tp = _subSelect.GetPattern(0);
                var scan = new TriplePatternScan(_store, sourceSpan, tp, bindingTable, default,
                    TemporalQueryMode.Current, default, default, default, _prefixes);
                try
                {
                    while (scan.MoveNext(ref bindingTable))
                    {
                        ProcessRowForAggregation(ref bindingTable, groups, source, sourceSpan);
                        bindingTable.Clear();
                    }
                }
                finally
                {
                    scan.Dispose();
                }
            }
        }

        // Execute union branch patterns
        for (int i = _subSelect.UnionStartIndex; i < _subSelect.PatternCount; i++)
        {
            var tp = _subSelect.GetPattern(i);
            var scan = new TriplePatternScan(_store, sourceSpan, tp, bindingTable, default,
                TemporalQueryMode.Current, default, default, default, _prefixes);
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    ProcessRowForAggregation(ref bindingTable, groups, source, sourceSpan);
                    bindingTable.Clear();
                }
            }
            finally
            {
                scan.Dispose();
            }
        }
    }

    /// <summary>
    /// Process a single row for aggregation: apply filters, build group key, update aggregates.
    /// </summary>
    private void ProcessRowForAggregation(ref BindingTable bindingTable,
        Dictionary<string, SubQueryGroupedRow> groups, string source, ReadOnlySpan<char> sourceSpan)
    {
        // Apply filters if any
        if (_subSelect.FilterCount > 0)
        {
            for (int i = 0; i < _subSelect.FilterCount; i++)
            {
                var filter = _subSelect.GetFilter(i);
                var filterExpr = sourceSpan.Slice(filter.Start, filter.Length);
                var evaluator = new FilterEvaluator(filterExpr);
                if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
                    return;
            }
        }

        var groupKey = BuildGroupKey(ref bindingTable, source);

        if (!groups.TryGetValue(groupKey, out var group))
        {
            group = new SubQueryGroupedRow(_subSelect, bindingTable, source);
            groups[groupKey] = group;
        }

        group.UpdateAggregates(bindingTable, source);
    }

    /// <summary>
    /// Build a group key from GROUP BY variables.
    /// </summary>
    private string BuildGroupKey(ref BindingTable bindings, string source)
    {
        if (!_subSelect.HasGroupBy || _subSelect.GroupBy.Count == 0)
            return "";

        var keyBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < _subSelect.GroupBy.Count; i++)
        {
            var (start, len) = _subSelect.GroupBy.GetVariable(i);
            var varName = source.AsSpan(start, len);
            var bindingIdx = bindings.FindBinding(varName);
            if (bindingIdx >= 0)
            {
                if (i > 0) keyBuilder.Append('\0');
                keyBuilder.Append(bindings.GetString(bindingIdx));
            }
        }
        return keyBuilder.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MaterializeSinglePattern(ref BindingTable bindingTable, List<MaterializedRow> results,
        HashSet<int>? seenHashes, ref int skipped, ref int returned)
    {
        var tp = _subSelect.GetPattern(0);
        var source = _source.AsSpan();
        // Use graph context if subquery is inside a GRAPH clause (e.g., GRAPH :g2 { ?s ?p ?o })
        var graphSpan = _graphContext != null ? _graphContext.AsSpan() : ReadOnlySpan<char>.Empty;
        // Pass prefixes for prefix expansion in nested subqueries (e.g., ex:q -> <http://...#q>)
        var scan = new TriplePatternScan(_store, source, tp, bindingTable, graphSpan,
            TemporalQueryMode.Current, default, default, default, _prefixes);
        try
        {
            while (scan.MoveNext(ref bindingTable))
            {
                if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                {
                    if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                        break;
                }
                bindingTable.Clear();
            }
        }
        finally
        {
            scan.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MaterializeMultiPattern(ref BindingTable bindingTable, List<MaterializedRow> results,
        HashSet<int>? seenHashes, ref int skipped, ref int returned)
    {
        if (_subSelect.HasUnion)
        {
            // Execute UNION: first branch then union branch
            MaterializeUnionBranches(ref bindingTable, results, seenHashes, ref skipped, ref returned);
        }
        else
        {
            // Build pattern on heap
            var boxedPattern = new MultiPatternScan.BoxedPattern();
            for (int i = 0; i < _subSelect.PatternCount; i++)
            {
                boxedPattern.Pattern.AddPattern(_subSelect.GetPattern(i));
            }

            var source = _source.AsSpan();
            // Use graph context if subquery is inside a GRAPH clause
            var graphSpan = _graphContext != null ? _graphContext.AsSpan() : ReadOnlySpan<char>.Empty;
            // Pass prefixes for prefix expansion in nested subqueries (e.g., ex:q -> <http://...#q>)
            var scan = new MultiPatternScan(_store, source, boxedPattern, graphSpan, _prefixes);
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                    {
                        if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                            break;
                    }
                    bindingTable.Clear();
                }
            }
            finally
            {
                scan.Dispose();
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MaterializeUnionBranches(ref BindingTable bindingTable, List<MaterializedRow> results,
        HashSet<int>? seenHashes, ref int skipped, ref int returned)
    {
        var source = _source.AsSpan();

        // Execute first branch (patterns before UNION)
        if (_subSelect.FirstBranchPatternCount > 0)
        {
            // If first branch has multiple patterns, execute as multi-pattern scan
            if (_subSelect.FirstBranchPatternCount > 1)
            {
                var firstBranch = new MultiPatternScan.BoxedPattern();
                for (int i = 0; i < _subSelect.FirstBranchPatternCount; i++)
                {
                    firstBranch.Pattern.AddPattern(_subSelect.GetPattern(i));
                }

                var firstScan = new MultiPatternScan(_store, source, firstBranch, _prefixes);
                try
                {
                    while (firstScan.MoveNext(ref bindingTable))
                    {
                        if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                        {
                            if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                                return;
                        }
                        bindingTable.Clear();
                    }
                }
                finally
                {
                    firstScan.Dispose();
                }
            }
            else
            {
                // Single pattern - use TriplePatternScan
                var tp = _subSelect.GetPattern(0);
                var scan = new TriplePatternScan(_store, source, tp, bindingTable, default,
                    TemporalQueryMode.Current, default, default, default, _prefixes);
                try
                {
                    while (scan.MoveNext(ref bindingTable))
                    {
                        if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                        {
                            if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                                return;
                        }
                        bindingTable.Clear();
                    }
                }
                finally
                {
                    scan.Dispose();
                }
            }
        }

        // Execute union branch - each pattern separately as an alternative
        for (int i = _subSelect.UnionStartIndex; i < _subSelect.PatternCount; i++)
        {
            var tp = _subSelect.GetPattern(i);
            var scan = new TriplePatternScan(_store, source, tp, bindingTable, default,
                TemporalQueryMode.Current, default, default, default, _prefixes);
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    if (ProcessAndAddResult(ref bindingTable, results, seenHashes, ref skipped, ref returned))
                    {
                        if (_subSelect.Limit > 0 && returned >= _subSelect.Limit)
                            return;
                    }
                    bindingTable.Clear();
                }
            }
            finally
            {
                scan.Dispose();
            }
        }
    }

    private bool ProcessAndAddResult(ref BindingTable innerBindings, List<MaterializedRow> results,
        HashSet<int>? seenHashes, ref int skipped, ref int returned)
    {
        var source = _source.AsSpan();

        // Apply filters if any
        if (_subSelect.FilterCount > 0)
        {
            for (int i = 0; i < _subSelect.FilterCount; i++)
            {
                var filter = _subSelect.GetFilter(i);
                var filterExpr = source.Slice(filter.Start, filter.Length);
                var evaluator = new FilterEvaluator(filterExpr);
                if (!evaluator.Evaluate(innerBindings.GetBindings(), innerBindings.Count, innerBindings.GetStringBuffer()))
                    return false;
            }
        }

        // Apply VALUES constraint if present
        if (_subSelect.Values.HasValues)
        {
            if (!MatchesValuesConstraint(ref innerBindings, source))
                return false;
        }

        // Evaluate non-aggregate computed expressions (e.g., CONCAT(?F, " ", ?L) AS ?FullName)
        // These are stored with AggregateFunction.None and need to be evaluated per-row
        if (_subSelect.HasAggregates && !_subSelect.HasRealAggregates)
        {
            EvaluateComputedExpressions(ref innerBindings, source);
        }

        // Apply OFFSET
        if (skipped < _subSelect.Offset)
        {
            skipped++;
            return false;
        }

        // Apply DISTINCT on projected variables
        if (seenHashes != null)
        {
            var hash = ComputeProjectedBindingsHash(ref innerBindings, source);
            if (!seenHashes.Add(hash))
                return false;
        }

        // Project and materialize the result
        var projectedRow = ProjectToMaterializedRow(ref innerBindings, source);
        results.Add(projectedRow);
        returned++;
        return true;
    }

    /// <summary>
    /// Evaluates non-aggregate computed expressions (e.g., CONCAT, STR) per-row
    /// and binds the results to their alias variables.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EvaluateComputedExpressions(ref BindingTable bindings, ReadOnlySpan<char> source)
    {
        var stringBuffer = bindings.GetStringBuffer();

        for (int i = 0; i < _subSelect.AggregateCount; i++)
        {
            var agg = _subSelect.GetAggregate(i);

            // Only evaluate non-aggregate expressions (Function == None)
            if (agg.Function != AggregateFunction.None) continue;

            // Skip if no expression to evaluate
            if (agg.VariableLength == 0) continue;

            // Get expression and alias
            var expr = source.Slice(agg.VariableStart, agg.VariableLength);
            var aliasName = source.Slice(agg.AliasStart, agg.AliasLength);

            // Evaluate the expression using BindExpressionEvaluator
            var evaluator = new BindExpressionEvaluator(expr,
                bindings.GetBindings(),
                bindings.Count,
                stringBuffer,
                ReadOnlySpan<char>.Empty);  // No base IRI in subquery context
            var value = evaluator.Evaluate();

            // Bind the result to the alias variable
            switch (value.Type)
            {
                case ValueType.Integer:
                    bindings.Bind(aliasName, value.IntegerValue);
                    break;
                case ValueType.Double:
                    bindings.Bind(aliasName, value.DoubleValue);
                    break;
                case ValueType.Boolean:
                    bindings.Bind(aliasName, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.Uri:
                    bindings.Bind(aliasName, value.StringValue);
                    break;
            }
        }
    }

    private int ComputeProjectedBindingsHash(ref BindingTable innerBindings, ReadOnlySpan<char> source)
    {
        int hash = 17;
        if (_subSelect.SelectAll)
        {
            for (int i = 0; i < innerBindings.Count; i++)
            {
                hash = hash * 31 + innerBindings.GetVariableHash(i);
                hash = hash * 31 + string.GetHashCode(innerBindings.GetString(i));
            }
        }
        else
        {
            for (int i = 0; i < _subSelect.ProjectedVarCount; i++)
            {
                var (start, len) = _subSelect.GetProjectedVariable(i);
                var varName = source.Slice(start, len);
                var idx = innerBindings.FindBinding(varName);
                if (idx >= 0)
                {
                    hash = hash * 31 + innerBindings.GetVariableHash(idx);
                    hash = hash * 31 + string.GetHashCode(innerBindings.GetString(idx));
                }
            }
        }
        return hash;
    }

    private MaterializedRow ProjectToMaterializedRow(ref BindingTable innerBindings, ReadOnlySpan<char> source)
    {
        var projectedBindings = new Binding[16];
        var projectedBuffer = new char[512];
        var projectedTable = new BindingTable(projectedBindings, projectedBuffer);

        if (_subSelect.SelectAll)
        {
            for (int i = 0; i < innerBindings.Count; i++)
            {
                var varHash = innerBindings.GetVariableHash(i);
                var value = innerBindings.GetString(i);
                projectedTable.BindWithHash(varHash, value);
            }
        }
        else
        {
            for (int i = 0; i < _subSelect.ProjectedVarCount; i++)
            {
                var (start, len) = _subSelect.GetProjectedVariable(i);
                var varName = source.Slice(start, len);
                var idx = innerBindings.FindBinding(varName);
                if (idx >= 0)
                {
                    projectedTable.Bind(varName, innerBindings.GetString(idx));
                }
            }
        }

        return new MaterializedRow(projectedTable);
    }

    /// <summary>
    /// Check if current bindings match the VALUES constraint.
    /// Returns true if bindings match at least one row in the VALUES clause.
    /// </summary>
    private bool MatchesValuesConstraint(ref BindingTable bindings, ReadOnlySpan<char> source)
    {
        var values = _subSelect.Values;
        int rowCount = values.RowCount;
        int varCount = values.VariableCount;

        // For each row in VALUES, check if ALL values in that row match
        for (int row = 0; row < rowCount; row++)
        {
            bool rowMatches = true;

            for (int varIdx = 0; varIdx < varCount; varIdx++)
            {
                var (valStart, valLen) = values.GetValueAt(row, varIdx);

                // UNDEF matches anything
                if (valLen == -1)
                    continue;

                // Get variable name
                var (varStart, varLength) = values.GetVariable(varIdx);
                var varName = source.Slice(varStart, varLength);

                // Find binding for this variable
                var bindingIdx = bindings.FindBinding(varName);
                if (bindingIdx < 0)
                {
                    // Variable not bound - doesn't match this row
                    rowMatches = false;
                    break;
                }

                var boundValue = bindings.GetString(bindingIdx);
                var valuesEntry = source.Slice(valStart, valLen);

                // Compare with prefix expansion
                if (!CompareValuesMatchWithPrefixExpansion(boundValue, valuesEntry))
                {
                    rowMatches = false;
                    break;
                }
            }

            if (rowMatches)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compare a bound value with a VALUES entry for equality, expanding prefixed names if needed.
    /// </summary>
    private bool CompareValuesMatchWithPrefixExpansion(ReadOnlySpan<char> boundValue, ReadOnlySpan<char> valuesEntry)
    {
        // Direct comparison first
        if (boundValue.SequenceEqual(valuesEntry))
            return true;

        // If VALUES entry is already a full IRI
        if (valuesEntry.Length > 0 && valuesEntry[0] == '<')
        {
            return boundValue.SequenceEqual(valuesEntry);
        }

        // Check for prefixed name that needs expansion
        var colonIdx = valuesEntry.IndexOf(':');
        if (colonIdx >= 0 && _prefixes != null)
        {
            var prefix = valuesEntry.Slice(0, colonIdx + 1); // Include the colon
            var localName = valuesEntry.Slice(colonIdx + 1);

            foreach (var mapping in _prefixes)
            {
                var mappedPrefix = _source.AsSpan(mapping.PrefixStart, mapping.PrefixLength);
                if (prefix.SequenceEqual(mappedPrefix))
                {
                    // Found matching prefix - expand and compare
                    var iriNs = _source.AsSpan(mapping.IriStart, mapping.IriLength);
                    // IRI namespace is like <http://example.org/> - remove trailing > and append local name
                    var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);
                    var expanded = string.Concat(nsWithoutClose, localName, ">");
                    return boundValue.SequenceEqual(expanded.AsSpan());
                }
            }
        }

        // Handle literal comparison
        if (valuesEntry.Length > 0 && valuesEntry[0] == '"' &&
            boundValue.Length > 0 && boundValue[0] == '"')
        {
            return boundValue.SequenceEqual(valuesEntry);
        }

        return false;
    }
}
