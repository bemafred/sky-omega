using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SkyOmega.Mercury.Runtime.Buffers;
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
public partial class QueryExecutor : IDisposable
{
    /// <summary>
    /// Default maximum join depth (number of patterns in a single nested loop join).
    /// Prevents stack overflow from queries with excessive pattern counts.
    /// </summary>
    public const int DefaultMaxJoinDepth = 32;

    /// <summary>
    /// Maximum number of result iterations before throwing an exception.
    /// Prevents infinite loops in pathological queries (e.g., cyclic property paths).
    /// Default: 10 million iterations.
    /// </summary>
    public const long DefaultMaxIterations = 10_000_000;

    private readonly QuadStore _store;
    private readonly string _source;
    private readonly QueryBuffer _buffer;  // Heap-allocated pattern storage (ADR-009 Phase 2)
    private readonly GraphPattern _cachedPattern;  // Cached from buffer for scans
    private readonly IBufferManager _bufferManager;
    private readonly char[] _stringBuffer; // Pooled buffer for string operations (replaces scattered new char[1024])
    private readonly int _maxJoinDepth;
    private bool _disposed;

    // Prefix mappings for expanding prefixed names
    private readonly Prologue _prologue;
    private readonly PrefixMapping[]? _prefixMappings;

    // Dataset context: default graph IRIs (FROM) and named graph IRIs (FROM NAMED)
    private readonly string[]? _defaultGraphs;
    private readonly string[]? _namedGraphs;

    // SERVICE clause execution
    private readonly ISparqlServiceExecutor? _serviceExecutor;
    private ServiceMaterializer? _serviceMaterializer;

    // Cached patterns for SERVICE+local joins (stored on heap to avoid stack overflow)
    private readonly TriplePattern? _cachedFirstPattern;
    private readonly ServiceClause? _cachedFirstServiceClause;

    // Temporal query parameters
    private readonly TemporalQueryMode _temporalMode;
    private readonly DateTimeOffset _asOfTime;
    private readonly DateTimeOffset _rangeStart;
    private readonly DateTimeOffset _rangeEnd;

