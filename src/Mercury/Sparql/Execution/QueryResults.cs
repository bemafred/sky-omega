using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref partial struct QueryResults
{
    private TriplePatternScan _singleScan;
    private MultiPatternScan _multiScan;
    // Note: VariableGraphScan removed - GRAPH ?g uses materialization to avoid stack overflow
    private SubQueryScan _subQueryScan;
    private DefaultGraphUnionScan _defaultGraphUnionScan;
    private CrossGraphMultiPatternScan _crossGraphScan;
    // Pattern data stored on heap via QueryBuffer (eliminates ~4KB GraphPattern from stack)
    private Patterns.QueryBuffer? _buffer;
    private ReadOnlySpan<char> _source;
    private QuadStore? _store;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private BindingTable _bindingTable;
    private readonly bool _hasFilters;
    private readonly bool _hasOptional;
    private readonly bool _isMultiPattern;
    private readonly bool _isSubQuery;
    private readonly bool _isDefaultGraphUnion;
    private readonly bool _isCrossGraphMultiPattern;
    private bool _isEmpty;
    private FilterEvaluator _filterEvaluator;

    // LIMIT/OFFSET support
    private readonly int _limit;
    private readonly int _offset;
    private int _skipped;
    private int _returned;

    // DISTINCT support
    private readonly bool _distinct;
    private HashSet<int>? _seenHashes;

    // UNION support
    private readonly bool _hasUnion;
    private bool _unionBranchActive;
    private TriplePatternScan _unionSingleScan;
    private MultiPatternScan _unionMultiScan;
    private bool _unionIsMultiPattern;
    private readonly int _firstBranchBindCount;  // BINDs before UNION branch
    private readonly bool _hasUnionBindsOnly;    // True if UNION branch has only BINDs (no patterns)

    // ORDER BY support
    private readonly OrderByClause _orderBy;
    private readonly bool _hasOrderBy;
    private List<MaterializedRow>? _sortedResults;
    private int _sortedIndex;

    // Pre-materialized results support (for grouping pre-materialized data)
    private readonly bool _isMaterialized;
    private int _materializedIndex;

    // BIND support
    private readonly bool _hasBinds;

    // MINUS support
    private readonly bool _hasMinus;

    // VALUES support (inline VALUES in pattern slots)
    private readonly bool _hasValues;

    // Post-query VALUES support (VALUES clause after WHERE clause)
    private readonly bool _hasPostQueryValues;
    // For join semantics: track iteration through VALUES rows for current base solution
    private bool _pendingValuesJoin;
    private int _valuesJoinRowIndex;

    // EXISTS/NOT EXISTS support
    private readonly bool _hasExists;

    // Debug properties
    internal bool HasExists => _hasExists;
    internal bool HasOrderBy => _hasOrderBy;

    // Expanded term storage for prefix resolution in EXISTS/MINUS evaluation
    private string? _expandedSubject;
    private string? _expandedPredicate;
    private string? _expandedObject;

    // Graph context for EXISTS/MINUS evaluation inside GRAPH clauses
    private string? _graphContext;

    // GROUP BY support
    private readonly bool _hasGroupBy;
    private readonly GroupByClause _groupBy;
    private readonly SelectClause _selectClause;
    private List<GroupedRow>? _groupedResults;
    private int _groupedIndex;

    // HAVING support
    private readonly bool _hasHaving;
    private readonly HavingClause _having;

    public static QueryResults Empty()
    {
        var result = new QueryResults();
        result._isEmpty = true;
        return result;
    }

    /// <summary>
    /// Create QueryResults from pre-materialized rows.
    /// Used for subquery joins where results are collected eagerly to avoid stack overflow.
    /// </summary>
    internal static QueryResults FromMaterialized(List<MaterializedRow> rows, Patterns.QueryBuffer buffer,
        ReadOnlySpan<char> source, QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        // If actual ORDER BY is specified, sort the materialized results
        if (orderBy.HasOrderBy && rows.Count > 1)
        {
            var sourceStr = source.ToString();
            rows.Sort(new MaterializedRowComparer(orderBy, sourceStr));
        }

        return new QueryResults(rows, buffer, source, store, bindings, stringBuffer,
            limit, offset, distinct, orderBy, groupBy, selectClause, having);
    }

    /// <summary>
    /// Create QueryResults from pre-materialized rows with graph context for EXISTS/MINUS evaluation.
    /// Used for GRAPH clause results where EXISTS filters need to query the named graph.
    /// </summary>
    internal static QueryResults FromMaterializedWithGraphContext(List<MaterializedRow> rows, Patterns.QueryBuffer buffer,
        ReadOnlySpan<char> source, QuadStore store, Binding[] bindings, char[] stringBuffer,
        string graphContext, int limit = 0, int offset = 0, bool distinct = false)
    {
        var result = new QueryResults(rows, buffer, source, store, bindings, stringBuffer,
            limit, offset, distinct, default, default, default, default);
        result._graphContext = graphContext;
        return result;
    }

    /// <summary>
    /// Create QueryResults from pre-materialized rows without requiring the full GraphPattern.
    /// This overload avoids stack overflow by not passing the large GraphPattern struct.
    /// </summary>
    internal static QueryResults FromMaterializedSimple(List<MaterializedRow> rows,
        ReadOnlySpan<char> source, QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        // If actual ORDER BY is specified, sort the materialized results
        if (orderBy.HasOrderBy && rows.Count > 1)
        {
            var sourceStr = source.ToString();
            rows.Sort(new MaterializedRowComparer(orderBy, sourceStr));
        }

        return new QueryResults(rows, source, store, bindings, stringBuffer,
            limit, offset, distinct, orderBy, groupBy, selectClause, having);
    }

    /// <summary>
    /// Create QueryResults from pre-materialized rows without any large clause structs.
    /// This is the minimal version that avoids stack overflow.
    /// NoInlining prevents the compiler from merging this into a larger stack frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static QueryResults FromMaterializedRows(List<MaterializedRow> rows,
        ReadOnlySpan<char> source, QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false)
    {
        return new QueryResults(rows, source, store, bindings, stringBuffer, limit, offset, distinct);
    }

    /// <summary>
    /// Create QueryResults from a pre-materialized list without source/store references.
    /// This is the most minimal version - just iterates the list directly.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static QueryResults FromMaterializedList(List<MaterializedRow> rows,
        Binding[] bindings, char[] stringBuffer, int limit = 0, int offset = 0, bool distinct = false)
    {
        return new QueryResults(rows, bindings, stringBuffer, limit, offset, distinct);
    }

    /// <summary>
    /// Private constructor for pre-materialized results.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, Patterns.QueryBuffer buffer, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having)
    {
        _buffer = buffer;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = false; // Filters already applied during materialization
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isSubQuery = false;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = false;
        _isEmpty = rows.Count == 0;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = orderBy;
        _hasOrderBy = true; // Force use of MoveNextOrdered() to iterate pre-collected results
        _sortedResults = rows;
        _sortedIndex = -1;
        _hasBinds = false;
        _hasMinus = false;
        _hasValues = false;
        _hasPostQueryValues = false;
        _hasExists = buffer?.HasExists ?? false;
        _groupBy = groupBy;
        _selectClause = selectClause;
        // Enable grouping for explicit GROUP BY OR implicit aggregation (aggregates without GROUP BY)
        _hasGroupBy = groupBy.HasGroupBy || selectClause.HasRealAggregates;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    /// <summary>
    /// Private constructor for pre-materialized results without pattern.
    /// Used for simple materialized iteration where pattern access is not needed.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having)
    {
        _buffer = null; // No pattern needed for materialized results
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = false; // Filters already applied during materialization
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isSubQuery = false;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = false;
        _isEmpty = rows.Count == 0;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = orderBy;
        _hasOrderBy = true; // Force use of MoveNextOrdered() to iterate pre-collected results
        _sortedResults = rows;
        _sortedIndex = -1;
        _isMaterialized = true;  // Enable iteration from pre-materialized list
        _materializedIndex = -1;
        _hasBinds = false;
        _hasMinus = false;
        _hasValues = false;
        _hasPostQueryValues = false;
        _hasExists = false;
        _groupBy = groupBy;
        _selectClause = selectClause;
        // Enable grouping for explicit GROUP BY OR implicit aggregation (aggregates without GROUP BY)
        _hasGroupBy = groupBy.HasGroupBy || selectClause.HasRealAggregates;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    /// <summary>
    /// Minimal private constructor for pre-materialized results without any large clause structs.
    /// Avoids stack overflow by not passing ORDER BY, GROUP BY, SELECT, HAVING clauses.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct)
    {
        _buffer = null;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = false;
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isSubQuery = false;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = false;
        _isEmpty = rows.Count == 0;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = default;
        _hasOrderBy = true; // Use MoveNextOrdered() to iterate pre-collected results
        _sortedResults = rows;
        _sortedIndex = -1;
        _hasBinds = false;
        _hasMinus = false;
        _hasValues = false;
        _hasPostQueryValues = false;
        _hasExists = false;
        _groupBy = default;
        _selectClause = default;
        _hasGroupBy = false;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = default;
        _hasHaving = false;
    }

    /// <summary>
    /// Most minimal constructor - just for iterating a pre-materialized list.
    /// No source/store references needed.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct)
    {
        _buffer = null;
        _source = default;
        _store = null;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = false;
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isSubQuery = false;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = false;
        _isEmpty = rows.Count == 0;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = default;
        _hasOrderBy = true;
        _sortedResults = rows;
        _sortedIndex = -1;
        _hasBinds = false;
        _hasMinus = false;
        _hasValues = false;
        _hasPostQueryValues = false;
        _hasExists = false;
        _groupBy = default;
        _selectClause = default;
        _hasGroupBy = false;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = default;
        _hasHaving = false;
    }

    internal QueryResults(TriplePatternScan scan, Patterns.QueryBuffer buffer, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _singleScan = scan;
        _buffer = buffer;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = buffer.HasFilters;
        _hasOptional = buffer.HasOptionalPatterns;
        _hasUnion = buffer.HasUnion;
        _firstBranchBindCount = buffer.FirstBranchBindCount;
        _hasUnionBindsOnly = buffer.HasUnion && buffer.UnionBranchTripleCount == 0 && buffer.UnionBranchBindCount > 0;
        _isMultiPattern = false;
        _isSubQuery = false;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = false;
        _isEmpty = false;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = orderBy;
        _hasOrderBy = orderBy.HasOrderBy;
        _sortedResults = null;
        _sortedIndex = -1;
        _hasBinds = buffer.HasBinds;
        _hasMinus = buffer.HasMinus;
        _hasValues = buffer.HasValues;
        _hasPostQueryValues = buffer.HasPostQueryValues;
        _hasExists = buffer.HasExists;
        _groupBy = groupBy;
        _selectClause = selectClause;
        // Enable grouping for explicit GROUP BY OR implicit aggregation (aggregates without GROUP BY)
        _hasGroupBy = groupBy.HasGroupBy || selectClause.HasRealAggregates;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    internal QueryResults(MultiPatternScan scan, Patterns.QueryBuffer buffer, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _multiScan = scan;
        _buffer = buffer;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = buffer.HasFilters;
        _hasOptional = buffer.HasOptionalPatterns;
        _hasUnion = buffer.HasUnion;
        _firstBranchBindCount = buffer.FirstBranchBindCount;
        _hasUnionBindsOnly = buffer.HasUnion && buffer.UnionBranchTripleCount == 0 && buffer.UnionBranchBindCount > 0;
        _isMultiPattern = true;
        _isSubQuery = false;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = false;
        _isEmpty = false;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = orderBy;
        _hasOrderBy = orderBy.HasOrderBy;
        _sortedResults = null;
        _sortedIndex = -1;
        _hasBinds = buffer.HasBinds;
        _hasMinus = buffer.HasMinus;
        _hasValues = buffer.HasValues;
        _hasPostQueryValues = buffer.HasPostQueryValues;
        _hasExists = buffer.HasExists;
        _groupBy = groupBy;
        _selectClause = selectClause;
        // Enable grouping for explicit GROUP BY OR implicit aggregation (aggregates without GROUP BY)
        _hasGroupBy = groupBy.HasGroupBy || selectClause.HasRealAggregates;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    // Note: VariableGraphScan constructor removed - GRAPH ?g now uses FromMaterialized

    internal QueryResults(SubQueryScan scan, Patterns.QueryBuffer buffer, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _subQueryScan = scan;
        _buffer = buffer;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = buffer.HasFilters;
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isSubQuery = true;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = false;
        _isEmpty = false;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = orderBy;
        _hasOrderBy = orderBy.HasOrderBy;
        _sortedResults = null;
        _sortedIndex = -1;
        _hasBinds = false;
        _hasMinus = false;
        _hasValues = false;
        _hasPostQueryValues = false;
        _hasExists = false;
        _groupBy = groupBy;
        _selectClause = selectClause;
        // Enable grouping for explicit GROUP BY OR implicit aggregation (aggregates without GROUP BY)
        _hasGroupBy = groupBy.HasGroupBy || selectClause.HasRealAggregates;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
        _isDefaultGraphUnion = false;
    }

    internal QueryResults(DefaultGraphUnionScan scan, Patterns.QueryBuffer buffer, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _defaultGraphUnionScan = scan;
        _buffer = buffer;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = buffer.HasFilters;
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isSubQuery = false;
        _isDefaultGraphUnion = true;
        _isCrossGraphMultiPattern = false;
        _isEmpty = false;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = orderBy;
        _hasOrderBy = orderBy.HasOrderBy;
        _sortedResults = null;
        _sortedIndex = -1;
        _hasBinds = false;
        _hasMinus = false;
        _hasValues = false;
        _hasPostQueryValues = false;
        _hasExists = false;
        _groupBy = groupBy;
        _selectClause = selectClause;
        // Enable grouping for explicit GROUP BY OR implicit aggregation (aggregates without GROUP BY)
        _hasGroupBy = groupBy.HasGroupBy || selectClause.HasRealAggregates;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    internal QueryResults(CrossGraphMultiPatternScan scan, Patterns.QueryBuffer buffer, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _crossGraphScan = scan;
        _buffer = buffer;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = buffer.HasFilters;
        _hasOptional = buffer.HasOptionalPatterns;
        _hasUnion = false;
        _isMultiPattern = false;
        _isSubQuery = false;
        _isDefaultGraphUnion = false;
        _isCrossGraphMultiPattern = true;
        _isEmpty = false;
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _unionBranchActive = false;
        _orderBy = orderBy;
        _hasOrderBy = orderBy.HasOrderBy;
        _sortedResults = null;
        _sortedIndex = -1;
        _hasBinds = buffer.HasBinds;
        _hasMinus = buffer.HasMinus;
        _hasValues = buffer.HasValues;
        _hasPostQueryValues = buffer.HasPostQueryValues;
        _hasExists = buffer.HasExists;
        _groupBy = groupBy;
        _selectClause = selectClause;
        // Enable grouping for explicit GROUP BY OR implicit aggregation (aggregates without GROUP BY)
        _hasGroupBy = groupBy.HasGroupBy || selectClause.HasRealAggregates;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    /// <summary>
    /// Current result row with variable bindings.
    /// </summary>
    public readonly BindingTable Current => _bindingTable;

    /// <summary>
    /// Set the graph context for EXISTS/MINUS evaluation inside GRAPH clauses.
    /// When set, EXISTS patterns will query the specified graph instead of the default graph.
    /// </summary>
    internal void SetGraphContext(string? graphIri)
    {
        _graphContext = graphIri;
    }

    /// <summary>
    /// Move to next result row.
    /// </summary>
    public bool MoveNext()
    {
        if (_isEmpty) return false;

        // GROUP BY requires collecting all results first, then grouping
        if (_hasGroupBy)
        {
            return MoveNextGrouped();
        }

        // ORDER BY requires collecting all results first, then sorting
        if (_hasOrderBy)
        {
            return MoveNextOrdered();
        }

        return MoveNextUnordered();
    }

    /// <summary>
    /// Move to next result for ORDER BY queries.
    /// Collects all results on first call, sorts them, then iterates.
    /// </summary>
    private bool MoveNextUnordered()
    {
        // Check if we've hit the limit
        if (_limit > 0 && _returned >= _limit)
            return false;

        while (true)
        {
            // If we have a pending base solution for VALUES join, iterate through VALUES rows
            if (_pendingValuesJoin)
            {
                if (TryNextValuesJoinRow())
                {
                    // Apply DISTINCT - skip duplicate rows
                    if (_distinct)
                    {
                        var hash = ComputeBindingsHash();
                        if (!_seenHashes!.Add(hash))
                            continue; // Duplicate, try next VALUES row
                    }

                    // Apply OFFSET - skip results until we've skipped enough
                    if (_skipped < _offset)
                    {
                        _skipped++;
                        continue;
                    }

                    _returned++;
                    return true;
                }
                // No more matching VALUES rows for this base solution
                _pendingValuesJoin = false;
            }

            bool hasNext;

            if (_isSubQuery)
            {
                hasNext = _subQueryScan.MoveNext(ref _bindingTable);
            }
            else if (_isCrossGraphMultiPattern)
            {
                hasNext = _crossGraphScan.MoveNext(ref _bindingTable);
            }
            else if (_isDefaultGraphUnion)
            {
                hasNext = _defaultGraphUnionScan.MoveNext(ref _bindingTable);
            }
            else if (_unionBranchActive)
            {
                // Using UNION branch scans
                if (_unionIsMultiPattern)
                    hasNext = _unionMultiScan.MoveNext(ref _bindingTable);
                else
                    hasNext = _unionSingleScan.MoveNext(ref _bindingTable);
            }
            else
            {
                // Using first branch scans
                if (_isMultiPattern)
                    hasNext = _multiScan.MoveNext(ref _bindingTable);
                else
                    hasNext = _singleScan.MoveNext(ref _bindingTable);
            }

            if (!hasNext)
            {
                // Try switching to UNION branch
                if (!_isSubQuery && !_isDefaultGraphUnion && !_isCrossGraphMultiPattern && _hasUnion && !_unionBranchActive)
                {
                    _unionBranchActive = true;
                    if (!InitializeUnionBranch())
                        return false;
                    continue; // Try again with union branch
                }
                return false;
            }

            // Try to extend with optional patterns (left outer join semantics)
            if (_hasOptional)
            {
                TryMatchOptionalPatterns();
            }

            // Evaluate BIND expressions before FILTER (BIND may create variables used in FILTER)
            if (_hasBinds)
            {
                EvaluateBindExpressions();
            }

            // Evaluate non-aggregate SELECT expressions (e.g., (HOURS(?date) AS ?x))
            // These create computed values that may be used in FILTER or returned in results
            EvaluateSelectExpressions();

            // Apply filters
            // Note: Don't clear binding table on rejection - the scan's TruncateTo handles resetting.
            // Clearing here breaks the scan's internal binding count tracking.
            if (_hasFilters)
            {
                if (!EvaluateFilters())
                    continue; // Try next row
            }

            // Apply EXISTS/NOT EXISTS filters
            if (_hasExists)
            {
                if (!EvaluateExistsFilters())
                    continue; // EXISTS condition failed
            }

            // Apply MINUS - exclude matching rows
            if (_hasMinus)
            {
                if (MatchesMinusPattern())
                    continue; // Matches MINUS, skip this row
            }

            // Apply VALUES - check if bound value matches any VALUES value (inline VALUES in patterns)
            if (_hasValues)
            {
                if (!MatchesValuesConstraint())
                    continue; // Doesn't match VALUES, skip this row
            }

            // Apply post-query VALUES as a join (not a filter)
            // For each base solution, we return one result per matching VALUES row
            // This implements proper join semantics where a solution matching multiple
            // VALUES rows appears multiple times in the output
            if (_hasPostQueryValues)
            {
                // Start iterating through VALUES rows for this base solution
                _pendingValuesJoin = true;
                _valuesJoinRowIndex = 0;
                continue; // Go back to start of loop to process VALUES rows
            }

            // Apply DISTINCT - skip duplicate rows
            if (_distinct)
            {
                var hash = ComputeBindingsHash();
                if (!_seenHashes!.Add(hash))
                    continue; // Duplicate, try next row
            }

            // Apply OFFSET - skip results until we've skipped enough
            if (_skipped < _offset)
            {
                _skipped++;
                continue;
            }

            _returned++;
            return true;
        }
    }

    /// <summary>
    /// Try to find the next matching VALUES row for the current base solution.
    /// Returns true if a matching row was found, false if no more rows match.
    /// In SPARQL semantics, VALUES is a join, not a filter:
    /// - If a variable is bound and matches the VALUES value → match
    /// - If a variable is unbound and VALUES value is not UNDEF → introduce binding and match
    /// - If a variable is bound but doesn't match → no match
    /// </summary>
    private bool TryNextValuesJoinRow()
    {
        if (_buffer == null || !_buffer.HasPostQueryValues) return false;

        var postValues = _buffer.PostQueryValues;
        if (!postValues.HasValues) return false;

        int varCount = postValues.VariableCount;
        if (varCount == 0) return false;

        int rowCount = postValues.RowCount;

        // Continue from where we left off
        while (_valuesJoinRowIndex < rowCount)
        {
            int row = _valuesJoinRowIndex++;

            bool rowMatches = true;
            // Track which variable indices need bindings introduced (unbound in solution, non-UNDEF in VALUES)
            Span<int> unboundVarIndices = stackalloc int[varCount];
            int unboundCount = 0;

            for (int varIdx = 0; varIdx < varCount; varIdx++)
            {
                var (varStart, varLength) = postValues.GetVariable(varIdx);
                var varName = _source.Slice(varStart, varLength);

                var (valStart, valLength) = postValues.GetValueAt(row, varIdx);

                // UNDEF matches anything (including unbound)
                if (valLength == -1)
                    continue;

                // Find binding for this variable
                var bindingIdx = _bindingTable.FindBinding(varName);
                if (bindingIdx < 0)
                {
                    // Variable not bound - VALUES can introduce a binding (join semantics)
                    // Record the index so we can add bindings later
                    unboundVarIndices[unboundCount++] = varIdx;
                    continue;
                }

                var valuesValue = _source.Slice(valStart, valLength);
                var expandedValue = ExpandPrefixedName(valuesValue);
                var boundValue = _bindingTable.GetString(bindingIdx);

                // Handle string literal comparison
                if (!CompareValuesMatch(boundValue, expandedValue))
                {
                    rowMatches = false;
                    break;
                }
            }

            if (rowMatches)
            {
                // Introduce bindings for unbound variables
                for (int i = 0; i < unboundCount; i++)
                {
                    var varIdx = unboundVarIndices[i];
                    var (varStart, varLength) = postValues.GetVariable(varIdx);
                    var varName = _source.Slice(varStart, varLength);
                    var (valStart, valLength) = postValues.GetValueAt(row, varIdx);
                    var valuesValue = _source.Slice(valStart, valLength);
                    var expandedValue = ExpandPrefixedName(valuesValue);

                    // Use generic Bind - the value is already in the correct format
                    // (URIs have angle brackets, literals have quotes)
                    _bindingTable.Bind(varName, expandedValue);
                }
                return true;
            }
        }

        // No more matching rows
        return false;
    }

    /// <summary>
    /// Try to match optional patterns and extend bindings.
    /// If a pattern doesn't match, we continue without it (left outer join).
    /// </summary>
}

/// <summary>
/// Materialized row for ORDER BY sorting.
/// Stores binding hashes and values as strings (heap-allocated).
/// </summary>
internal sealed class MaterializedRow
{
    private readonly int[] _hashes;
    private readonly string[] _values;
    private readonly int _count;

    public int BindingCount => _count;

    public MaterializedRow(BindingTable bindings)
    {
        _count = bindings.Count;
        _hashes = new int[_count];
        _values = new string[_count];

        var bindingSpan = bindings.GetBindings();
        for (int i = 0; i < _count; i++)
        {
            _hashes[i] = bindingSpan[i].VariableNameHash;
            _values[i] = bindings.GetString(i).ToString();
        }
    }

    public int GetHash(int index) => _hashes[index];
    public ReadOnlySpan<char> GetValue(int index) => _values[index];

    public ReadOnlySpan<char> GetValueByName(ReadOnlySpan<char> name)
    {
        var hash = ComputeHash(name);
        for (int i = 0; i < _count; i++)
        {
            if (_hashes[i] == hash)
                return _values[i];
        }
        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// Restore this row's bindings into a BindingTable.
    /// Used when re-executing operators with stored results.
    /// </summary>
    public void RestoreBindings(ref BindingTable bindings)
    {
        for (int i = 0; i < _count; i++)
        {
            bindings.BindWithHash(_hashes[i], _values[i].AsSpan());
        }
    }

    private static int ComputeHash(ReadOnlySpan<char> s)
    {
        // FNV-1a hash - must match BindingTable.ComputeHash
        uint hash = 2166136261;
        foreach (var ch in s)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }
}

/// <summary>
/// Grouped row for GROUP BY aggregation.
/// Stores group key values and aggregate accumulators.
/// </summary>
internal sealed class GroupedRow
{
    // Group key storage
    private readonly int[] _keyHashes;
    private readonly string[] _keyValues;
    private readonly int _keyCount;

    // Aggregate storage
    private readonly int[] _aggHashes;        // Hash of alias variable name
    private readonly string[] _aggValues;      // Final computed values
    private readonly AggregateFunction[] _aggFunctions;
    private readonly int[] _aggVarHashes;      // Hash of source variable name
    private readonly int _aggCount;

    // Aggregate accumulators
    private readonly long[] _counts;
    private readonly double[] _sums;
    private readonly double[] _mins;
    private readonly double[] _maxes;
    private readonly decimal[] _decimalSums;    // For precise decimal arithmetic
    private readonly decimal[] _decimalMins;
    private readonly decimal[] _decimalMaxes;
    private readonly bool[] _useDecimal;        // True if all values are decimal (not double/float)
    private readonly HashSet<string>?[] _distinctSets;
    private readonly List<string>?[] _concatValues;  // For GROUP_CONCAT
    private readonly string[] _separators;           // For GROUP_CONCAT
    private readonly string?[] _sampleValues;        // For SAMPLE

    public int KeyCount => _keyCount;
    public int AggregateCount => _aggCount;

    public GroupedRow(GroupByClause groupBy, SelectClause selectClause, BindingTable bindings, string source)
    {
        // Store group key values
        _keyCount = groupBy.Count;
        _keyHashes = new int[_keyCount];
        _keyValues = new string[_keyCount];

        for (int i = 0; i < _keyCount; i++)
        {
            var (start, len) = groupBy.GetVariable(i);
            var varName = source.AsSpan(start, len);
            _keyHashes[i] = ComputeHash(varName);
            var idx = bindings.FindBinding(varName);
            _keyValues[i] = idx >= 0 ? bindings.GetString(idx).ToString() : "";
        }

        // Initialize aggregate accumulators
        _aggCount = selectClause.AggregateCount;
        _aggHashes = new int[_aggCount];
        _aggValues = new string[_aggCount];
        _aggFunctions = new AggregateFunction[_aggCount];
        _aggVarHashes = new int[_aggCount];
        _counts = new long[_aggCount];
        _sums = new double[_aggCount];
        _mins = new double[_aggCount];
        _maxes = new double[_aggCount];
        _decimalSums = new decimal[_aggCount];
        _decimalMins = new decimal[_aggCount];
        _decimalMaxes = new decimal[_aggCount];
        _useDecimal = new bool[_aggCount];
        _distinctSets = new HashSet<string>?[_aggCount];
        _concatValues = new List<string>?[_aggCount];
        _separators = new string[_aggCount];
        _sampleValues = new string?[_aggCount];

        for (int i = 0; i < _aggCount; i++)
        {
            var agg = selectClause.GetAggregate(i);
            _aggFunctions[i] = agg.Function;

            // Hash of alias (result variable name)
            var aliasName = source.AsSpan(agg.AliasStart, agg.AliasLength);
            _aggHashes[i] = ComputeHash(aliasName);

            // Hash of source variable
            var varName = source.AsSpan(agg.VariableStart, agg.VariableLength);
            _aggVarHashes[i] = ComputeHash(varName);

            // Initialize accumulators
            _mins[i] = double.MaxValue;
            _maxes[i] = double.MinValue;
            _decimalMins[i] = decimal.MaxValue;
            _decimalMaxes[i] = decimal.MinValue;
            _useDecimal[i] = true; // Assume decimal until we see a double/float
            if (agg.Distinct)
            {
                _distinctSets[i] = new HashSet<string>();
            }

            // Initialize GROUP_CONCAT accumulators
            if (agg.Function == AggregateFunction.GroupConcat)
            {
                _concatValues[i] = new List<string>();
                // Extract separator from source, default to space
                _separators[i] = agg.SeparatorLength > 0
                    ? source.Substring(agg.SeparatorStart, agg.SeparatorLength)
                    : " ";
            }
        }
    }

    public void UpdateAggregates(BindingTable bindings, string source)
    {
        for (int i = 0; i < _aggCount; i++)
        {
            var func = _aggFunctions[i];
            var varHash = _aggVarHashes[i];

            // Find the value for this aggregate's variable
            string? valueStr = null;
            double numValue = 0;
            decimal decimalValue = 0;
            bool hasNumValue = false;
            bool isDouble = false;

            // For COUNT(*), we don't need a specific variable
            if (varHash != ComputeHash("*".AsSpan()))
            {
                var idx = bindings.FindBindingByHash(varHash);
                if (idx >= 0)
                {
                    valueStr = bindings.GetString(idx).ToString();
                    // Use RDF-aware numeric parsing to handle typed literals like "1"^^<xsd:integer>
                    hasNumValue = TryParseRdfNumeric(valueStr, out numValue, out decimalValue, out isDouble);
                }
                else
                {
                    // Variable not bound - skip for most aggregates
                    if (func != AggregateFunction.Count)
                        continue;
                }
            }

            // Handle DISTINCT
            if (_distinctSets[i] != null)
            {
                var val = valueStr ?? "";
                if (!_distinctSets[i]!.Add(val))
                    continue; // Already seen this value
            }

            // If we encounter a double/float value, switch to double mode
            if (hasNumValue && isDouble)
                _useDecimal[i] = false;

            // Update accumulator based on function
            switch (func)
            {
                case AggregateFunction.Count:
                    _counts[i]++;
                    break;
                case AggregateFunction.Sum:
                    if (hasNumValue)
                    {
                        _sums[i] += numValue;
                        _decimalSums[i] += decimalValue;
                    }
                    break;
                case AggregateFunction.Avg:
                    if (hasNumValue)
                    {
                        _sums[i] += numValue;
                        _decimalSums[i] += decimalValue;
                        _counts[i]++;
                    }
                    break;
                case AggregateFunction.Min:
                    if (hasNumValue)
                    {
                        if (numValue < _mins[i])
                            _mins[i] = numValue;
                        if (decimalValue < _decimalMins[i])
                            _decimalMins[i] = decimalValue;
                    }
                    break;
                case AggregateFunction.Max:
                    if (hasNumValue)
                    {
                        if (numValue > _maxes[i])
                            _maxes[i] = numValue;
                        if (decimalValue > _decimalMaxes[i])
                            _decimalMaxes[i] = decimalValue;
                    }
                    break;
                case AggregateFunction.GroupConcat:
                    if (valueStr != null)
                        _concatValues[i]!.Add(valueStr);
                    break;
                case AggregateFunction.Sample:
                    // SAMPLE returns an arbitrary value - we take the first one
                    if (_sampleValues[i] == null && valueStr != null)
                        _sampleValues[i] = valueStr;
                    break;
            }
        }
    }

    public void FinalizeAggregates()
    {
        for (int i = 0; i < _aggCount; i++)
        {
            _aggValues[i] = _aggFunctions[i] switch
            {
                AggregateFunction.Count => FormatTypedLiteral(_counts[i].ToString(), XsdInteger),
                AggregateFunction.Sum => _useDecimal[i]
                    ? FormatTypedLiteral(FormatDecimal(_decimalSums[i]), XsdDecimal)
                    : FormatTypedLiteral(_sums[i].ToString(CultureInfo.InvariantCulture), XsdDouble),
                AggregateFunction.Avg => _counts[i] > 0
                    ? (_useDecimal[i]
                        ? FormatTypedLiteral(FormatDecimal(_decimalSums[i] / _counts[i]), XsdDecimal)
                        : FormatTypedLiteral((_sums[i] / _counts[i]).ToString(CultureInfo.InvariantCulture), XsdDouble))
                    : FormatTypedLiteral("0", XsdInteger),
                AggregateFunction.Min => _useDecimal[i]
                    ? (_decimalMins[i] == decimal.MaxValue ? "" : FormatTypedLiteral(FormatDecimal(_decimalMins[i]), XsdDecimal))
                    : (_mins[i] == double.MaxValue ? "" : FormatTypedLiteral(_mins[i].ToString(CultureInfo.InvariantCulture), XsdDouble)),
                AggregateFunction.Max => _useDecimal[i]
                    ? (_decimalMaxes[i] == decimal.MinValue ? "" : FormatTypedLiteral(FormatDecimal(_decimalMaxes[i]), XsdDecimal))
                    : (_maxes[i] == double.MinValue ? "" : FormatTypedLiteral(_maxes[i].ToString(CultureInfo.InvariantCulture), XsdDouble)),
                AggregateFunction.GroupConcat => _concatValues[i] != null
                    ? string.Join(_separators[i], _concatValues[i]!)
                    : "",
                AggregateFunction.Sample => _sampleValues[i] ?? "",
                _ => ""
            };
        }
    }

    private const string XsdInteger = "http://www.w3.org/2001/XMLSchema#integer";
    private const string XsdDecimal = "http://www.w3.org/2001/XMLSchema#decimal";
    private const string XsdDouble = "http://www.w3.org/2001/XMLSchema#double";

    /// <summary>
    /// Format a value as a typed RDF literal: "value"^^&lt;datatype&gt;
    /// </summary>
    private static string FormatTypedLiteral(string value, string datatype)
    {
        return $"\"{value}\"^^<{datatype}>";
    }

    /// <summary>
    /// Format a decimal value, removing trailing zeros but keeping at least one decimal place
    /// if the original value had decimals.
    /// </summary>
    private static string FormatDecimal(decimal value)
    {
        // Use G29 to get up to 29 significant digits without trailing zeros
        var str = value.ToString("G29", CultureInfo.InvariantCulture);
        return str;
    }

    public int GetKeyHash(int index) => _keyHashes[index];
    public ReadOnlySpan<char> GetKeyValue(int index) => _keyValues[index];
    public int GetAggregateHash(int index) => _aggHashes[index];
    public ReadOnlySpan<char> GetAggregateValue(int index) => _aggValues[index];

    private static int ComputeHash(ReadOnlySpan<char> s)
    {
        uint hash = 2166136261;
        foreach (var ch in s)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }

    /// <summary>
    /// Try to parse a numeric value from an RDF literal string.
    /// Handles formats: "42", "3.14", "1"^^&lt;xsd:integer&gt;, "2.0"^^&lt;xsd:decimal&gt;
    /// Returns both double and decimal representations, and indicates if the value
    /// is a double (scientific notation or xsd:double/xsd:float type).
    /// </summary>
    private static bool TryParseRdfNumeric(string str, out double doubleResult, out decimal decimalResult, out bool isDouble)
    {
        doubleResult = 0;
        decimalResult = 0;
        isDouble = false;

        if (string.IsNullOrEmpty(str))
            return false;

        string valueStr;
        string? datatype = null;

        // Handle typed literals: "value"^^<datatype>
        if (str.StartsWith('"'))
        {
            // Find end of quoted value - it's either ^^ (typed), @ (language), or closing quote
            int endQuote = str.IndexOf('"', 1);
            if (endQuote <= 0)
                return false;

            // Extract the value between quotes
            valueStr = str.Substring(1, endQuote - 1);

            // Check for datatype
            var suffix = str.AsSpan(endQuote + 1);
            if (suffix.StartsWith("^^"))
            {
                var dtStr = suffix.Slice(2).ToString();
                if (dtStr.StartsWith('<') && dtStr.EndsWith('>'))
                    datatype = dtStr.Substring(1, dtStr.Length - 2);
                else
                    datatype = dtStr;
            }
        }
        else
        {
            valueStr = str;
        }

        // Determine if this is a double/float type
        isDouble = datatype != null &&
            (datatype.EndsWith("double", StringComparison.OrdinalIgnoreCase) ||
             datatype.EndsWith("float", StringComparison.OrdinalIgnoreCase));

        // Also check for scientific notation (indicates double)
        if (!isDouble && (valueStr.Contains('e') || valueStr.Contains('E')))
            isDouble = true;

        // Try to parse as double (always)
        if (!double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleResult))
            return false;

        // Try to parse as decimal (may fail for very large/small numbers or scientific notation)
        if (!decimal.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalResult))
        {
            // If decimal parse fails, use the double value converted to decimal
            // This will lose precision but at least gives us a value
            try
            {
                decimalResult = (decimal)doubleResult;
            }
            catch
            {
                decimalResult = 0;
                isDouble = true; // Force double mode since decimal can't represent this
            }
        }

        return true;
    }
}

/// <summary>
/// Results from CONSTRUCT query execution. Yields constructed triples.
/// Must be disposed to return pooled resources.
/// </summary>
