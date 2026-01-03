using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// SERVICE clause execution methods.
/// Handles federated queries via <see cref="ISparqlServiceExecutor"/>.
/// Uses <see cref="ServiceMaterializer"/> for efficient result caching.
/// </summary>
public partial class QueryExecutor
{
    /// <summary>
    /// Gets or creates the ServiceMaterializer for this executor.
    /// </summary>
    private ServiceMaterializer GetServiceMaterializer()
    {
        if (_serviceMaterializer == null)
        {
            if (_serviceExecutor == null)
            {
                throw new InvalidOperationException(
                    "SERVICE clause requires an ISparqlServiceExecutor. " +
                    "Use the QueryExecutor constructor that accepts ISparqlServiceExecutor.");
            }
            _serviceMaterializer = new ServiceMaterializer(_serviceExecutor);
        }
        return _serviceMaterializer;
    }

    /// <summary>
    /// Fetches SERVICE results once and returns them for iteration.
    /// Uses ServiceMaterializer's executor to handle errors and SILENT.
    /// </summary>
    private List<ServiceResultRow> FetchServiceResults(ServiceClause serviceClause, BindingTable incomingBindings)
    {
        // Resolve endpoint URI
        string endpointUri;
        if (serviceClause.Endpoint.IsVariable)
        {
            var varName = _source.AsSpan().Slice(serviceClause.Endpoint.Start, serviceClause.Endpoint.Length);
            var idx = incomingBindings.FindBinding(varName);
            if (idx < 0)
            {
                // Variable not bound - cannot execute SERVICE
                return new List<ServiceResultRow>();
            }
            endpointUri = incomingBindings.GetString(idx).ToString();
            if (endpointUri.StartsWith('<') && endpointUri.EndsWith('>'))
                endpointUri = endpointUri[1..^1];
        }
        else
        {
            var iri = _source.AsSpan().Slice(serviceClause.Endpoint.Start, serviceClause.Endpoint.Length);
            if (iri.Length > 2 && iri[0] == '<' && iri[^1] == '>')
                endpointUri = iri[1..^1].ToString();
            else
                endpointUri = iri.ToString();
        }

        // Build SPARQL query from patterns
        var query = BuildServiceQuery(serviceClause, incomingBindings);

        try
        {
            return _serviceExecutor!.ExecuteSelectAsync(endpointUri, query)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (SparqlServiceException)
        {
            if (serviceClause.Silent)
                return new List<ServiceResultRow>();
            throw;
        }
        catch (Exception ex)
        {
            if (serviceClause.Silent)
                return new List<ServiceResultRow>();
            throw new SparqlServiceException($"SERVICE execution failed: {ex.Message}", ex)
            {
                EndpointUri = endpointUri,
                Query = query
            };
        }
    }

    /// <summary>
    /// Builds a SPARQL query string from SERVICE clause patterns.
    /// Substitutes bound variables from incoming bindings.
    /// </summary>
    private string BuildServiceQuery(ServiceClause clause, BindingTable incomingBindings)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT * WHERE { ");
        var source = _source.AsSpan();

        for (int i = 0; i < clause.PatternCount; i++)
        {
            var pattern = clause.GetPattern(i);

            // Subject
            AppendServiceTerm(sb, pattern.Subject, source, incomingBindings);
            sb.Append(' ');

            // Predicate
            AppendServiceTerm(sb, pattern.Predicate, source, incomingBindings);
            sb.Append(' ');

            // Object
            AppendServiceTerm(sb, pattern.Object, source, incomingBindings);
            sb.Append(" . ");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendServiceTerm(System.Text.StringBuilder sb, Term term,
        ReadOnlySpan<char> source, BindingTable incomingBindings)
    {
        var value = source.Slice(term.Start, term.Length);

        if (term.IsVariable)
        {
            // Check if variable is bound - substitute if so
            var idx = incomingBindings.FindBinding(value);
            if (idx >= 0)
            {
                sb.Append(incomingBindings.GetString(idx));
                return;
            }
        }

        sb.Append(value);
    }

    /// <summary>
    /// Execute a query with SERVICE clause and return lightweight materialized results.
    /// Use this for queries with SERVICE clauses to avoid stack overflow from large QueryResults struct.
    /// Caller must hold read lock on store.
    /// Uses ServicePatternScan for efficient iteration over cached results.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal MaterializedQueryResults ExecuteServiceToMaterialized()
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

            // Fetch SERVICE results once
            var serviceResults = FetchServiceResults(serviceClause, bindingTable);
            var serviceScan = new ServicePatternScan(serviceResults, bindingTable);

            // Materialize results
            var results = new List<MaterializedRow>();
            try
            {
                while (serviceScan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                serviceScan.Dispose();
            }

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
    /// Execute a query with SERVICE clauses (federated queries).
    /// Returns materialized results to avoid stack overflow from large QueryResults struct.
    /// For queries like: SELECT * WHERE { SERVICE &lt;endpoint&gt; { ?s ?p ?o } }
    /// Also supports: SERVICE SILENT &lt;endpoint&gt; { ... }
    /// Uses ServicePatternScan for efficient iteration over cached results.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteWithServiceMaterialized()
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

        // Check for UNION with SERVICE in second branch
        if (pattern.HasUnion && serviceCount > 0)
        {
            return ExecuteUnionWithServiceMaterialized(pattern, bindings, stringBuffer, bindingTable);
        }

        // Simple case: SERVICE clause only, no local patterns
        if (pattern.PatternCount == 0 && serviceCount == 1)
        {
            // Execute single SERVICE clause
            var serviceClause = pattern.GetServiceClause(0);

            // Fetch SERVICE results once
            var serviceResults = FetchServiceResults(serviceClause, bindingTable);
            var serviceScan = new ServicePatternScan(serviceResults, bindingTable);

            // Materialize results and return
            var results = new List<MaterializedRow>();
            try
            {
                while (serviceScan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                serviceScan.Dispose();
            }

            return results;
        }

        // Mixed case: SERVICE clause(s) with local patterns
        if (pattern.PatternCount > 0 && serviceCount == 1)
        {
            return ExecuteServiceWithLocalPatternsInternal(bindings, stringBuffer);
        }

        // Multiple SERVICE clauses - execute sequentially and join
        if (serviceCount > 1)
        {
            return ExecuteMultipleServicesMaterialized(pattern, bindings, stringBuffer, bindingTable);
        }

        return null;
    }

    /// <summary>
    /// Execute UNION query where one or both branches contain SERVICE clauses.
    /// { local patterns } UNION { SERVICE ... } or { SERVICE ... } UNION { SERVICE ... }
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteUnionWithServiceMaterialized(
        in GraphPattern pattern,
        Binding[] bindings,
        char[] stringBuffer,
        BindingTable bindingTable)
    {
        var results = new List<MaterializedRow>();

        // Separate SERVICE clauses by branch
        ServiceClause? firstBranchService = null;
        ServiceClause? secondBranchService = null;
        for (int i = 0; i < pattern.ServiceClauseCount; i++)
        {
            var svc = pattern.GetServiceClause(i);
            if (svc.UnionBranch == 0)
                firstBranchService = svc;
            else
                secondBranchService = svc;
        }

        // Execute first branch (patterns before _unionStartIndex)
        var firstBranchPatternCount = pattern.FirstBranchPatternCount;
        if (firstBranchPatternCount > 0 && firstBranchService.HasValue)
        {
            // First branch has both local patterns AND SERVICE - join them
            var localResults = ExecuteFirstBranchPatterns(in pattern, bindings, stringBuffer);
            foreach (var localRow in localResults)
            {
                ThrowIfCancellationRequested();
                bindingTable.TruncateTo(0);
                localRow.RestoreBindings(ref bindingTable);

                var serviceResults = FetchServiceResults(firstBranchService.Value, bindingTable);
                var serviceScan = new ServicePatternScan(serviceResults, bindingTable);
                try
                {
                    while (serviceScan.MoveNext(ref bindingTable))
                    {
                        results.Add(new MaterializedRow(bindingTable));
                    }
                }
                finally
                {
                    serviceScan.Dispose();
                }
            }
        }
        else if (firstBranchPatternCount > 0)
        {
            // Execute local patterns in first branch (no SERVICE)
            var firstBranchResults = ExecuteFirstBranchPatterns(in pattern, bindings, stringBuffer);
            results.AddRange(firstBranchResults);
        }
        else if (firstBranchService.HasValue)
        {
            // First branch is SERVICE-only
            var serviceResults = FetchServiceResults(firstBranchService.Value, bindingTable);
            var serviceScan = new ServicePatternScan(serviceResults, bindingTable);
            try
            {
                while (serviceScan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                serviceScan.Dispose();
            }
        }

        // Execute second branch (patterns from _unionStartIndex onwards, or SERVICE)
        var secondBranchPatternCount = pattern.UnionBranchPatternCount;
        if (secondBranchPatternCount > 0 && secondBranchService.HasValue)
        {
            // Second branch has both local patterns and SERVICE - join them
            bindingTable.Clear();
            var localResults = ExecuteSecondBranchPatterns(in pattern, bindings, stringBuffer);
            foreach (var localRow in localResults)
            {
                ThrowIfCancellationRequested();
                bindingTable.TruncateTo(0);
                localRow.RestoreBindings(ref bindingTable);

                var serviceResults = FetchServiceResults(secondBranchService.Value, bindingTable);
                var serviceScan = new ServicePatternScan(serviceResults, bindingTable);
                try
                {
                    while (serviceScan.MoveNext(ref bindingTable))
                    {
                        results.Add(new MaterializedRow(bindingTable));
                    }
                }
                finally
                {
                    serviceScan.Dispose();
                }
            }
        }
        else if (secondBranchPatternCount > 0)
        {
            // Second branch has only local patterns
            var secondBranchResults = ExecuteSecondBranchPatterns(in pattern, bindings, stringBuffer);
            results.AddRange(secondBranchResults);
        }
        else if (secondBranchService.HasValue)
        {
            // Second branch is SERVICE-only
            bindingTable.Clear();
            var serviceResults = FetchServiceResults(secondBranchService.Value, bindingTable);
            var serviceScan = new ServicePatternScan(serviceResults, bindingTable);
            try
            {
                while (serviceScan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                serviceScan.Dispose();
            }
        }

        return results;
    }

    /// <summary>
    /// Execute patterns in the first UNION branch (before _unionStartIndex).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteFirstBranchPatterns(
        in GraphPattern pattern,
        Binding[] bindings,
        char[] stringBuffer)
    {
        var results = new List<MaterializedRow>();
        var bindingTable = new BindingTable(bindings, stringBuffer);
        var patternCount = pattern.FirstBranchPatternCount;

        if (patternCount == 1)
        {
            var tp = pattern.GetPattern(0);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd);
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                scan.Dispose();
            }
        }
        else if (patternCount > 1)
        {
            // Create a temporary pattern with just the first branch patterns
            var branchPattern = new GraphPattern();
            for (int i = 0; i < patternCount; i++)
            {
                branchPattern.AddPattern(pattern.GetPattern(i));
            }
            var multiScan = new MultiPatternScan(_store, _source, branchPattern, false, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, null, null);
            try
            {
                while (multiScan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                multiScan.Dispose();
            }
        }

        return results;
    }

    /// <summary>
    /// Execute patterns in the second UNION branch (from _unionStartIndex onwards).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteSecondBranchPatterns(
        in GraphPattern pattern,
        Binding[] bindings,
        char[] stringBuffer)
    {
        var results = new List<MaterializedRow>();
        var bindingTable = new BindingTable(bindings, stringBuffer);
        var firstBranchCount = pattern.FirstBranchPatternCount;
        var totalCount = pattern.PatternCount;
        var secondBranchCount = totalCount - firstBranchCount;

        if (secondBranchCount == 1)
        {
            var tp = pattern.GetPattern(firstBranchCount);
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd);
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                scan.Dispose();
            }
        }
        else if (secondBranchCount > 1)
        {
            // Create a temporary pattern with just the second branch patterns
            var branchPattern = new GraphPattern();
            for (int i = firstBranchCount; i < totalCount; i++)
            {
                branchPattern.AddPattern(pattern.GetPattern(i));
            }
            var multiScan = new MultiPatternScan(_store, _source, branchPattern, false, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, null, null);
            try
            {
                while (multiScan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                multiScan.Dispose();
            }
        }

        return results;
    }

    /// <summary>
    /// Execute SERVICE+local pattern join on thread pool thread (fresh stack).
    /// Returns materialized results list for the caller to wrap in QueryResults.
    /// Creates BindingTable locally to avoid passing ref struct.
    /// Uses QueryPlanner to select optimal strategy (local-first vs service-first).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteServiceWithLocalPatternsInternal(Binding[] bindings, char[] stringBuffer)
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Determine optimal strategy using QueryPlanner
        bool useLocalFirst = true; // Default to local-first
        if (_planner != null && _cachedFirstServiceClause.HasValue)
        {
            useLocalFirst = _planner.ShouldUseLocalFirstStrategy(
                in pattern,
                _cachedFirstServiceClause.Value,
                _source.AsSpan());
        }

        if (useLocalFirst)
        {
            // Local-first: Execute local patterns, then SERVICE for each result
            var localResults = ExecuteLocalPatternsPhase(in pattern, bindingTable);
            if (localResults.Count == 0)
                return new List<MaterializedRow>();
            return ExecuteServiceJoinPhase(localResults, bindings, stringBuffer);
        }
        else
        {
            // Service-first: Execute SERVICE, then local patterns for each result
            var serviceResults = ExecuteServiceFirstPhase(bindingTable);
            if (serviceResults.Count == 0)
                return new List<MaterializedRow>();
            return ExecuteLocalJoinPhase(serviceResults, bindings, stringBuffer);
        }
    }

    /// <summary>
    /// Phase 1: Execute local patterns to get initial results.
    /// Uses MultiPatternScan for multiple patterns with filter pushdown.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteLocalPatternsPhase(in GraphPattern pattern, BindingTable bindingTable)
    {
        var results = new List<MaterializedRow>();
        var requiredCount = pattern.RequiredPatternCount;
        var incomingBindingCount = bindingTable.Count;

        if (requiredCount == 1)
        {
            // Single pattern - use TriplePatternScan
            if (!_cachedFirstPattern.HasValue)
                return results;

            var tp = _cachedFirstPattern.Value;
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd);
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    if (!PassesFilters(in pattern, ref bindingTable))
                    {
                        bindingTable.TruncateTo(incomingBindingCount);
                        continue;
                    }
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                scan.Dispose();
            }
        }
        else if (requiredCount > 1)
        {
            // Multiple patterns - use MultiPatternScan with filter pushdown
            var levelFilters = pattern.FilterCount > 0
                ? FilterAnalyzer.BuildLevelFilters(in pattern, _source, requiredCount, _optimizedPatternOrder)
                : null;
            var unpushableFilters = levelFilters != null
                ? FilterAnalyzer.GetUnpushableFilters(in pattern, _source, _optimizedPatternOrder)
                : null;

            var multiScan = new MultiPatternScan(_store, _source, pattern, false, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _optimizedPatternOrder, levelFilters);
            try
            {
                while (multiScan.MoveNext(ref bindingTable))
                {
                    ThrowIfCancellationRequested();
                    // Only evaluate unpushable filters - pushed ones were checked in MoveNext
                    if (!PassesUnpushableFilters(in pattern, ref bindingTable, unpushableFilters))
                    {
                        bindingTable.TruncateTo(incomingBindingCount);
                        continue;
                    }
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                multiScan.Dispose();
            }
        }

        return results;
    }