    // Query optimization
    private readonly QueryPlanner? _planner;
    private readonly int[]? _optimizedPatternOrder;

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, in Query query)
        : this(store, source, in query, null, null, null, DefaultMaxJoinDepth) { }

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, in Query query,
        ISparqlServiceExecutor? serviceExecutor)
        : this(store, source, in query, serviceExecutor, null, null, DefaultMaxJoinDepth) { }

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, in Query query,
        ISparqlServiceExecutor? serviceExecutor, QueryPlanner? planner)
        : this(store, source, in query, serviceExecutor, planner, null, DefaultMaxJoinDepth) { }

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, in Query query,
        ISparqlServiceExecutor? serviceExecutor, QueryPlanner? planner, IBufferManager? bufferManager)
        : this(store, source, in query, serviceExecutor, planner, bufferManager, DefaultMaxJoinDepth) { }

    public QueryExecutor(QuadStore store, ReadOnlySpan<char> source, in Query query,
        ISparqlServiceExecutor? serviceExecutor, QueryPlanner? planner, IBufferManager? bufferManager,
        int maxJoinDepth)
    {
        if (maxJoinDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxJoinDepth), "Max join depth must be positive");

        _store = store;
        _source = source.ToString();  // Copy to heap - enables class-based execution
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _stringBuffer = _bufferManager.Rent<char>(1024).Array!;  // Pooled buffer for string operations
        _planner = planner;
        _maxJoinDepth = maxJoinDepth;

        // ADR-009 Phase 2: Convert Query to QueryBuffer for heap-based storage
        // This avoids stack overflow when accessing patterns in nested calls
        // The Query struct is NOT stored - only the buffer is kept
        _buffer = QueryBufferAdapter.FromQuery(in query, source);
        // Cache the original GraphPattern (has all complex structures like subqueries, service clauses)
        // Building from buffer would lose this info, so we copy directly
        _cachedPattern = query.WhereClause.Pattern;
        // Store prologue for prefix expansion
        _prologue = query.Prologue;

        // Extract prefix mappings for subquery execution
        _prefixMappings = ExtractPrefixMappings(in _prologue);

        _defaultGraphs = null;
        _namedGraphs = null;

        // Extract dataset clauses into arrays (using buffer instead of query)
        if (_buffer.Datasets != null && _buffer.Datasets.Length > 0)
        {
            var defaultList = new List<string>();
            var namedList = new List<string>();

            foreach (var ds in _buffer.Datasets)
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

        // Cache first pattern and service clause for SERVICE+local joins
        // Now using _cachedPattern instead of query.WhereClause.Pattern
        if (_cachedPattern.PatternCount > 0)
        {
            _cachedFirstPattern = _cachedPattern.GetPattern(0);
        }
        if (_cachedPattern.ServiceClauseCount > 0)
        {
            _cachedFirstServiceClause = _cachedPattern.GetServiceClause(0);
        }

        // Extract temporal clause parameters (from buffer instead of query)
        _temporalMode = _buffer.TemporalMode;
        if (_temporalMode == TemporalQueryMode.AsOf)
        {
            var timeStr = source.Slice(_buffer.TimeStartStart, _buffer.TimeStartLength);
            _asOfTime = ParseDateTimeOffset(timeStr);
        }
        else if (_temporalMode == TemporalQueryMode.During)
        {
            var startStr = source.Slice(_buffer.TimeStartStart, _buffer.TimeStartLength);
            var endStr = source.Slice(_buffer.TimeEndStart, _buffer.TimeEndLength);
            _rangeStart = ParseDateTimeOffset(startStr);
            _rangeEnd = ParseDateTimeOffset(endStr);
        }

        // Compute optimized pattern order if planner is available and we have multiple patterns
        if (_planner != null && _cachedPattern.RequiredPatternCount > 1)
        {
            _optimizedPatternOrder = _planner.OptimizePatternOrder(
                in _cachedPattern, source);
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
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
            return result;

        // Try parse as date only (assume start of day)
        if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return new DateTimeOffset(dt, TimeSpan.Zero);

        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Extract prefix mappings from Prologue into an array for subquery execution.
    /// Returns null if no prefixes are defined.
    /// </summary>
    private static PrefixMapping[]? ExtractPrefixMappings(in Prologue prologue)
    {
        if (prologue.PrefixCount == 0)
            return null;

        var mappings = new PrefixMapping[prologue.PrefixCount];
        for (int i = 0; i < prologue.PrefixCount; i++)
        {
            var (ps, pl, irs, irl) = prologue.GetPrefix(i);
            mappings[i] = new PrefixMapping
            {
                PrefixStart = ps,
                PrefixLength = pl,
                IriStart = irs,
                IriLength = irl
            };
        }
        return mappings;
    }

    /// <summary>
    /// Alternative constructor that takes a pre-allocated QueryBuffer directly.
    /// The caller transfers ownership of the buffer to the executor.
    /// </summary>
    internal QueryExecutor(QuadStore store, ReadOnlySpan<char> source, QueryBuffer buffer)
        : this(store, source, buffer, null, null) { }

    internal QueryExecutor(QuadStore store, ReadOnlySpan<char> source, QueryBuffer buffer,
        ISparqlServiceExecutor? serviceExecutor)
        : this(store, source, buffer, serviceExecutor, null) { }

    internal QueryExecutor(QuadStore store, ReadOnlySpan<char> source, QueryBuffer buffer,
        ISparqlServiceExecutor? serviceExecutor, IBufferManager? bufferManager)
    {
        _store = store;
        _source = source.ToString();
        _buffer = buffer;
        _cachedPattern = buffer.BuildGraphPattern();  // Cache for scans
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _stringBuffer = _bufferManager.Rent<char>(1024).Array!;  // Pooled buffer for string operations
        _serviceExecutor = serviceExecutor;
        _maxJoinDepth = DefaultMaxJoinDepth;  // Use default join depth limit

        // Cache first pattern and service clause for SERVICE+local joins
        if (_cachedPattern.PatternCount > 0)
        {
            _cachedFirstPattern = _cachedPattern.GetPattern(0);
        }
        if (_cachedPattern.ServiceClauseCount > 0)
        {
            _cachedFirstServiceClause = _cachedPattern.GetServiceClause(0);
        }

        // Extract temporal parameters from buffer
        _temporalMode = buffer.TemporalMode;
        if (_temporalMode == TemporalQueryMode.AsOf)
        {
            var timeStr = source.Slice(buffer.TimeStartStart, buffer.TimeStartLength);
            _asOfTime = ParseDateTimeOffset(timeStr);
        }
        else if (_temporalMode == TemporalQueryMode.During)
        {
            var startStr = source.Slice(buffer.TimeStartStart, buffer.TimeStartLength);
            var endStr = source.Slice(buffer.TimeEndStart, buffer.TimeEndLength);
            _rangeStart = ParseDateTimeOffset(startStr);
            _rangeEnd = ParseDateTimeOffset(endStr);
        }

        // Extract datasets from buffer
        if (buffer.Datasets != null && buffer.Datasets.Length > 0)
        {
            var defaultList = new List<string>();
            var namedList = new List<string>();

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
        _serviceMaterializer?.Dispose();
        _buffer?.Dispose();
        if (_stringBuffer != null)
            _bufferManager.Return(_stringBuffer);
    }

    /// <summary>
    /// Execute a query with FROM clauses and return lightweight materialized results.
    /// Use this for queries with FROM dataset clauses to avoid stack overflow from large QueryResults struct.
    /// Caller must hold read lock on store.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal MaterializedQueryResults ExecuteFromToMaterialized()
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
        ref readonly var pattern = ref _cachedPattern;
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
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _buffer.Prefixes);
                try
                {
                    while (scan.MoveNext(ref bindingTable))
                    {
                        if (!PassesFilters(in pattern, ref bindingTable))
                        {
                            bindingTable.Clear();
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
            else
            {
                // Multiple patterns - use MultiPatternScan with graph and pushed filters
                // Analyze filters for pushdown
                var patternCount = pattern.RequiredPatternCount;
                var levelFilters = patternCount > 0 && pattern.FilterCount > 0
                    ? FilterAnalyzer.BuildLevelFilters(in pattern, _source, patternCount, null)
                    : null;
                var unpushableFilters = levelFilters != null
                    ? FilterAnalyzer.GetUnpushableFilters(in pattern, _source, null)
                    : null;

                var multiScan = new MultiPatternScan(_store, _source, pattern, false, graphIri.AsSpan(),
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd, null, levelFilters, _buffer.Prefixes);
                try
                {
                    while (multiScan.MoveNext(ref bindingTable))
                    {
                        // Only evaluate unpushable filters - pushed ones were checked in MoveNext
                        if (!PassesUnpushableFilters(in pattern, ref bindingTable, unpushableFilters))
                        {
                            bindingTable.Clear();
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
        }
        else
        {
            // Multiple FROM clauses - use CrossGraphMultiPatternScan
            var crossGraphScan = new CrossGraphMultiPatternScan(_store, _source, pattern, _defaultGraphs);
            try
            {
                while (crossGraphScan.MoveNext(ref bindingTable))
                {
                    if (!PassesFilters(in pattern, ref bindingTable))
                    {
                        bindingTable.Clear();
                        continue;
                    }
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                crossGraphScan.Dispose();
            }
        }

        if (results.Count == 0)
            return MaterializedQueryResults.Empty();

        return new MaterializedQueryResults(results, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <remarks>
    /// ADR-009: [NoInlining] isolates stack frame for 22KB QueryResults and large Query struct access.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public QueryResults Execute()
    {
        // Check for subqueries first - they may have graph context that takes precedence
        // over empty GRAPH clause execution
        if (_buffer.HasSubQueries)
        {
            return ExecuteWithSubQueries();
        }

        // Check for GRAPH clauses - use _buffer to avoid large struct copies
        // A query with GRAPH but no direct triple patterns (only patterns inside GRAPH)
        // Call ExecuteGraphClausesToList() which returns List<MaterializedRow>? (8 bytes)
        // instead of QueryResults (~22KB) to reduce stack pressure
        if (_buffer.HasGraph && _buffer.TriplePatternCount == 0)
        {
            var graphResults = ExecuteGraphClausesToList();
            if (graphResults == null || graphResults.Count == 0)
                return QueryResults.Empty();

            var graphBindings = new Binding[16];
            return QueryResults.FromMaterializedList(graphResults, graphBindings, _stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
        }

        // Check for SERVICE clauses - use _buffer
        // SERVICE execution uses materialization pattern to avoid stack overflow
        // (see docs/mercury-adr-buffer-pattern.md)
        if (_buffer.HasService)
        {
            var serviceResults = ExecuteWithServiceMaterialized();
            if (serviceResults == null || serviceResults.Count == 0)
                return QueryResults.Empty();

            var serviceBindings = new Binding[16];
            return QueryResults.FromMaterializedList(serviceResults, serviceBindings, _stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
        }

        if (_buffer.TriplePatternCount == 0)
            return QueryResults.Empty();

        // Check for FROM clauses (default graph dataset)
        if (_defaultGraphs != null && _defaultGraphs.Length > 0)
        {
            return ExecuteWithDefaultGraphs();
        }

        // For regular queries, use cached pattern from buffer
        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Access cached pattern (from buffer)
        ref readonly var pattern = ref _cachedPattern;
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
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _buffer.Prefixes);

            return new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct,
                _buffer.GetOrderByClause(), _buffer.GetGroupByClause(), _buffer.GetSelectClause(),
                _buffer.GetHavingClause());
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
        ref readonly var pattern = ref _cachedPattern;
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
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _buffer.Prefixes);

                return new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer,
                    _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct,
                    _buffer.GetOrderByClause(), _buffer.GetGroupByClause(), _buffer.GetSelectClause(),
                    _buffer.GetHavingClause());
            }

            if (requiredCount == 0)
                return QueryResults.Empty();

            // Multiple patterns - use MultiPatternScan with graph
            return new QueryResults(
                new MultiPatternScan(_store, _source, pattern, false, graphIri,
                    _temporalMode, _asOfTime, _rangeStart, _rangeEnd, null, null, _buffer.Prefixes),
                _buffer, _source, _store, bindings, stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct,
                _buffer.GetOrderByClause(), _buffer.GetGroupByClause(), _buffer.GetSelectClause(),
                _buffer.GetHavingClause());
        }

        // Multiple FROM clauses - use CrossGraphMultiPatternScan for cross-graph joins
        // This allows joins where pattern1 matches in graph1 and pattern2 matches in graph2
        var crossGraphScan = new CrossGraphMultiPatternScan(_store, _source, pattern, _defaultGraphs);
        return new QueryResults(crossGraphScan, _buffer, _source, _store, bindings, stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct,
            _buffer.GetOrderByClause(), _buffer.GetGroupByClause(), _buffer.GetSelectClause(),
            _buffer.GetHavingClause());
    }

    /// <summary>
    /// Execute an ASK query and return true if any result exists.
    /// Caller must hold read lock on store.
    /// </summary>
    public bool ExecuteAsk()
    {
        // Check for GRAPH clauses - delegate to Execute()
        if (_buffer.HasGraph)
        {
            var results = Execute();
            try { return results.MoveNext(); }
            finally { results.Dispose(); }
        }

        // Check for subqueries - delegate to Execute()
        if (_buffer.HasSubQueries)
        {
            var results = Execute();
            try { return results.MoveNext(); }
            finally { results.Dispose(); }
        }

        // Check for SERVICE clauses - delegate to Execute()
        if (_buffer.HasService)
        {
            var results = Execute();
            try { return results.MoveNext(); }
            finally { results.Dispose(); }
        }

        ref readonly var pattern = ref _cachedPattern;

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
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _buffer.Prefixes);

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
        var multiScan = new MultiPatternScan(_store, _source, pattern, false, default, _buffer.Prefixes);
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
    internal ConstructResults ExecuteConstruct()
    {
        ref readonly var pattern = ref _cachedPattern;
        var template = _buffer.GetConstructTemplate();

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
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _buffer.Prefixes);
            var queryResults = new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer);

            return new ConstructResults(queryResults, template, _source, bindings, stringBuffer);
        }

        // No required patterns
        if (requiredCount == 0)
        {
            return ConstructResults.Empty();
        }

        // Multiple required patterns - need join
        var multiScan = new MultiPatternScan(_store, _source, pattern, false, default, _buffer.Prefixes);
        var multiResults = new QueryResults(multiScan, _buffer, _source, _store, bindings, stringBuffer);

        return new ConstructResults(multiResults, template, _source, bindings, stringBuffer);
    }

    /// <summary>
    /// Execute a DESCRIBE query and return triples describing the matched resources.
    /// Returns all triples where described resources appear as subject or object.
    /// Caller must hold read lock on store.
    /// </summary>
    internal DescribeResults ExecuteDescribe()
    {
        ref readonly var pattern = ref _cachedPattern;
        var describeAll = _buffer.DescribeAll;

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
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _buffer.Prefixes);
            queryResults = new QueryResults(scan, _buffer, _source, _store, bindings, stringBuffer);
        }
        else if (requiredCount == 0)
        {
            return DescribeResults.Empty();
        }
        else
        {
            var multiScan = new MultiPatternScan(_store, _source, pattern, false, default, _buffer.Prefixes);
            queryResults = new QueryResults(multiScan, _buffer, _source, _store, bindings, stringBuffer);
        }

        return new DescribeResults(_store, queryResults, bindings, stringBuffer, describeAll);
    }

    /// <remarks>
    /// ADR-009: [NoInlining] isolates the stack frame for the 22KB QueryResults return value.
    /// Without this, stack frames merge and multiple Execute calls exhaust the 1MB Windows stack.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private QueryResults ExecuteWithJoins()
    {
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;

        // Access pattern via ref to avoid copying
        ref readonly var pattern = ref _cachedPattern;

        // Use nested loop join for required patterns only
        // Pass optimized pattern order if available for join reordering
        return new QueryResults(
            new MultiPatternScan(_store, _source, pattern, false, default,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _optimizedPatternOrder, null, _buffer.Prefixes),
            _buffer,
            _source,
            _store,
            bindings,
            stringBuffer,
            _buffer.Limit,
            _buffer.Offset,
            _buffer.SelectDistinct,
            _buffer.GetOrderByClause(),
            _buffer.GetGroupByClause(),
            _buffer.GetSelectClause(),
            _buffer.GetHavingClause());
    }

    // Helper to create OrderByClause from buffer's OrderByEntry array
    private static OrderByClause CreateOrderByClause(OrderByEntry[] entries)
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
    private static GroupByClause CreateGroupByClause(GroupByEntry[] entries)
    {
        var clause = new GroupByClause();
        foreach (var entry in entries)
        {
            clause.AddVariable(entry.VariableStart, entry.VariableLength);
        }
        return clause;
    }

    // Helper to create SelectClause from buffer
    private SelectClause CreateSelectClause()
    {
        return new SelectClause
        {
            Distinct = _buffer.SelectDistinct,
            SelectAll = _buffer.SelectAll
        };
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
        var stringBuffer = PooledBufferManager.Shared.Rent<char>(2048).Array!;
        try
        {
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
        finally
        {
            PooledBufferManager.Shared.Return(stringBuffer);
        }
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

    /// <summary>
    /// Evaluates only the unpushable filter expressions that weren't pushed to MultiPatternScan.
    /// Used with filter pushdown optimization to avoid evaluating filters twice.
    /// </summary>
    private bool PassesUnpushableFilters(in GraphPattern pattern, ref BindingTable bindingTable, List<int>? unpushableFilters)
    {
        // If no filter analysis was done, fall back to checking all filters
        if (unpushableFilters == null)
            return PassesFilters(in pattern, ref bindingTable);

        // No unpushable filters - all were pushed
        if (unpushableFilters.Count == 0)
            return true;

        // Check only unpushable filters
        foreach (var filterIndex in unpushableFilters)
        {
            var filter = pattern.GetFilter(filterIndex);
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
