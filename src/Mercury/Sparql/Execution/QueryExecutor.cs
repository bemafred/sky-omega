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
        // Store prologue for prefix expansion
        _prologue = query.Prologue;

        // Extract prefix mappings for subquery execution (shared with the other executors)
        _prefixMappings = PrefixExpander.Extract(in _prologue);

        ResolveDataset(_buffer.Datasets, source, _prefixMappings, out _defaultGraphs, out _namedGraphs);

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
    /// Resolve a dataset clause list — a query's FROM / FROM NAMED or an Update's USING / USING NAMED — into the
    /// tree executor's graph sets (prefixed names expanded against <paramref name="prefixes"/>):
    /// <list type="bullet">
    /// <item><paramref name="defaultGraphs"/>: null = no dataset clause, the store's unnamed default graph; empty = a
    /// dataset without FROM / USING, whose default graph is empty (§13.2); [g…] = those graphs, whose merge is the
    /// default graph.</item>
    /// <item><paramref name="namedGraphs"/>: null = no dataset clause, every named graph is accessible; empty = a
    /// dataset without FROM NAMED / USING NAMED, so no named graph is; [g…] = only those.</item>
    /// </list>
    /// Each is a set: a graph named twice is in it once.
    /// </summary>
    internal static void ResolveDataset(DatasetClause[]? datasets, ReadOnlySpan<char> source, PrefixMapping[]? prefixes,
        out string[]? defaultGraphs, out string[]? namedGraphs)
    {
        defaultGraphs = null;
        namedGraphs = null;
        if (datasets == null || datasets.Length == 0)
            return;

        var defaultList = new List<string>();
        var namedList = new List<string>();
        foreach (var ds in datasets)
        {
            var rawIri = source.Slice(ds.GraphIri.Start, ds.GraphIri.Length);
            var iri = PrefixExpander.TryExpand(rawIri, prefixes, source) ?? rawIri.ToString();
            var list = ds.IsNamed ? namedList : defaultList;
            if (!list.Contains(iri))
                list.Add(iri);
        }

        // A dataset clause describes the whole dataset: with FROM NAMED and no FROM, the default graph is empty
        defaultGraphs = defaultList.ToArray();
        namedGraphs = namedList.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stringBuffer != null)
            _bufferManager.Return(_stringBuffer);
    }

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

        var rows = ExecuteGraphViaTree(out var groupedValues);
        // §18.2.5 applies Project before Distinct, and a SELECT * projects the in-scope variables: a pattern's blank node,
        // bound only to join its triples, must not tell two solutions apart. Without DISTINCT the projection is the
        // result's to make (the rows keep their other bindings until then).
        if (_buffer.SelectDistinct && _buffer.SelectAll)
            rows = TreeJoinExecutor.Project(rows, InScopeVariableNames());
        // No solutions is no rows, except for an aggregate without GROUP BY: it groups the empty sequence into one
        // group (COUNT 0), so the rows go to the modifier layer, which synthesizes that group (§18.5).
        if (rows.Count == 0 && !(_buffer.GetSelectClause().HasRealAggregates && !_buffer.GetGroupByClause().HasGroupBy))
            return QueryResults.Empty();

        var bindings = new Binding[64];
        return QueryResults.FromMaterializedSimple(rows, _source.AsSpan(), _store, bindings, _stringBuffer,
            _buffer.LimitOffset, _buffer.SelectDistinct,
            _buffer.GetOrderByClause(), _buffer.GetGroupByClause(),
            _buffer.GetSelectClause(), _buffer.GetHavingClause(), _buffer, trailingValues: groupedValues);
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

        int whereStart = _buffer.WhereGroupStart;
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
                _buffer.LimitOffset, _buffer.SelectDistinct, select);
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
    /// <summary>
    /// True when an ORDER BY condition reads a variable a SELECT expression assigns: ORDER BY sorts after the SELECT
    /// expressions (§18.2.5), so such a condition cannot be applied to the WHERE's rows.
    /// </summary>
    private bool OrderByReadsASelectExpression(OrderByClause orderBy, SelectClause select)
    {
        for (int a = 0; a < select.AggregateCount; a++)
        {
            var assigned = select.GetAggregate(a);
            if (assigned.AliasLength == 0)
                continue;
            var alias = _source.AsSpan(assigned.AliasStart, assigned.AliasLength);
            for (int c = 0; c < orderBy.Count; c++)
            {
                var condition = orderBy.GetCondition(c);
                if (new SparqlParser(_source.AsSpan()).ExpressionReads(condition.ExpressionStart, condition.ExpressionLength, alias))
                    return true;
            }
        }
        return false;
    }

    private bool TryStreamOrderByTopK(out QueryResults result)
    {
        result = default;

        var orderBy = _buffer.GetOrderByClause();
        if (!orderBy.HasOrderBy) return false;        // not an ORDER BY query
        if (_buffer.HasPostQueryValues) return false; // a trailing VALUES joins (multiplies) before the page — top-K would miss it
        if (_buffer.LimitOffset.RowsRead is not long capacity64) return false; // no bounding LIMIT — top-K cannot bound it (result floor)
        if (_buffer.SelectDistinct) return false;     // DISTINCT must dedup before the top-N — fall back
        var select = _buffer.GetSelectClause();
        if (select.HasRealAggregates) return false;   // aggregates go through the fold / grouped path
        if (_buffer.GetGroupByClause().HasGroupBy) return false;
        if (_buffer.GetHavingClause().HasHaving) return false;
        if (OrderByReadsASelectExpression(orderBy, select)) return false; // the scan's rows have no SELECT expressions yet

        // The heap retains `capacity` (= OFFSET + LIMIT) rows. Bound that by a fraction of available physical memory so
        // a pathological OFFSET+LIMIT cannot itself OOM the heap; above the cap, fall back to full materialization (the
        // streaming-presentation territory, not this fold). This is the "adapted to system memory availability" budget.
        long budget = SkyOmega.Mercury.Runtime.ProcessMemoryProbe.AvailablePhysicalBytes() / 8;
        const long AssumedRowBytes = 512;
        long capCap = System.Math.Max(100_000, budget / AssumedRowBytes);
        if (capacity64 <= 0 || capacity64 > capCap) return false;
        int capacity = (int)capacity64;

        int whereStart = _buffer.WhereGroupStart;
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(whereStart, ref tree);
            if (!TreeJoinExecutor.IsFlatBgp(ref tree, root))
                return false; // nested group / property path — full materialization handles those

            var comparer = new MaterializedRowComparer(orderBy, _source, _prefixMappings);
            var rows = new TreeJoinExecutor(_store, _source, _prefixMappings, _source, _serviceExecutor,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _namedGraphs, _defaultGraphs, ReorderBgpInTree)
                .StreamFlatBgpTopK(ref tree, root, "", comparer, capacity);

            var bindings = new Binding[64];
            result = QueryResults.FromMaterializedSimple(rows, _source.AsSpan(), _store, bindings, _stringBuffer,
                _buffer.LimitOffset, distinct: false,
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
    /// materialized rows for the shared modifier layer. A trailing VALUES is joined with the rows, unless the query
    /// groups: then <paramref name="groupedValues"/> is its data rows, which the modifier layer joins with the groups.
    /// </summary>
    private List<MaterializedRow> ExecuteGraphViaTree(out List<MaterializedRow>? groupedValues, int askExistenceCap = 0)
    {
        int whereStart = _buffer.WhereGroupStart;
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
            else if (_buffer.LimitOffset.RowsRead is long cap
                && !_buffer.GetOrderByClause().HasOrderBy
                && !_buffer.GetGroupByClause().HasGroupBy
                && !_buffer.GetHavingClause().HasHaving
                && _buffer.GetSelectClause().AggregateCount == 0
                && !_buffer.SelectDistinct
                && !_buffer.HasPostQueryValues   // a trailing VALUES joins AFTER the BGP — it can multiply rows, so the cap is invalid
                && TreeJoinExecutor.IsPureBgp(ref tree, root))
            {
                if (cap < int.MaxValue) maxRows = (int)cap;
            }

            // ADR-024: text:match trigram pre-filter — resolve candidate object atom IDs per text:match variable so the
            // tree's scan filters at the index level (candidate-narrowing that is mandatory at scale).
            var trigramMap = TreeJoinExecutor.ResolveTrigramCandidates(_store, ref tree, root, _source);

            var executor = new TreeJoinExecutor(_store, _source, _prefixMappings, _source, _serviceExecutor,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _namedGraphs, _defaultGraphs, ReorderBgpInTree, trigramMap);
            var rows = executor.Evaluate(ref tree, root, "", maxRows);

            // A trailing (post-query) VALUES is JOINED with the whole-pattern solution (multiply + bind), not filtered; in
            // a query that groups, with the groups after HAVING (§18.2.4.3), so the modifier layer joins it
            groupedValues = null;
            if (_buffer.HasPostQueryValues)
            {
                if (askExistenceCap == 0 && UsesGrouping())
                    groupedValues = executor.DataBlockRows(_buffer.PostQueryValues);
                else
                    rows = executor.JoinPostQueryValues(rows, _buffer.PostQueryValues);
            }

            return rows;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>Whether the query groups its solutions (<see cref="QueryResults.UsesGrouping"/>).</summary>
    /// <remarks>NoInlining keeps the clause structs out of <see cref="ExecuteGraphViaTree"/>'s frame, which is under
    /// every join's recursion.</remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool UsesGrouping() => QueryResults.UsesGrouping(_buffer.GetGroupByClause(), _buffer.GetSelectClause());

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

        // Every WHERE, with or without top-level triple patterns, is a group the tree evaluates: {} is one empty
        // solution that BIND extends and FILTER removes (§18.2). There is no separate evaluator for a WHERE without
        // triples (ADR-055 D2). Only DESCRIBE may have no WHERE group at all; it then has no solutions here (ADR-055 D3
        // gives DESCRIBE <iri> its own evaluation).
        if (_buffer.WhereGroupStart < 0)
            return QueryResults.Empty();

        // ADR-047 B2: a FROM dataset (default-graph clauses) runs through the unified tree like everything else — the
        // FROM graphs are passed to the TreeJoinExecutor as _defaultGraphs, and a default-context pattern reads their
        // RDF merge, which IS the default graph (SPARQL §13.2), as one graph (MergedGraphEnumerator). The old separate
        // FROM / cross-graph executor path is deleted.

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
                _buffer.LimitOffset, _buffer.SelectDistinct);
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
        // ADR-047 A2/B2: a WHERE's existence goes through the unified tree, GRAPH, sub-SELECT and SERVICE included — the same evaluator SELECT and
        // CONSTRUCT use. A pure BGP short-circuits at the first matched row (askExistenceCap: 1, safe because nothing
        // filters after the join); a group with FILTER / EXISTS / an operator is evaluated whole (the tree's existence
        // primitive, the one ExistsBodyHasMatch uses) and tested non-empty. An all-OPTIONAL group keeps the empty-row
        // seed, so it correctly ASKs true — the old path's requiredCount==0 → false silently dropped that solution. A
        // FROM dataset is honoured here too (the tree reads the merge of _defaultGraphs), so ASK FROM tests the FROM graphs — the
        // old slot path this never reached ignored FROM and tested the unnamed default (a latent bug the cutover fixes).
        // ASK applies no solution modifiers, so its trailing VALUES joins the WHERE's solutions even with GROUP BY
        return ExecuteGraphViaTree(out _, askExistenceCap: 1).Count > 0;
    }

    /// <summary>
    /// Execute a CONSTRUCT query and return constructed triples.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public ConstructResults ExecuteConstruct()
    {
        var template = _buffer.GetConstructTemplate();

        if (!template.HasPatterns)
            return ConstructResults.Empty();

        // The template is instantiated over the WHERE's solutions after the solution modifiers, as a SELECT's rows
        // are: Execute() is the one path for both, so ORDER BY, LIMIT and OFFSET apply (SPARQL 1.1 §16.2).
        return new ConstructResults(Execute(), template, _source, _buffer.Prefixes);
    }

    /// <summary>
    /// Execute a DESCRIBE query ([11] DescribeQuery, SPARQL 1.1 §16.4). It describes its IRI targets, and the IRIs and
    /// blank nodes its variable targets take in the WHERE's solutions, after the solution modifiers; <c>DESCRIBE *</c>
    /// takes every variable's. Without a WHERE there is one empty solution, so only the IRI targets are described.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public DescribeResults ExecuteDescribe()
    {
        var describe = _buffer.Describe;
        var resources = new List<string>();
        var seen = new HashSet<string>();
        var variableHashes = new List<int>();

        if (describe.All)
        {
            foreach (var name in InScopeVariableNames())
                variableHashes.Add(VariableName.Hash(name));
        }
        else
        {
            var targets = new SparqlParser.DescribeTargetReader(_source.AsSpan(), describe.TargetsStart);
            while (targets.MoveNext(out var target))
            {
                var text = _source.AsSpan(target.Start, target.Length);
                if (target.Type == TermType.Variable)
                {
                    variableHashes.Add(VariableName.Hash(text));
                    continue;
                }
                var iri = ExpandPrefixedName(text).ToString();
                if (seen.Add(iri)) resources.Add(iri);
            }
        }

        if (_buffer.WhereGroupStart >= 0 && variableHashes.Count > 0)
        {
            var results = Execute();
            try
            {
                while (results.MoveNext())
                {
                    var solution = results.Current;
                    for (int i = 0; i < solution.Count; i++)
                    {
                        if (!variableHashes.Contains(solution.GetVariableHash(i)))
                            continue;
                        var value = solution.GetString(i);
                        // A literal is not described
                        if (value.Length > 1 && (value[0] == '<' || (value[0] == '_' && value[1] == ':')))
                        {
                            var resource = value.ToString();
                            if (seen.Add(resource)) resources.Add(resource);
                        }
                    }
                }
            }
            finally
            {
                results.Dispose();
            }
        }

        return new DescribeResults(_store, resources, DescribedGraphs(),
            new TemporalScope(_temporalMode, _asOfTime, _rangeStart, _rangeEnd));
    }

    /// <summary>
    /// The graphs a DESCRIBE reads, in IRI form (empty: the default graph). With FROM or FROM NAMED, the graphs they
    /// name; otherwise the default graph and every named graph, so a store that keeps its triples in named graphs
    /// can be described. (SPARQL 1.1 §16.4 leaves the description to the service.)
    /// </summary>
    private List<string> DescribedGraphs()
    {
        var graphs = new List<string>();
        if (_defaultGraphs != null || _namedGraphs != null)
        {
            foreach (var g in (_defaultGraphs ?? []).Concat(_namedGraphs ?? []))
            {
                var iri = Rdf.RdfIri.GraphTerm(g);
                if (!graphs.Contains(iri)) graphs.Add(iri);
            }
            return graphs;
        }
        graphs.Add("");
        var named = _store.GetNamedGraphs();
        while (named.MoveNext())
        {
            var g = named.Current;
            graphs.Add(Rdf.RdfIri.GraphTerm(g.ToString()));
        }
        return graphs;
    }

    /// <summary>
    /// The variables of a <c>SELECT *</c> or <c>DESCRIBE *</c>: the WHERE's in-scope variables (SPARQL 1.1 §18.2.1),
    /// then the trailing VALUES', each once, without the sigil, including variables no solution binds (ADR-055 D7).
    /// </summary>
    internal List<string> InScopeVariableNames()
    {
        var names = new List<string>();
        var inScope = new InScopeVariables(_source.AsSpan());
        try
        {
            var parser = new SparqlParser(_source.AsSpan());
            if (_buffer.WhereGroupStart >= 0)
                parser.AddInScopeVariables(_buffer.WhereGroupStart, ref inScope);
            if (_buffer.HasPostQueryValues)
                parser.AddDataBlockVariables(_buffer.PostQueryValues.Start, ref inScope);
            for (int i = 0; i < inScope.Count; i++)
                names.Add(VariableName.WithoutSigil(inScope[i]).ToString());
        }
        finally
        {
            inScope.Dispose();
        }
        return names;
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
    /// Expands a prefixed name to its full IRI using the prologue prefix mappings.
    /// Also handles 'a' shorthand for rdf:type.
    /// Returns the original span if not a prefixed name or no matching prefix found.
    /// </summary>
    private ReadOnlySpan<char> ExpandPrefixedName(ReadOnlySpan<char> term)
    {
        var expanded = PrefixExpander.TryExpand(term, _prefixMappings, _source.AsSpan());
        if (expanded is null) return term;
        _expandedTerm = expanded;
        return _expandedTerm.AsSpan();
    }
}