    /// <summary>
    /// Phase 2: For each local result, join with SERVICE results.
    /// Uses cached SERVICE clause to avoid accessing large GraphPattern struct from stack.
    /// For OPTIONAL SERVICE, preserves local bindings even when SERVICE returns no match.
    ///
    /// OPTIMIZATION: Fetches SERVICE results ONCE and reuses for all local rows (when endpoint is fixed).
    /// For variable endpoints, must fetch per local result (different endpoints possible).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteServiceJoinPhase(
        List<MaterializedRow> localResults,
        Binding[] bindings,
        char[] stringBuffer)
    {
        var finalResults = new List<MaterializedRow>();

        // Use cached SERVICE clause (on heap)
        if (!_cachedFirstServiceClause.HasValue)
            return finalResults;

        var serviceClause = _cachedFirstServiceClause.Value;
        var isOptional = serviceClause.IsOptional;
        var isVariableEndpoint = serviceClause.IsVariable;

        // For fixed endpoints, fetch SERVICE results ONCE (key optimization)
        List<ServiceResultRow>? cachedServiceResults = null;
        if (!isVariableEndpoint)
        {
            var emptyBindingTable = new BindingTable(bindings, stringBuffer);
            cachedServiceResults = FetchServiceResults(serviceClause, emptyBindingTable);

            if (cachedServiceResults.Count == 0 && !isOptional)
            {
                // No SERVICE results and not optional - no final results possible
                return finalResults;
            }
        }

        foreach (var localRow in localResults)
        {
            ThrowIfCancellationRequested();

            // Restore local bindings
            var bindingTable = new BindingTable(bindings, stringBuffer);
            bindingTable.TruncateTo(0);
            localRow.RestoreBindings(ref bindingTable);

            // For variable endpoints, fetch per local result (endpoint may differ)
            var serviceResults = isVariableEndpoint
                ? FetchServiceResults(serviceClause, bindingTable)
                : cachedServiceResults!;

            // Use ServicePatternScan to iterate through results
            // Note: ServicePatternScan.MoveNext handles TruncateTo internally
            var serviceScan = new ServicePatternScan(serviceResults, bindingTable);
            var hasServiceMatch = false;
            try
            {
                while (serviceScan.MoveNext(ref bindingTable))
                {
                    hasServiceMatch = true;
                    finalResults.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                serviceScan.Dispose();
            }

            // For OPTIONAL SERVICE, preserve local bindings even if no SERVICE match
            if (isOptional && !hasServiceMatch)
            {
                bindingTable.TruncateTo(0);
                localRow.RestoreBindings(ref bindingTable);
                finalResults.Add(new MaterializedRow(bindingTable));
            }
        }

        return finalResults;
    }

    /// <summary>
    /// Service-first phase: Execute SERVICE clause and return materialized results.
    /// Used when QueryPlanner determines SERVICE is more selective than local patterns.
    /// Uses ServicePatternScan for efficient iteration over cached results.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteServiceFirstPhase(BindingTable bindingTable)
    {
        var results = new List<MaterializedRow>();
        var incomingBindingCount = bindingTable.Count;

        // Use cached SERVICE clause (on heap)
        if (!_cachedFirstServiceClause.HasValue)
            return results;

        var serviceClause = _cachedFirstServiceClause.Value;

        // Fetch SERVICE results once
        var serviceResults = FetchServiceResults(serviceClause, bindingTable);
        var serviceScan = new ServicePatternScan(serviceResults, bindingTable);

        try
        {
            while (serviceScan.MoveNext(ref bindingTable))
            {
                ThrowIfCancellationRequested();
                results.Add(new MaterializedRow(bindingTable));
                bindingTable.TruncateTo(incomingBindingCount);
            }
        }
        finally
        {
            serviceScan.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Local join phase: For each SERVICE result, execute local patterns and join.
    /// Used when QueryPlanner determines SERVICE is more selective than local patterns.
    /// Supports single or multiple local patterns via TriplePatternScan or MultiPatternScan.
    /// Uses filter pushdown for multi-pattern queries to evaluate filters early.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> ExecuteLocalJoinPhase(
        List<MaterializedRow> serviceResults,
        Binding[] bindings,
        char[] stringBuffer)
    {
        var finalResults = new List<MaterializedRow>();
        ref readonly var pattern = ref _query.WhereClause.Pattern;
        var requiredCount = pattern.RequiredPatternCount;

        // Pre-compute filter pushdown for multi-pattern case
        List<int>[]? levelFilters = null;
        List<int>? unpushableFilters = null;
        if (requiredCount > 1 && pattern.FilterCount > 0)
        {
            levelFilters = FilterAnalyzer.BuildLevelFilters(in pattern, _source, requiredCount, _optimizedPatternOrder);
            unpushableFilters = FilterAnalyzer.GetUnpushableFilters(in pattern, _source, _optimizedPatternOrder);
        }

        var bindingTable = new BindingTable(bindings, stringBuffer);

        foreach (var serviceRow in serviceResults)
        {
            ThrowIfCancellationRequested();

            // Restore bindings from SERVICE result
            bindingTable.TruncateTo(0);
            serviceRow.RestoreBindings(ref bindingTable);
            var bindingCountAfterRestore = bindingTable.Count;

            if (requiredCount == 1)
            {
                // Single pattern - use TriplePatternScan
                if (!_cachedFirstPattern.HasValue)
                    continue;

                var tp = _cachedFirstPattern.Value;
                var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd);
                try
                {
                    while (scan.MoveNext(ref bindingTable))
                    {
                        if (!PassesFilters(in pattern, ref bindingTable))
                        {
                            bindingTable.TruncateTo(bindingCountAfterRestore);
                            continue;
                        }
                        finalResults.Add(new MaterializedRow(bindingTable));
                    }
                }
                finally
                {
                    scan.Dispose();
                }
            }
            else if (requiredCount > 1)
            {
                // Multiple patterns - use MultiPatternScan with filter pushdown
                var multiScan = new MultiPatternScan(_store, _source, pattern, false, default,
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _optimizedPatternOrder, levelFilters);
                try
                {
                    while (multiScan.MoveNext(ref bindingTable))
                    {
                        // Only evaluate unpushable filters - pushed ones were checked in MoveNext
                        if (!PassesUnpushableFilters(in pattern, ref bindingTable, unpushableFilters))
                        {
                            bindingTable.TruncateTo(bindingCountAfterRestore);
                            continue;
                        }
                        finalResults.Add(new MaterializedRow(bindingTable));
                    }
                }
                finally
                {
                    multiScan.Dispose();
                }
            }
        }

        return finalResults;
    }

    /// <summary>
    /// Execute a query with multiple SERVICE clauses.
    /// Returns materialized results to avoid stack overflow.
    /// Executes each SERVICE sequentially and joins results.
    ///
    /// Note: For multiple SERVICE clauses with shared variables, we send bound values
    /// to the remote endpoint for each incoming row. This allows the remote to filter,
    /// which is more efficient than fetching all results and filtering locally.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow>? ExecuteMultipleServicesMaterialized(
        in GraphPattern pattern,
        Binding[] bindings,
        char[] stringBuffer,
        BindingTable bindingTable)
    {
        var serviceCount = pattern.ServiceClauseCount;
        var hasLocalPatterns = pattern.PatternCount > 0;

        // Strategy: Execute first SERVICE, then for each result execute remaining SERVICE clauses
        // If there are local patterns, execute them first (they're usually more selective)

        List<MaterializedRow>? currentResults = null;

        // If we have local patterns, execute them first
        if (hasLocalPatterns)
        {
            var localResults = ExecuteLocalPatternsPhase(in pattern, bindingTable);
            if (localResults.Count == 0)
                return null;

            currentResults = localResults;
        }

        // Execute each SERVICE clause, joining with current results
        for (int i = 0; i < serviceCount; i++)
        {
            var serviceClause = pattern.GetServiceClause(i);

            if (currentResults == null)
            {
                // First SERVICE clause - no prior results to join with
                var serviceResults = FetchServiceResults(serviceClause, bindingTable);
                var serviceScan = new ServicePatternScan(serviceResults, bindingTable);

                currentResults = new List<MaterializedRow>();
                try
                {
                    while (serviceScan.MoveNext(ref bindingTable))
                    {
                        ThrowIfCancellationRequested();
                        currentResults.Add(new MaterializedRow(bindingTable));
                    }
                }
                finally
                {
                    serviceScan.Dispose();
                }
            }
            else
            {
                // Join SERVICE with current results
                // Send bound values to remote for filtering (more efficient for shared variables)
                var newResults = new List<MaterializedRow>();

                foreach (var row in currentResults)
                {
                    ThrowIfCancellationRequested();
                    bindingTable.TruncateTo(0);
                    row.RestoreBindings(ref bindingTable);

                    // Fetch SERVICE with current bindings - allows remote filtering
                    var serviceResults = FetchServiceResults(serviceClause, bindingTable);
                    var serviceScan = new ServicePatternScan(serviceResults, bindingTable);
                    try
                    {
                        while (serviceScan.MoveNext(ref bindingTable))
                        {
                            newResults.Add(new MaterializedRow(bindingTable));
                        }
                    }
                    finally
                    {
                        serviceScan.Dispose();
                    }
                }

                currentResults = newResults;
            }

            if (currentResults.Count == 0)
                return null;
        }

        return currentResults;
    }
}
