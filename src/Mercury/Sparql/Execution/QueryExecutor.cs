using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

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
internal partial class QueryExecutor : IDisposable
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
    private string? _expandedTerm; // Buffer for expanded prefixed names (prevents span-over-temporary)

    // Dataset context: default graph IRIs (FROM) and named graph IRIs (FROM NAMED)
    private readonly string[]? _defaultGraphs;
    private readonly string[]? _namedGraphs;

    // SERVICE clause execution — the tree's ServiceStep evaluates SERVICE against this executor (ADR-047 B3).
    private readonly ISparqlServiceExecutor? _serviceExecutor;

    // Temporal query parameters
    private readonly TemporalQueryMode _temporalMode;
    private readonly DateTimeOffset _asOfTime;
    private readonly DateTimeOffset _rangeStart;
    private readonly DateTimeOffset _rangeEnd;

    // Query optimization
    private readonly QueryPlanner? _planner;

    // Debug properties for testing
    internal bool BufferHasExists => _buffer.HasExists;
    internal int BufferExistsFilterCount => _buffer.ExistsFilterCount;
    internal bool BufferHasGraph => _buffer.HasGraph;
    internal int BufferTriplePatternCount => _buffer.TriplePatternCount;
    internal bool BufferHasSubQueries => _buffer.HasSubQueries;
    internal int BufferHashCode => _buffer.GetHashCode();

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
        // Semantics:
        //   - _namedGraphs = null: no dataset clause, all named graphs accessible
        //   - _namedGraphs = empty array: USING specified without USING NAMED, named graphs inaccessible
        //   - _namedGraphs = [g1, g2, ...]: USING NAMED specified, only those graphs accessible
        if (_buffer.Datasets != null && _buffer.Datasets.Length > 0)
        {
            var defaultList = new List<string>();
            var namedList = new List<string>();

            foreach (var ds in _buffer.Datasets)
            {
                // Expand prefixed names like :g1 to full IRIs like <http://example.org/g1>
                var rawIri = source.Slice(ds.GraphIri.Start, ds.GraphIri.Length);
                var iri = ExpandPrefixedName(rawIri).ToString();
                if (ds.IsNamed)
                    namedList.Add(iri);
                else
                    defaultList.Add(iri);
            }

            if (defaultList.Count > 0) _defaultGraphs = defaultList.ToArray();
            // W3C SPARQL spec: When USING is present without USING NAMED, named graphs become inaccessible
            // This is signaled by setting _namedGraphs to empty array (vs null = no restriction)
            _namedGraphs = namedList.Count > 0 ? namedList.ToArray() : Array.Empty<string>();
        }

        _serviceExecutor = serviceExecutor;

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
        // Semantics:
        //   - _namedGraphs = null: no dataset clause, all named graphs accessible
        //   - _namedGraphs = empty array: USING specified without USING NAMED, named graphs inaccessible
        //   - _namedGraphs = [g1, g2, ...]: USING NAMED specified, only those graphs accessible
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
            // W3C SPARQL spec: When USING is present without USING NAMED, named graphs become inaccessible
            _namedGraphs = namedList.Count > 0 ? namedList.ToArray() : Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer?.Dispose();
        if (_stringBuffer != null)
            _bufferManager.Return(_stringBuffer);
    }

    /// <summary>
    /// Execute any query and return lightweight materialized results (~200 bytes).
    /// Use this instead of Execute() to avoid stack overflow from large QueryResults struct (~22KB).
    /// This method handles all query types: subqueries, GRAPH, SERVICE, FROM, and regular patterns.
    /// Caller must hold read lock on store.
    /// </summary>
    /// <remarks>
    /// ADR-003: This method follows the Buffer Pattern for stack safety by returning
    /// MaterializedQueryResults (~200 bytes) instead of QueryResults (~22KB).
    /// </remarks>
    /// <summary>
    /// Execute the WHERE through the unified tree executor and apply the shared solution-modifier layer. Used by
    /// every path the ADR-045 cutover routes to TreeJoinExecutor: GRAPH-only, VALUES-only, and VALUES + triple
    /// patterns. Returns <see cref="QueryResults.Empty"/> on no rows.
    /// </summary>
    private QueryResults ExecuteViaTreeMaterialized()
    {
        // ADR-047 materialization fix — a simple aggregate (real aggregates, no GROUP BY) over a flat BGP folds each
        // join row straight into the accumulator as the scan produces it (O(1) intermediate memory, zero per-row
        // allocation) instead of materializing the whole intermediate as a List<MaterializedRow> just to reduce it to
        // one row. That materialization is the tree's one regression vs the STREAMING old path (a COUNT over a
        // 1M-row join held 1M rows). Everything else falls through to the materializing path below — including ORDER
        // BY / DISTINCT, which materialize on BOTH paths and so are not a regression.
        if (TryFoldStreamingAggregate(out var folded))
            return folded;

        // ADR-047 — the other "stream when reducible" case: ORDER BY + LIMIT is a top-N, so keep only the N we need in
        // a bounded heap as the scan runs (O(OFFSET+LIMIT) memory) instead of materializing the whole match set just
        // to sort it and slice the first page. Genuine disk spill for ORDER BY *without* a bounding LIMIT would not
        // help peak memory here — QueryResult.Rows materializes the full result regardless (the result floor), so the
        // sorted set is held either way; that case needs streaming result presentation, a separate change.
        if (TryStreamOrderByTopK(out var topk))
            return topk;

        var rows = ExecuteGraphViaTree();
        if (rows.Count == 0)
            return QueryResults.Empty();

        var bindings = new Binding[64];
        return QueryResults.FromMaterializedSimple(rows, _source.AsSpan(), _store, bindings, _stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct,
            _buffer.GetOrderByClause(), _buffer.GetGroupByClause(),
            _buffer.GetSelectClause(), _buffer.GetHavingClause(), _buffer);
    }

    /// <summary>
    /// ADR-047 materialization fix — if the query is a simple aggregate over a FLAT pure-BGP, fold it streaming and
    /// return true with the result; otherwise return false (the caller takes the materializing path). The fold drives
    /// the SAME <see cref="GroupedRow.UpdateAggregates"/> the materializing route uses, so the result is identical —
    /// only the intermediate rows are never collected. Gated (cheaply, before any parse) to a single global group with
    /// only aggregate columns and no modifier the fold does not itself apply: GROUP BY / HAVING / ORDER BY / DISTINCT
    /// fall back (they either materialize on both paths anyway, or need the group(s) post-fold). LIMIT/OFFSET pass
    /// through — the presentation applies them to the one aggregate row. The flat-BGP shape is confirmed after the
    /// parse; nested groups / property paths fall back too.
    /// </summary>
    private bool TryFoldStreamingAggregate(out QueryResults result)
    {
        result = default;

        var select = _buffer.GetSelectClause();
        if (!select.HasRealAggregates) return false;          // not an aggregate query
        if (_buffer.HasPostQueryValues) return false;         // a trailing VALUES joins before the aggregate — the fold would miss it
        if (select.ProjectedVariableCount > 0) return false;  // a plain SELECT var alongside an aggregate — needs the rows
        if (_buffer.GetGroupByClause().HasGroupBy) return false; // grouping is O(groups), not O(1) — not this fold
        if (_buffer.GetHavingClause().HasHaving) return false;   // HAVING filters the group post-fold — fall back
        if (_buffer.GetOrderByClause().HasOrderBy) return false; // ORDER BY — fall back (a no-op on one row, but keep the gate tight)
        if (_buffer.SelectDistinct) return false;                // query-level DISTINCT — fall back

        int whereStart = FindWhereGroupStart();
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(whereStart, ref tree);
            if (!TreeJoinExecutor.IsFlatBgp(ref tree, root))
                return false; // nested group / property path — the materializing path handles those

            // Fold the flat BGP into one global group (no GROUP BY ⇒ KeyCount 0, so the throwaway BindingTable below is
            // unread). Zero input rows still finalize to the default aggregate values (COUNT 0, etc.) — SPARQL's
            // implicit-aggregation-of-the-empty-set rule, the same one the materializing path applies.
            var groupBindingArray = new Binding[16];
            var groupCharArray = new char[256];
            var groupBindings = new BindingTable(groupBindingArray, groupCharArray);
            var group = new GroupedRow(default, select, groupBindings, _source);

            new TreeJoinExecutor(_store, _source, _prefixMappings, _source, _serviceExecutor,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _namedGraphs, _defaultGraphs, ReorderBgpInTree)
                .FoldFlatBgpAggregate(ref tree, root, "", group);

            group.FinalizeAggregates();

            var groups = new List<GroupedRow>(1) { group };
            var bindings = new Binding[64];
            result = QueryResults.FromFinalizedGroups(groups, _source.AsSpan(), _store, bindings, _stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct, select);
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>
    /// ADR-047 memory bound for must-materialize ORDER BY — if the query is ORDER BY + LIMIT over a FLAT pure-BGP,
    /// stream it into a bounded top-K heap (retains OFFSET+LIMIT rows) and return true with the result; otherwise
    /// return false (the caller takes the materializing path). A top-N is reducible: only the first page is kept, so
    /// the whole match set never lives in memory. Gated to a bounding LIMIT (without one, top-K cannot bound it and
    /// QueryResult.Rows materializes the full result anyway), no DISTINCT / GROUP BY / aggregate / HAVING, and a
    /// capacity within a memory-adaptive cap (a pathological OFFSET+LIMIT falls back rather than OOM the heap). The
    /// kept rows are re-sorted (cheap, ≤ capacity) and paged by the shared modifier layer — identical bag to the old
    /// path, which sorts the whole set then slices the same page.
    /// </summary>
    private bool TryStreamOrderByTopK(out QueryResults result)
    {
        result = default;

        var orderBy = _buffer.GetOrderByClause();
        if (!orderBy.HasOrderBy) return false;        // not an ORDER BY query
        if (_buffer.HasPostQueryValues) return false; // a trailing VALUES joins (multiplies) before the page — top-K would miss it
        if (_buffer.Limit <= 0) return false;         // no bounding LIMIT — top-K cannot bound it (result floor)
        if (_buffer.SelectDistinct) return false;     // DISTINCT must dedup before the top-N — fall back
        var select = _buffer.GetSelectClause();
        if (select.HasRealAggregates) return false;   // aggregates go through the fold / grouped path
        if (_buffer.GetGroupByClause().HasGroupBy) return false;
        if (_buffer.GetHavingClause().HasHaving) return false;

        // The heap retains `capacity` (= OFFSET + LIMIT) rows. Bound that by a fraction of available physical memory so
        // a pathological OFFSET+LIMIT cannot itself OOM the heap; above the cap, fall back to full materialization (the
        // streaming-presentation territory, not this fold). This is the "adapted to system memory availability" budget.
        long capacity64 = (long)_buffer.Offset + _buffer.Limit;
        long budget = SkyOmega.Mercury.Runtime.ProcessMemoryProbe.AvailablePhysicalBytes() / 8;
        const long AssumedRowBytes = 512;
        long capCap = System.Math.Max(100_000, budget / AssumedRowBytes);
        if (capacity64 <= 0 || capacity64 > capCap) return false;
        int capacity = (int)capacity64;

        int whereStart = FindWhereGroupStart();
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(whereStart, ref tree);
            if (!TreeJoinExecutor.IsFlatBgp(ref tree, root))
                return false; // nested group / property path — full materialization handles those

            var comparer = new MaterializedRowComparer(orderBy, _source);
            var rows = new TreeJoinExecutor(_store, _source, _prefixMappings, _source, _serviceExecutor,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _namedGraphs, _defaultGraphs, ReorderBgpInTree)
                .StreamFlatBgpTopK(ref tree, root, "", comparer, capacity);

            var bindings = new Binding[64];
            result = QueryResults.FromMaterializedSimple(rows, _source.AsSpan(), _store, bindings, _stringBuffer,
                _buffer.Limit, _buffer.Offset, distinct: false,
                orderBy, default, select, default, _buffer);
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>
    /// ADR-045 cutover: evaluate the GRAPH-clause WHERE group through the unified zero-GC tree executor. Parses the
    /// WHERE group over the FULL query (so the tree's slot offsets index the same source as the prologue prefixes),
    /// threads the active graph per pattern and the dataset (USING / FROM NAMED) restriction, and returns
    /// materialized rows for the shared modifier layer.
    /// </summary>
    private List<MaterializedRow> ExecuteGraphViaTree(int askExistenceCap = 0)
    {
        int whereStart = FindWhereGroupStart();
        // Pool the pattern-tree buffer — it is per-query setup, not the hot path, but renting keeps the GRAPH path's
        // per-query allocation bounded by the result rows rather than a fixed ~64 KB buffer each call.
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(whereStart, ref tree);

            // LIMIT-pushdown: a pure-BGP GRAPH query with LIMIT and no order/group/aggregate/distinct/having can stop
            // the scan after OFFSET+LIMIT rows instead of materializing the whole match set then truncating
            // (ck:obs-graph-limit-pushdown). Any of those modifiers needs the full result first, so no cap there.
            int maxRows = int.MaxValue;
            // ADR-047 A2 — ASK existence probe: a solution exists iff the FIRST row does. A pure BGP has no post-join
            // filter, so the first matched row IS a real solution; cap the scan at 1 (the unconstrained ASK { ?s ?p ?o }
            // short-circuits instead of materializing the store). A group with a FILTER / EXISTS / operator needs the
            // whole group first (the cap is left off — the tree's EvalGroup-then-test existence primitive applies).
            if (askExistenceCap > 0 && TreeJoinExecutor.IsPureBgp(ref tree, root))
                maxRows = askExistenceCap;
            else if (_buffer.Limit > 0
                && !_buffer.GetOrderByClause().HasOrderBy
                && !_buffer.GetGroupByClause().HasGroupBy
                && !_buffer.GetHavingClause().HasHaving
                && _buffer.GetSelectClause().AggregateCount == 0
                && !_buffer.SelectDistinct
                && !_buffer.HasPostQueryValues   // a trailing VALUES joins AFTER the BGP — it can multiply rows, so the cap is invalid
                && TreeJoinExecutor.IsPureBgp(ref tree, root))
            {
                long cap = (long)_buffer.Offset + _buffer.Limit;
                if (cap < int.MaxValue) maxRows = (int)cap;
            }

            // ADR-024: text:match trigram pre-filter — resolve candidate object atom IDs per text:match variable so the
            // tree's scan filters at the index level (candidate-narrowing that is mandatory at scale).
            Dictionary<string, HashSet<long>>? trigramMap = null;
            var textHints = FilterAnalyzer.DetectTextMatchFilters(in _cachedPattern, _source.AsSpan(), null);
            if (textHints != null)
                foreach (var hint in textHints)
                {
                    var candidates = _store.QueryTrigramCandidates(hint.SearchTerm.AsSpan());
                    if (candidates.Count > 0)
                        (trigramMap ??= new Dictionary<string, HashSet<long>>())[hint.VarName] = new HashSet<long>(candidates);
                }

            var executor = new TreeJoinExecutor(_store, _source, _prefixMappings, _source, _serviceExecutor,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _namedGraphs, _defaultGraphs, ReorderBgpInTree, trigramMap);
            var rows = executor.Evaluate(ref tree, root, "", maxRows);

            // A trailing (post-query) VALUES is JOINED with the whole-pattern solution (multiply + bind), not filtered.
            if (_buffer.HasPostQueryValues)
                rows = executor.JoinPostQueryValues(rows, _buffer.PostQueryValues);

            return rows;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>
    /// Locate THIS-statement's WHERE-group opening '{'. The source can be a whole multi-statement UPDATE (a naive text
    /// search for "WHERE" would find the wrong statement, and the first '{' is the DELETE/INSERT template), so anchor on
    /// the buffer, which is per-statement.
    /// <para>
    /// Primary: bound the search by this statement's earliest triple-pattern term (a per-statement offset) and take the
    /// WHERE keyword at or before it; the WHERE group's '{' is the first '{' after that keyword. That skips a preceding
    /// DELETE/INSERT template '{ … }' and any earlier statement's WHERE. Crucially it is also robust to a GRAPH header
    /// nested inside EXISTS / MINUS / a sub-group: keying off the first graph header (the fallback below) would pick the
    /// brace of THAT nested group, so the tree parsed and ran only the sub-pattern as the whole query — dropping the
    /// outer BGP and the EXISTS/MINUS semantics (this is the exists-graph-variable over-match,
    /// ck:bug-findwheregroupstart-nested-graph).
    /// </para>
    /// <para>
    /// Fallback (no top-level triple, or no WHERE keyword — e.g. a GRAPH-clause-only group <c>SELECT * { GRAPH ?g { … } }</c>):
    /// the WHERE group encloses the GRAPH clause, so the '{' is the one immediately before the first (outermost,
    /// document-order) graph header's term.
    /// </para>
    /// </summary>
    private int FindWhereGroupStart()
    {
        int firstTerm = FirstPatternTermStart();
        if (firstTerm >= 0)
        {
            int whereKw = LastWhereKeywordAtOrBefore(firstTerm);
            if (whereKw >= 0)
            {
                int brace = _source.IndexOf('{', whereKw);
                if (brace >= 0 && brace <= firstTerm) return brace;
            }
        }

        var patterns = _buffer.GetPatterns();
        var headers = patterns.EnumerateGraphHeaders();
        if (headers.MoveNext())
        {
            int termStart = System.Math.Min(headers.Current.GraphTermStart, _source.Length - 1);
            int brace = _source.LastIndexOf('{', termStart);
            if (brace >= 0) return brace;
        }

        return _source.IndexOf('{');
    }

    /// <summary>The least source offset among this statement's cached triple-pattern subjects, or -1 if there are none.</summary>
    private int FirstPatternTermStart()
    {
        ref readonly var pattern = ref _cachedPattern;
        int min = -1;
        for (int i = 0; i < pattern.PatternCount; i++)
        {
            int s = pattern.GetPattern(i).Subject.Start;
            if (s >= 0 && (min < 0 || s < min)) min = s;
        }
        // A sub-SELECT-only WHERE has no top-level triple to anchor on, but its content still sits INSIDE the WHERE
        // group — anchor on the sub-query's earliest term (pattern subject / projected var / aggregate alias) so
        // FindWhereGroupStart locates THIS statement's WHERE. Without it, an `INSERT { … } WHERE { SELECT (COUNT(*) …) }`
        // in a multi-statement UPDATE falls back to IndexOf('{'), which grabs the INSERT template brace.
        for (int q = 0; q < pattern.SubQueryCount; q++)
        {
            var sq = pattern.GetSubQuery(q);
            for (int i = 0; i < sq.PatternCount; i++)
            {
                int s = sq.GetPattern(i).Subject.Start;
                if (s >= 0 && (min < 0 || s < min)) min = s;
            }
            for (int i = 0; i < sq.ProjectedVarCount; i++)
            {
                int s = sq.GetProjectedVariable(i).Start;
                if (s >= 0 && (min < 0 || s < min)) min = s;
            }
            for (int i = 0; i < sq.AggregateCount; i++)
            {
                int s = sq.GetAggregate(i).AliasStart;
                if (s >= 0 && (min < 0 || s < min)) min = s;
            }
        }
        return min;
    }

    /// <summary>
    /// The start index of the last standalone "WHERE" keyword token (case-insensitive, with non-alphanumeric
    /// boundaries) at or before <paramref name="limit"/>, or -1 if there is none. String literals are skipped so a
    /// "where" inside one is not mistaken for the keyword. Anchors the WHERE group within a multi-statement UPDATE.
    /// </summary>
    private int LastWhereKeywordAtOrBefore(int limit)
    {
        int found = -1;
        int depth = 0; // brace depth — a WHERE nested in a sub-SELECT / EXISTS / group body sits at depth > 0 and is NOT
                       // this statement's group opener (the same nested-WHERE over-match the flip fixed for GRAPH/EXISTS).
        for (int i = 0; i + 5 <= _source.Length && i <= limit; i++)
        {
            char ch = _source[i];
            if (ch == '"' || ch == '\'')
            {
                i++;
                while (i < _source.Length && _source[i] != ch) { if (_source[i] == '\\') i++; i++; }
                continue;
            }
            if (ch == '{') { depth++; continue; }
            if (ch == '}') { if (depth > 0) depth--; continue; }
            if (depth != 0) continue; // only a top-level WHERE opens this statement's group
            if ((ch | 0x20) != 'w' || (_source[i + 1] | 0x20) != 'h' || (_source[i + 2] | 0x20) != 'e'
                || (_source[i + 3] | 0x20) != 'r' || (_source[i + 4] | 0x20) != 'e')
                continue;
            if (i > 0 && (char.IsLetterOrDigit(_source[i - 1]) || _source[i - 1] == '_')) continue;
            int after = i + 5;
            if (after < _source.Length && (char.IsLetterOrDigit(_source[after]) || _source[after] == '_')) continue;
            found = i;
        }
        return found;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal MaterializedQueryResults ExecuteToMaterialized()
    {
        // ADR-047 B1: sub-queries route through the unified tree (SubSelectStep) — see Execute(). GRAPH-only falls into
        // the GRAPH branch below; a sub-SELECT joined with outer triples falls through to the regular tree route.

        // Check for GRAPH clauses (no top-level patterns)
        if (_buffer.HasGraph && _buffer.TriplePatternCount == 0)
        {
            // ADR-045 cutover: the unified zero-GC tree executor (see ExecuteGraphViaTree).
            return WrapResultsAsMaterialized(ExecuteGraphViaTree());
        }

        // ADR-047 B2/B3: FROM (default-graph dataset) and SERVICE (federation) materialize through the unified tree too.
        // _defaultGraphs reaches the TreeJoinExecutor (see ExecuteGraphViaTree), whose default-context scan unions the
        // FROM graphs; a SERVICE clause is a ServiceHeader the tree's ServiceStep evaluates against the injected
        // ISparqlServiceExecutor and joins. The old separate FROM/SERVICE/cross-graph executor paths are deleted;
        // ServiceMaterializer.cs is retained (unwired) for the ADR-048 federation-efficiency work.

        // Empty pattern case
        if (_buffer.TriplePatternCount == 0)
        {
            // A sub-SELECT-only, VALUES-only, or SERVICE-only WHERE has no top-level triples but is real content — route
            // it to the tree (SubSelectStep / ValuesStep / ServiceStep), not the empty-pattern path (ADR-047 B1/B3).
            if (_buffer.HasSubQueries || _buffer.HasValues || _buffer.HasService)
                return WrapResultsAsMaterialized(ExecuteGraphViaTree());
            // For empty patterns with BIND/expressions, execute and collect results
            return ExecuteEmptyPatternToMaterialized();
        }

        // ADR-047 A3: the regular (non-carve-out) WHERE materializes through the unified tree — identical to the GRAPH
        // case above (WrapResultsAsMaterialized over ExecuteGraphViaTree). The tree has already applied the BGP join,
        // FILTER and (NOT) EXISTS; MaterializedQueryResults applies LIMIT / OFFSET / DISTINCT. This is the test-only
        // materialized entry (QueryExecutorTests); production SELECT goes through Execute(). The old
        // ExecuteRegularPatternToMaterialized is now dead — deleted in phase C.
        return WrapResultsAsMaterialized(ExecuteGraphViaTree());
    }

    /// <summary>
    /// Helper to wrap List&lt;MaterializedRow&gt; into MaterializedQueryResults.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private MaterializedQueryResults WrapResultsAsMaterialized(List<MaterializedRow>? results)
    {
        if (results == null || results.Count == 0)
            return MaterializedQueryResults.Empty();

        var bindings = new Binding[16];
        return new MaterializedQueryResults(results, bindings, _stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>
    /// Execute an empty pattern (WHERE {}) and return materialized results.
    /// For empty patterns with BIND/expressions, returns a single row with evaluated expressions.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private MaterializedQueryResults ExecuteEmptyPatternToMaterialized()
    {
        var selectClause = _buffer.GetSelectClause();
        if (selectClause.AggregateCount == 0 && !_buffer.HasBinds && !_buffer.HasFilters && !_buffer.HasExists)
            return MaterializedQueryResults.Empty();

        // For empty patterns with expressions, return a single row with evaluated expressions
        var bindings = new Binding[16];
        var bindingTable = new BindingTable(bindings, _stringBuffer);

        // Get base IRI for relative IRI resolution
        var baseIri = _buffer.BaseUriLength > 0
            ? _source.AsSpan(_buffer.BaseUriStart, _buffer.BaseUriLength)
            : ReadOnlySpan<char>.Empty;

        // Evaluate BIND expressions first (e.g., BIND(UUID() AS ?uuid))
        if (_buffer.HasBinds)
        {
            ref readonly var pattern = ref _cachedPattern;
            for (int i = 0; i < _buffer.BindCount; i++)
            {
                var bind = pattern.GetBind(i);
                var expr = _source.AsSpan(bind.ExprStart, bind.ExprLength);
                var varName = _source.AsSpan(bind.VarStart, bind.VarLength);

                var evaluator = new BindExpressionEvaluator(expr,
                    bindings.AsSpan(0, bindingTable.Count),
                    bindingTable.Count,
                    _stringBuffer,
                    baseIri);
                var value = evaluator.Evaluate(_prefixMappings, _source);

                // Bind the result using typed overloads
                switch (value.Type)
                {
                    case ValueType.Integer:
                        bindingTable.Bind(varName, value.IntegerValue);
                        break;
                    case ValueType.Double:
                        bindingTable.Bind(varName, value.DoubleValue);
                        break;
                    case ValueType.Boolean:
                        bindingTable.Bind(varName, value.BooleanValue);
                        break;
                    case ValueType.String:
                    case ValueType.Uri:
                        bindingTable.Bind(varName, value.StringValue);
                        break;
                }
            }
        }

        // Evaluate SELECT expressions (e.g., (BNODE() AS ?b1))
        for (int i = 0; i < selectClause.AggregateCount; i++)
        {
            var agg = selectClause.GetAggregate(i);

            // Only evaluate non-aggregate expressions (Function == None)
            if (agg.Function != AggregateFunction.None) continue;

            // Skip if no expression to evaluate
            if (agg.VariableLength == 0) continue;

            // Get expression and alias
            var expr = _source.AsSpan(agg.VariableStart, agg.VariableLength);
            var aliasName = _source.AsSpan(agg.AliasStart, agg.AliasLength);

            // Evaluate the expression
            var evaluator = new BindExpressionEvaluator(expr,
                bindings.AsSpan(0, bindingTable.Count),
                bindingTable.Count,
                _stringBuffer,
                baseIri);
            var value = evaluator.Evaluate(_prefixMappings, _source);

            // Bind the result to the alias variable
            switch (value.Type)
            {
                case ValueType.Integer:
                    bindingTable.Bind(aliasName, value.IntegerValue);
                    break;
                case ValueType.Double:
                    bindingTable.Bind(aliasName, value.DoubleValue);
                    break;
                case ValueType.Boolean:
                    bindingTable.Bind(aliasName, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.Uri:
                    bindingTable.Bind(aliasName, value.StringValue);
                    break;
            }
        }

        // Evaluate FILTER if any (may reject the single row)
        if (_buffer.HasFilters)
        {
            ref readonly var pattern = ref _cachedPattern;
            for (int i = 0; i < pattern.FilterCount; i++)
            {
                var filter = pattern.GetFilter(i);
                var filterExpr = _source.AsSpan(filter.Start, filter.Length);
                var filterEvaluator = new FilterEvaluator(filterExpr);
                if (!filterEvaluator.Evaluate(bindings.AsSpan(0, bindingTable.Count),
                    bindingTable.Count, _stringBuffer, _buffer.Prefixes, _source))
                    return MaterializedQueryResults.Empty();
            }
        }

        var results = new List<MaterializedRow> { new MaterializedRow(bindingTable) };
        return new MaterializedQueryResults(results, bindings, _stringBuffer,
            _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
    }

    /// <summary>The store's result-row cap (<c>StorageOptions.MaxResultRows</c>; 0 = unbounded) — the drain loops
    /// check it via <see cref="ResultLimitExceededException.ThrowIfExceeded"/>. The unbounded-result guard.</summary>
    internal long MaxResultRows => _store.MaxResultRows;

    /// <summary>
    /// ADR-047 spike: when routing through the tree, reorder each BGP run by selectivity (the QueryPlanner model)
    /// before the nested-loop join. Benchmark-only knob to measure planned-tree vs unplanned-tree.
    /// </summary>
    internal bool ReorderBgpInTree { get; set; }

    /// <remarks>
    /// ADR-009: [NoInlining] isolates stack frame for 22KB QueryResults and large Query struct access.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public QueryResults Execute()
    {
        // ADR-047 B1: sub-queries route through the unified tree like everything else — a { SELECT … } in the WHERE is
        // a SubSelectHeader the tree's SubSelectStep evaluates (threading the active graph, so GRAPH-context and
        // per-graph-aggregate sub-SELECTs work). A GRAPH-only sub-query falls into the GRAPH branch below (also the
        // tree); a sub-SELECT joined with outer triples falls through to the default tree dispatch. The old separate
        // sub-query executor path is deleted.

        // Check for GRAPH clauses - use _buffer to avoid large struct copies
        // A query with GRAPH but no direct triple patterns (only patterns inside GRAPH)
        // Call ExecuteGraphClausesToList() which returns List<MaterializedRow>? (8 bytes)
        // instead of QueryResults (~22KB) to reduce stack pressure
        if (_buffer.HasGraph && _buffer.TriplePatternCount == 0)
        {
            // ADR-045 cutover: route GRAPH queries through the unified zero-GC tree executor ("a default graph is
            // also a graph") and the shared FromMaterializedSimple modifier layer — the same one the default path
            // uses. This replaces the divergent ExecuteGraphClausesToList + FromMaterializedWithGraphContext path.
            return ExecuteViaTreeMaterialized();
        }

        // ADR-047 B3: SERVICE (federation) runs through the unified tree like everything else — a SERVICE clause is a
        // ServiceHeader the tree's ServiceStep evaluates against the injected ISparqlServiceExecutor and joins with the
        // outer rows. UNION-with-SERVICE, OPTIONAL { SERVICE }, multiple SERVICE, and SERVICE ?ep all fall out of the
        // tree's existing UnionHeader / OptionalHeader / sequential-Join machinery — no per-shape orchestration. The old
        // SERVICE executor path is deleted; ServiceMaterializer.cs is retained (unwired) for ADR-048 federation efficiency.

        if (_buffer.TriplePatternCount == 0)
        {
            // A WHERE with no top-level triples is NOT necessarily empty. VALUES-only inline data, a sub-SELECT-only
            // body ({ SELECT … }), or a SERVICE-only body is real content the unified tree materializes — the
            // empty-pattern path below only checks aggregate/BIND/FILTER/EXISTS, so it would wrongly return nothing.
            // ADR-047 B1/B3: route those to the tree (SubSelectStep / ValuesStep / ServiceStep).
            if (_buffer.HasValues || _buffer.HasSubQueries || _buffer.HasService)
                return ExecuteViaTreeMaterialized();

            // Check if there are BIND, FILTER, EXISTS, or SELECT expressions to evaluate
            // (e.g., SELECT (REPLACE(...) AS ?new) WHERE {} or SELECT ?x WHERE { BIND(UUID() AS ?x) })
            var selectClause = _buffer.GetSelectClause();
            if (selectClause.AggregateCount > 0 || _buffer.HasBinds || _buffer.HasFilters || _buffer.HasExists)
            {
                // Empty pattern with expressions - return one row with computed values
                var emptyBindings = new Binding[16];
                return QueryResults.EmptyPattern(_buffer, _source.AsSpan(), emptyBindings, _stringBuffer, selectClause, _store);
            }
            return QueryResults.Empty();
        }

        // ADR-047 B2: a FROM dataset (default-graph clauses) runs through the unified tree like everything else — the
        // FROM graphs are passed to the TreeJoinExecutor as _defaultGraphs, and a default-context pattern scans their
        // UNION (their RDF merge IS the default graph, SPARQL §13.2). The old separate FROM / cross-graph executor
        // path is deleted.

        // ADR-047 CUTOVER (the flip): the default query path — plain BGP AND inline VALUES + triple — runs through the
        // unified zero-GC tree executor, the same one GRAPH queries use (ADR-045, "a default graph is also a graph").
        // Validated at 1TB WDBench breadth: the tree is correct (more correct than the old contaminated oracle on
        // var-predicate joins, chained OPTIONAL, and path reflexives) and parity-to-4x faster, never the slower path.
        // The two former VALUES+triple gaps are CLOSED: numeric/boolean VALUES tokens canonicalize to a typed literal
        // before the join (ExpandValueTerm→LiteralForm, the routine TriplePatternScan also applies to a constant), and
        // a zero-length-path reflexive over a VALUES-bound term is gated on graph-node membership (SPARQL §9.3); a
        // VALUES after a triple now correctly cross-joins (SPARQL §18) where the old path silently dropped it. As of
        // B1/B2/B3 every former carve-out (sub-query / FROM / SERVICE) routes through the tree above; the old slot
        // executor is fully unreachable, deleted in phase C.
        return ExecuteViaTreeMaterialized();
    }

    /// <summary>
    /// Execute query and materialize all results to a heap-allocated list.
    /// Returns a small QueryResults wrapper (~200 bytes) instead of the full struct (~90KB).
    /// This prevents stack overflow when results are passed through many call frames.
    /// </summary>
    /// <remarks>
    /// Use this method instead of Execute() when:
    /// - Running many queries in sequence (e.g., test suites)
    /// - Results need to be passed through multiple call frames
    /// - Stack space is limited (e.g., Windows default 1MB stack)
    ///
    /// Trade-off: Allocates all results upfront (no streaming). For very large result
    /// sets, consider using Execute() with streaming consumption instead.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public QueryResults ExecuteMaterialized()
    {
        // Create the large QueryResults on THIS stack frame only
        var results = Execute();
        try
        {
            // Materialize all results to heap
            var rows = new List<MaterializedRow>();
            while (results.MoveNext())
            {
                rows.Add(new MaterializedRow(results.Current));
            }

            // Return tiny wrapper backed by heap list
            var bindings = new Binding[16];
            return QueryResults.FromMaterializedList(rows, bindings, _stringBuffer,
                _buffer.Limit, _buffer.Offset, _buffer.SelectDistinct);
        }
        finally
        {
            results.Dispose();
        }
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

        // Build binding storage (needed for empty pattern with filters too)
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Empty pattern: if there are binds/filters, evaluate them
        // ASK { BIND(RAND() AS ?r) FILTER(DATATYPE(?r) = xsd:double) } should work
        if (pattern.PatternCount == 0)
        {
            // First evaluate any BIND expressions
            if (_buffer.BindCount > 0)
            {
                var baseIri = _buffer.BaseUriLength > 0
                    ? _source.AsSpan(_buffer.BaseUriStart, _buffer.BaseUriLength)
                    : ReadOnlySpan<char>.Empty;

                for (int i = 0; i < _buffer.BindCount; i++)
                {
                    var bind = pattern.GetBind(i);
                    var expr = _source.AsSpan(bind.ExprStart, bind.ExprLength);
                    var varName = _source.AsSpan(bind.VarStart, bind.VarLength);

                    var evaluator = new BindExpressionEvaluator(expr,
                        bindings.AsSpan(0, bindingTable.Count),
                        bindingTable.Count,
                        stringBuffer,
                        baseIri);
                    var value = evaluator.Evaluate(_prefixMappings, _source);

                    // Bind the result using typed overloads
                    switch (value.Type)
                    {
                        case ValueType.Integer:
                            bindingTable.Bind(varName, value.IntegerValue);
                            break;
                        case ValueType.Double:
                            bindingTable.Bind(varName, value.DoubleValue);
                            break;
                        case ValueType.Boolean:
                            bindingTable.Bind(varName, value.BooleanValue);
                            break;
                        case ValueType.String:
                        case ValueType.Uri:
                            bindingTable.Bind(varName, value.StringValue);
                            break;
                    }
                }
            }

            if (_buffer.FilterCount > 0)
            {
                // Evaluate all filters - all must pass for ASK to return true
                for (int i = 0; i < _buffer.FilterCount; i++)
                {
                    var filter = pattern.GetFilter(i);
                    var filterExpr = _source.AsSpan(filter.Start, filter.Length);
                    var evaluator = new FilterEvaluator(filterExpr);
                    if (!evaluator.Evaluate(bindings.AsSpan(0, bindingTable.Count), bindingTable.Count, stringBuffer, _buffer.Prefixes, _source))
                        return false;
                }
                return true;
            }
            // Has BINDs but no filters - return true if we got here
            if (_buffer.BindCount > 0)
                return true;
            return false;
        }

        // ADR-047 A2/B2: a non-empty WHERE's existence goes through the unified tree — the same evaluator SELECT and
        // CONSTRUCT use. A pure BGP short-circuits at the first matched row (askExistenceCap: 1, safe because nothing
        // filters after the join); a group with FILTER / EXISTS / an operator is evaluated whole (the tree's existence
        // primitive, the one ExistsBodyHasMatch uses) and tested non-empty. An all-OPTIONAL group keeps the empty-row
        // seed, so it correctly ASKs true — the old path's requiredCount==0 → false silently dropped that solution. A
        // FROM dataset is honoured here too (the tree unions _defaultGraphs), so ASK FROM tests the FROM graphs — the
        // old slot path this never reached ignored FROM and tested the unnamed default (a latent bug the cutover fixes).
        return ExecuteGraphViaTree(askExistenceCap: 1).Count > 0;
    }

    /// <summary>
    /// Execute a CONSTRUCT query and return constructed triples.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public ConstructResults ExecuteConstruct()
    {
        ref readonly var pattern = ref _cachedPattern;
        var template = _buffer.GetConstructTemplate();

        if (!template.HasPatterns)
            return ConstructResults.Empty();

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = _stringBuffer;

        // A CONSTRUCT WHERE with neither top-level triples nor a sub-SELECT is genuinely empty. A sub-SELECT-only WHERE
        // ({ SELECT … }) IS real content (the tree's SubSelectStep evaluates it), so it must NOT short-circuit to empty.
        if (pattern.PatternCount == 0 && !_buffer.HasSubQueries)
            return ConstructResults.Empty();

        // ADR-047 A2/B2/C2: the WHERE — BGP, FROM dataset, OR a sub-SELECT — evaluates through the unified tree; the
        // materialized rows (all variables bound, no projection) ConstructResults reads via .Current. C2 routed
        // CONSTRUCT-with-subquery here too (B1 had cut SELECT/ASK). A FROM dataset is honoured by the tree
        // (_defaultGraphs union, B2). The old separate sub-query / slot-scan executor path is deleted.
        return new ConstructResults(
            QueryResults.FromMaterializedRows(ExecuteGraphViaTree(), _source, _store, bindings, stringBuffer),
            template, _source, bindings, stringBuffer, _buffer.Prefixes);
    }

    /// <summary>
    /// Execute a DESCRIBE query and return triples describing the matched resources.
    /// Returns all triples where described resources appear as subject or object.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public DescribeResults ExecuteDescribe()
    {
        ref readonly var pattern = ref _cachedPattern;
        var describeAll = _buffer.DescribeAll;

        // If no WHERE clause, return empty
        if (pattern.PatternCount == 0)
            return DescribeResults.Empty();

        // ADR-047 A2/B2: evaluate the WHERE through the unified tree to collect the resources to describe — the
        // materialized rows (all variables bound, no projection) DescribeResults reads via .Current. A FROM dataset is
        // honoured here too (the tree's default-context scan unions _defaultGraphs, see ExecuteGraphViaTree): the WHERE
        // matches against the dataset's default graph, the union of the FROM graphs (SPARQL §16.4 evaluates it against
        // the dataset). No FROM ⇒ the real unnamed default graph, as before.
        var bindings = new Binding[16];
        var queryResults = QueryResults.FromMaterializedRows(ExecuteGraphViaTree(), _source, _store, bindings, _stringBuffer);

        return new DescribeResults(_store, queryResults, bindings, _stringBuffer, describeAll);
    }

    // Helper to create OrderByClause from buffer's OrderByEntry array

    // Helper to create GroupByClause from buffer's GroupByEntry array

    // Helper to create SelectClause from buffer

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
            // Pass prefixes for prefix expansion in filter expressions (e.g., ?a = :s1)
            if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer(),
                _buffer.Prefixes, _source.AsSpan()))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Expands a prefixed name to its full IRI using the prologue prefix mappings.
    /// Also handles 'a' shorthand for rdf:type.
    /// Returns the original span if not a prefixed name or no matching prefix found.
    /// </summary>
    private ReadOnlySpan<char> ExpandPrefixedName(ReadOnlySpan<char> term)
    {
        // Skip if already a full IRI, literal, or blank node
        if (term.Length == 0 || term[0] == '<' || term[0] == '"' || term[0] == '_')
            return term;

        // Handle 'a' shorthand for rdf:type (SPARQL keyword)
        if (term.Length == 1 && term[0] == 'a')
            return SyntheticTermHelper.RdfType.AsSpan();

        // Look for colon indicating prefixed name
        var colonIdx = term.IndexOf(':');
        if (colonIdx < 0)
            return term;

        var prefixCount = _prologue.PrefixCount;
        if (prefixCount == 0)
            return term;

        // Include the colon in the prefix (stored prefixes include trailing colon, e.g., "ex:")
        var prefixWithColon = term.Slice(0, colonIdx + 1);
        var localPart = term.Slice(colonIdx + 1);

        // Find matching prefix in mappings
        for (int i = 0; i < prefixCount; i++)
        {
            var (prefixStart, prefixLength, iriStart, iriLength) = _prologue.GetPrefix(i);
            var mappingPrefix = _source.AsSpan(prefixStart, prefixLength);
            if (prefixWithColon.SequenceEqual(mappingPrefix))
            {
                // Found matching prefix, expand to full IRI
                // The IRI is stored with angle brackets, e.g., "<http://example.org/>"
                var iriBase = _source.AsSpan(iriStart, iriLength);

                // Strip angle brackets from IRI base if present, then build full IRI
                var iriContent = iriBase;
                if (iriContent.Length >= 2 && iriContent[0] == '<' && iriContent[^1] == '>')
                    iriContent = iriContent.Slice(1, iriContent.Length - 2);

                // Build full IRI: <base + localPart>
                _expandedTerm = $"<{iriContent.ToString()}{localPart.ToString()}>";
                return _expandedTerm.AsSpan();
            }
        }

        return term;
    }
}
