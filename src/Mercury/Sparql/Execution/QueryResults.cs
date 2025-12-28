using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql;

public ref struct QueryResults
{
    private TriplePatternScan _singleScan;
    private MultiPatternScan _multiScan;
    private VariableGraphScan _variableGraphScan;
    private SubQueryScan _subQueryScan;
    private GraphPattern _pattern;
    private ReadOnlySpan<char> _source;
    private TripleStore? _store;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private BindingTable _bindingTable;
    private readonly bool _hasFilters;
    private readonly bool _hasOptional;
    private readonly bool _isMultiPattern;
    private readonly bool _isVariableGraph;
    private readonly bool _isSubQuery;
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

    // ORDER BY support
    private readonly OrderByClause _orderBy;
    private readonly bool _hasOrderBy;
    private List<MaterializedRow>? _sortedResults;
    private int _sortedIndex;

    // BIND support
    private readonly bool _hasBinds;

    // MINUS support
    private readonly bool _hasMinus;

    // VALUES support
    private readonly bool _hasValues;

    // EXISTS/NOT EXISTS support
    private readonly bool _hasExists;

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
    internal static QueryResults FromMaterialized(List<MaterializedRow> rows, GraphPattern pattern,
        ReadOnlySpan<char> source, TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        // If actual ORDER BY is specified, sort the materialized results
        if (orderBy.HasOrderBy && rows.Count > 1)
        {
            var sourceStr = source.ToString();
            rows.Sort((a, b) => CompareRowsStatic(a, b, orderBy, sourceStr));
        }

        return new QueryResults(rows, pattern, source, store, bindings, stringBuffer,
            limit, offset, distinct, orderBy, groupBy, selectClause, having);
    }

    /// <summary>
    /// Private constructor for pre-materialized results.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having)
    {
        _pattern = pattern;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = false; // Filters already applied during materialization
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isVariableGraph = false;
        _isSubQuery = false;
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
        _hasExists = false;
        _groupBy = groupBy;
        _selectClause = selectClause;
        _hasGroupBy = groupBy.HasGroupBy;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    internal QueryResults(TriplePatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _singleScan = scan;
        _pattern = pattern;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _hasOptional = pattern.HasOptionalPatterns;
        _hasUnion = pattern.HasUnion;
        _isMultiPattern = false;
        _isVariableGraph = false;
        _isSubQuery = false;
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
        _hasBinds = pattern.HasBinds;
        _hasMinus = pattern.HasMinus;
        _hasValues = pattern.HasValues;
        _hasExists = pattern.HasExists;
        _groupBy = groupBy;
        _selectClause = selectClause;
        _hasGroupBy = groupBy.HasGroupBy;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    internal QueryResults(MultiPatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _multiScan = scan;
        _pattern = pattern;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _hasOptional = pattern.HasOptionalPatterns;
        _hasUnion = pattern.HasUnion;
        _isMultiPattern = true;
        _isVariableGraph = false;
        _isSubQuery = false;
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
        _hasBinds = pattern.HasBinds;
        _hasMinus = pattern.HasMinus;
        _hasValues = pattern.HasValues;
        _hasExists = pattern.HasExists;
        _groupBy = groupBy;
        _selectClause = selectClause;
        _hasGroupBy = groupBy.HasGroupBy;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    internal QueryResults(VariableGraphScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _variableGraphScan = scan;
        _pattern = pattern;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _hasOptional = false; // Variable graph handles its own patterns
        _hasUnion = false;
        _isMultiPattern = false;
        _isVariableGraph = true;
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
        _hasExists = false;
        _groupBy = groupBy;
        _selectClause = selectClause;
        _hasGroupBy = groupBy.HasGroupBy;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
        _isSubQuery = false;
    }

    internal QueryResults(SubQueryScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default)
    {
        _subQueryScan = scan;
        _pattern = pattern;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _hasFilters = pattern.FilterCount > 0;
        _hasOptional = false;
        _hasUnion = false;
        _isMultiPattern = false;
        _isVariableGraph = false;
        _isSubQuery = true;
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
        _hasExists = false;
        _groupBy = groupBy;
        _selectClause = selectClause;
        _hasGroupBy = groupBy.HasGroupBy;
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
    private bool MoveNextOrdered()
    {
        // First call - collect and sort all results
        if (_sortedResults == null)
        {
            CollectAndSortResults();
        }

        // Check if we've hit the limit
        if (_limit > 0 && _returned >= _limit)
            return false;

        // Iterate through sorted results
        while (++_sortedIndex < _sortedResults!.Count)
        {
            // Apply OFFSET
            if (_skipped < _offset)
            {
                _skipped++;
                continue;
            }

            // Load the materialized row into binding table
            var row = _sortedResults[_sortedIndex];
            _bindingTable.Clear();
            for (int i = 0; i < row.BindingCount; i++)
            {
                _bindingTable.BindWithHash(row.GetHash(i), row.GetValue(i));
            }

            _returned++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collect all results and sort them according to ORDER BY.
    /// </summary>
    private void CollectAndSortResults()
    {
        _sortedResults = new List<MaterializedRow>();

        // Collect all results using the streaming approach
        while (MoveNextUnorderedForCollection())
        {
            // Materialize the current row
            var row = new MaterializedRow(_bindingTable);
            _sortedResults.Add(row);
            _bindingTable.Clear();
        }

        // Sort the results
        if (_sortedResults.Count > 1)
        {
            var orderBy = _orderBy;
            var sourceStr = _source.ToString();
            _sortedResults.Sort((a, b) => CompareRowsStatic(a, b, orderBy, sourceStr));
        }

        // Reset for iteration
        _sortedIndex = -1;
        _skipped = 0;
        _returned = 0;
    }

    /// <summary>
    /// Compare two rows according to ORDER BY conditions.
    /// </summary>
    private static int CompareRowsStatic(MaterializedRow a, MaterializedRow b, OrderByClause orderBy, string source)
    {
        for (int i = 0; i < orderBy.Count; i++)
        {
            var cond = orderBy.GetCondition(i);
            var varName = source.AsSpan(cond.VariableStart, cond.VariableLength);

            var aValue = a.GetValueByName(varName);
            var bValue = b.GetValueByName(varName);

            int cmp = CompareValues(aValue, bValue);
            if (cmp != 0)
            {
                return cond.Direction == OrderDirection.Descending ? -cmp : cmp;
            }
        }
        return 0;
    }

    /// <summary>
    /// Compare two values, handling numeric comparison when possible.
    /// </summary>
    private static int CompareValues(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        // Try numeric comparison first
        if (TryParseNumber(a, out var aNum) && TryParseNumber(b, out var bNum))
        {
            return aNum.CompareTo(bNum);
        }

        // Fall back to string comparison
        return a.SequenceCompareTo(b);
    }

    private static bool TryParseNumber(ReadOnlySpan<char> s, out double result)
    {
        return double.TryParse(s, out result);
    }

    /// <summary>
    /// MoveNext variant for collecting results (no LIMIT/OFFSET applied).
    /// </summary>
    private bool MoveNextUnorderedForCollection()
    {
        while (true)
        {
            bool hasNext;

            if (_isSubQuery)
            {
                hasNext = _subQueryScan.MoveNext(ref _bindingTable);
            }
            else if (_isVariableGraph)
            {
                hasNext = _variableGraphScan.MoveNext(ref _bindingTable);
            }
            else if (_unionBranchActive)
            {
                if (_unionIsMultiPattern)
                    hasNext = _unionMultiScan.MoveNext(ref _bindingTable);
                else
                    hasNext = _unionSingleScan.MoveNext(ref _bindingTable);
            }
            else
            {
                if (_isMultiPattern)
                    hasNext = _multiScan.MoveNext(ref _bindingTable);
                else
                    hasNext = _singleScan.MoveNext(ref _bindingTable);
            }

            if (!hasNext)
            {
                if (!_isSubQuery && !_isVariableGraph && _hasUnion && !_unionBranchActive)
                {
                    _unionBranchActive = true;
                    if (!InitializeUnionBranch())
                        return false;
                    continue;
                }
                return false;
            }

            if (_hasOptional)
            {
                TryMatchOptionalPatterns();
            }

            // Evaluate BIND expressions before FILTER (BIND may create variables used in FILTER)
            if (_hasBinds)
            {
                EvaluateBindExpressions();
            }

            if (_hasFilters && !EvaluateFilters())
            {
                _bindingTable.Clear();
                continue;
            }

            // Apply EXISTS/NOT EXISTS filters
            if (_hasExists && !EvaluateExistsFilters())
            {
                _bindingTable.Clear();
                continue;
            }

            // Apply MINUS - exclude matching rows
            if (_hasMinus)
            {
                if (MatchesMinusPattern())
                {
                    _bindingTable.Clear();
                    continue;
                }
            }

            // Apply VALUES - check if bound value matches any VALUES value
            if (_hasValues)
            {
                if (!MatchesValuesConstraint())
                {
                    _bindingTable.Clear();
                    continue;
                }
            }

            if (_distinct)
            {
                var hash = ComputeBindingsHash();
                if (!_seenHashes!.Add(hash))
                {
                    _bindingTable.Clear();
                    continue;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Move to next result for GROUP BY queries.
    /// Collects all results on first call, groups them, then iterates.
    /// </summary>
    private bool MoveNextGrouped()
    {
        // First call - collect and group results
        if (_groupedResults == null)
        {
            CollectAndGroupResults();
        }

        // Check if we've hit the limit
        if (_limit > 0 && _returned >= _limit)
            return false;

        // Iterate through grouped results
        while (++_groupedIndex < _groupedResults!.Count)
        {
            // Apply OFFSET
            if (_skipped < _offset)
            {
                _skipped++;
                continue;
            }

            // Load the grouped row into binding table
            var group = _groupedResults[_groupedIndex];
            _bindingTable.Clear();

            // Bind the grouping variables
            for (int i = 0; i < group.KeyCount; i++)
            {
                _bindingTable.BindWithHash(group.GetKeyHash(i), group.GetKeyValue(i));
            }

            // Bind the aggregate results
            for (int i = 0; i < group.AggregateCount; i++)
            {
                _bindingTable.BindWithHash(group.GetAggregateHash(i), group.GetAggregateValue(i));
            }

            _returned++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collect all results and group them according to GROUP BY.
    /// </summary>
    private void CollectAndGroupResults()
    {
        var groups = new Dictionary<string, GroupedRow>();
        var sourceStr = _source.ToString();

        // Collect all results using the streaming approach
        while (MoveNextUnorderedForCollection())
        {
            // Build group key from GROUP BY variables
            var keyBuilder = new System.Text.StringBuilder();
            for (int i = 0; i < _groupBy.Count; i++)
            {
                var (start, len) = _groupBy.GetVariable(i);
                var varName = _source.Slice(start, len);
                var bindingIdx = _bindingTable.FindBinding(varName);
                if (bindingIdx >= 0)
                {
                    if (i > 0) keyBuilder.Append('\0');
                    keyBuilder.Append(_bindingTable.GetString(bindingIdx));
                }
            }
            var groupKey = keyBuilder.ToString();

            // Get or create group
            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = new GroupedRow(_groupBy, _selectClause, _bindingTable, sourceStr);
                groups[groupKey] = group;
            }

            // Update aggregates for this group
            group.UpdateAggregates(_bindingTable, sourceStr);
            _bindingTable.Clear();
        }

        // Finalize aggregates (e.g., compute AVG from sum/count) and apply HAVING filter
        _groupedResults = new List<GroupedRow>(groups.Count);
        foreach (var group in groups.Values)
        {
            group.FinalizeAggregates();

            // Apply HAVING filter if present
            if (_hasHaving)
            {
                // Load group bindings into binding table for filter evaluation
                _bindingTable.Clear();
                for (int i = 0; i < group.KeyCount; i++)
                {
                    _bindingTable.BindWithHash(group.GetKeyHash(i), group.GetKeyValue(i));
                }
                for (int i = 0; i < group.AggregateCount; i++)
                {
                    _bindingTable.BindWithHash(group.GetAggregateHash(i), group.GetAggregateValue(i));
                }

                // Evaluate HAVING expression
                var havingExpr = _source.Slice(_having.ExpressionStart, _having.ExpressionLength);
                var evaluator = new FilterEvaluator(havingExpr);
                if (!evaluator.Evaluate(_bindingTable.GetBindings(), _bindingTable.Count, _bindingTable.GetStringBuffer()))
                    continue; // Skip this group - doesn't match HAVING
            }

            _groupedResults.Add(group);
        }

        // Reset for iteration
        _groupedIndex = -1;
        _skipped = 0;
        _returned = 0;
    }

    /// <summary>
    /// Move to next result for non-ORDER BY queries (streaming).
    /// </summary>
    private bool MoveNextUnordered()
    {
        // Check if we've hit the limit
        if (_limit > 0 && _returned >= _limit)
            return false;

        while (true)
        {
            bool hasNext;

            if (_isSubQuery)
            {
                hasNext = _subQueryScan.MoveNext(ref _bindingTable);
            }
            else if (_isVariableGraph)
            {
                hasNext = _variableGraphScan.MoveNext(ref _bindingTable);
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
                if (!_isSubQuery && !_isVariableGraph && _hasUnion && !_unionBranchActive)
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

            // Apply filters
            if (_hasFilters)
            {
                if (!EvaluateFilters())
                {
                    _bindingTable.Clear();
                    continue; // Try next row
                }
            }

            // Apply EXISTS/NOT EXISTS filters
            if (_hasExists)
            {
                if (!EvaluateExistsFilters())
                {
                    _bindingTable.Clear();
                    continue; // EXISTS condition failed
                }
            }

            // Apply MINUS - exclude matching rows
            if (_hasMinus)
            {
                if (MatchesMinusPattern())
                {
                    _bindingTable.Clear();
                    continue; // Matches MINUS, skip this row
                }
            }

            // Apply VALUES - check if bound value matches any VALUES value
            if (_hasValues)
            {
                if (!MatchesValuesConstraint())
                {
                    _bindingTable.Clear();
                    continue; // Doesn't match VALUES, skip this row
                }
            }

            // Apply DISTINCT - skip duplicate rows
            if (_distinct)
            {
                var hash = ComputeBindingsHash();
                if (!_seenHashes!.Add(hash))
                {
                    _bindingTable.Clear();
                    continue; // Duplicate, try next row
                }
            }

            // Apply OFFSET - skip results until we've skipped enough
            if (_skipped < _offset)
            {
                _skipped++;
                _bindingTable.Clear();
                continue;
            }

            _returned++;
            return true;
        }
    }

    /// <summary>
    /// Try to match optional patterns and extend bindings.
    /// If a pattern doesn't match, we continue without it (left outer join).
    /// </summary>
    private void TryMatchOptionalPatterns()
    {
        if (_store == null) return;

        for (int i = 0; i < _pattern.PatternCount; i++)
        {
            if (!_pattern.IsOptional(i)) continue;

            var optPattern = _pattern.GetPattern(i);
            TryMatchSingleOptionalPattern(optPattern);
        }
    }

    /// <summary>
    /// Try to match a single optional pattern and bind its variables.
    /// </summary>
    private void TryMatchSingleOptionalPattern(TriplePattern pattern)
    {
        if (_store == null) return;

        // Resolve terms - variables that are already bound use their value,
        // unbound variables become wildcards
        var subject = ResolveTermForOptional(pattern.Subject);
        var predicate = ResolveTermForOptional(pattern.Predicate);
        var obj = ResolveTermForOptional(pattern.Object);

        // Query the store
        var results = _store.QueryCurrent(subject, predicate, obj);
        try
        {
            if (results.MoveNext())
            {
                var triple = results.Current;

                // Bind any unbound variables from the result
                TryBindOptionalVariable(pattern.Subject, triple.Subject);
                TryBindOptionalVariable(pattern.Predicate, triple.Predicate);
                TryBindOptionalVariable(pattern.Object, triple.Object);
            }
            // If no match, we just don't add bindings (left outer join semantics)
        }
        finally
        {
            results.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForOptional(Term term)
    {
        if (!term.IsVariable)
        {
            // Constant - use source text
            return _source.Slice(term.Start, term.Length);
        }

        // Check if variable is already bound
        var varName = _source.Slice(term.Start, term.Length);
        var idx = _bindingTable.FindBinding(varName);
        if (idx >= 0)
        {
            // Use bound value
            return _bindingTable.GetString(idx);
        }

        // Unbound - use wildcard
        return ReadOnlySpan<char>.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryBindOptionalVariable(Term term, ReadOnlySpan<char> value)
    {
        if (!term.IsVariable) return;

        var varName = _source.Slice(term.Start, term.Length);

        // Only bind if not already bound
        if (_bindingTable.FindBinding(varName) < 0)
        {
            _bindingTable.Bind(varName, value);
        }
    }

    /// <summary>
    /// Compute a hash of all current bindings for DISTINCT checking.
    /// Uses FNV-1a hash combined across all binding values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComputeBindingsHash()
    {
        unchecked
        {
            int hash = (int)2166136261; // FNV offset basis

            for (int i = 0; i < _bindingTable.Count; i++)
            {
                var value = _bindingTable.GetString(i);
                foreach (var ch in value)
                {
                    hash = (hash ^ ch) * 16777619; // FNV prime
                }
                hash = (hash ^ '|') * 16777619; // Separator between bindings
            }

            return hash;
        }
    }

    /// <summary>
    /// Initialize the UNION branch scan.
    /// Returns false if the union branch is empty.
    /// </summary>
    private bool InitializeUnionBranch()
    {
        if (_store == null) return false;

        var unionPatternCount = _pattern.UnionBranchPatternCount;
        if (unionPatternCount == 0) return false;

        // Clear bindings from first branch before starting union branch
        _bindingTable.Clear();

        if (unionPatternCount == 1)
        {
            // Single union pattern - use simple scan
            var tp = _pattern.GetUnionPattern(0);
            _unionSingleScan = new TriplePatternScan(_store, _source, tp, _bindingTable);
            _unionIsMultiPattern = false;
            return true;
        }
        else
        {
            // Multiple union patterns - use multi-pattern scan with union mode
            _unionMultiScan = new MultiPatternScan(_store, _source, _pattern, unionMode: true);
            _unionIsMultiPattern = true;
            return true;
        }
    }

    private bool EvaluateFilters()
    {
        for (int i = 0; i < _pattern.FilterCount; i++)
        {
            var filter = _pattern.GetFilter(i);
            var filterExpr = _source.Slice(filter.Start, filter.Length);

            _filterEvaluator = new FilterEvaluator(filterExpr);
            var result = _filterEvaluator.Evaluate(
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer());

            if (!result) return false;
        }
        return true;
    }

    /// <summary>
    /// Evaluate all EXISTS/NOT EXISTS filters.
    /// Returns true if all filters pass, false if any filter fails.
    /// </summary>
    private bool EvaluateExistsFilters()
    {
        if (_store == null) return true;

        for (int i = 0; i < _pattern.ExistsFilterCount; i++)
        {
            var existsFilter = _pattern.GetExistsFilter(i);
            var matches = EvaluateExistsPattern(existsFilter);

            // EXISTS: must match at least once
            // NOT EXISTS: must not match at all
            if (existsFilter.Negated)
            {
                if (matches) return false; // NOT EXISTS failed - found a match
            }
            else
            {
                if (!matches) return false; // EXISTS failed - no match found
            }
        }
        return true;
    }

    /// <summary>
    /// Check if an EXISTS pattern has at least one match with current bindings.
    /// </summary>
    private bool EvaluateExistsPattern(ExistsFilter existsFilter)
    {
        if (_store == null || existsFilter.PatternCount == 0)
            return false;

        // For each pattern, substitute bound variables and query the store
        // All patterns must match for EXISTS to succeed (conjunction)
        for (int p = 0; p < existsFilter.PatternCount; p++)
        {
            var pattern = existsFilter.GetPattern(p);

            // Resolve terms - use bound values for variables
            var subject = ResolveExistsTerm(pattern.Subject);
            var predicate = ResolveExistsTerm(pattern.Predicate);
            var obj = ResolveExistsTerm(pattern.Object);

            // Query the store
            var results = _store.QueryCurrent(subject, predicate, obj);
            try
            {
                if (!results.MoveNext())
                    return false; // No match for this pattern
            }
            finally
            {
                results.Dispose();
            }
        }

        return true; // All patterns matched
    }

    /// <summary>
    /// Resolve a term for EXISTS evaluation.
    /// Variables are substituted with bound values, constants use source text.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveExistsTerm(Term term)
    {
        if (!term.IsVariable)
        {
            // Constant - use source text
            return _source.Slice(term.Start, term.Length);
        }

        // Variable - check if bound
        var varName = _source.Slice(term.Start, term.Length);
        var idx = _bindingTable.FindBinding(varName);
        if (idx >= 0)
        {
            // Use bound value
            return _bindingTable.GetString(idx);
        }

        // Unbound variable - use wildcard (empty span)
        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// Evaluate all BIND expressions and add bindings to the binding table.
    /// </summary>
    private void EvaluateBindExpressions()
    {
        for (int i = 0; i < _pattern.BindCount; i++)
        {
            var bind = _pattern.GetBind(i);
            var expr = _source.Slice(bind.ExprStart, bind.ExprLength);
            var varName = _source.Slice(bind.VarStart, bind.VarLength);

            // Evaluate the expression
            var evaluator = new BindExpressionEvaluator(expr,
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer());
            var value = evaluator.Evaluate();

            // Bind the result to the target variable using typed overloads
            switch (value.Type)
            {
                case ValueType.Integer:
                    _bindingTable.Bind(varName, value.IntegerValue);
                    break;
                case ValueType.Double:
                    _bindingTable.Bind(varName, value.DoubleValue);
                    break;
                case ValueType.Boolean:
                    _bindingTable.Bind(varName, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.Uri:
                    _bindingTable.Bind(varName, value.StringValue);
                    break;
            }
        }
    }

    /// <summary>
    /// Check if current bindings match all MINUS patterns.
    /// Returns true if any MINUS pattern matches (solution should be excluded).
    /// </summary>
    private bool MatchesMinusPattern()
    {
        if (_store == null) return false;

        // For MINUS semantics: exclude if ALL patterns in MINUS group match
        // We need to check if there's a compatible solution in the MINUS pattern
        for (int i = 0; i < _pattern.MinusPatternCount; i++)
        {
            var minusPattern = _pattern.GetMinusPattern(i);
            if (!MatchesSingleMinusPattern(minusPattern))
            {
                // If any pattern doesn't match, the MINUS doesn't exclude this solution
                return false;
            }
        }

        // All patterns matched - this solution should be excluded
        return _pattern.MinusPatternCount > 0;
    }

    /// <summary>
    /// Check if a single MINUS pattern matches the current bindings.
    /// SPARQL MINUS semantics: exclude if the pattern matches with compatible bindings.
    /// Variables not in current bindings become wildcards.
    /// </summary>
    private bool MatchesSingleMinusPattern(TriplePattern pattern)
    {
        if (_store == null) return false;

        // Resolve terms using current bindings
        // Variables not in bindings become wildcards (empty span)
        var subject = ResolveTermForMinus(pattern.Subject);
        var predicate = ResolveTermForMinus(pattern.Predicate);
        var obj = ResolveTermForMinus(pattern.Object);

        // Query the store to see if this pattern matches
        var results = _store.QueryCurrent(subject, predicate, obj);
        try
        {
            return results.MoveNext(); // Match if at least one triple found
        }
        finally
        {
            results.Dispose();
        }
    }

    /// <summary>
    /// Resolve a term for MINUS pattern matching.
    /// Variables use their bound value, constants use source text.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForMinus(Term term)
    {
        if (!term.IsVariable)
        {
            // Constant - use source text
            return _source.Slice(term.Start, term.Length);
        }

        // Check if variable is bound
        var varName = _source.Slice(term.Start, term.Length);
        var idx = _bindingTable.FindBinding(varName);
        if (idx >= 0)
        {
            // Use bound value
            return _bindingTable.GetString(idx);
        }

        // Unbound - use wildcard
        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// Check if the current bindings match the VALUES constraint.
    /// The VALUES variable must be bound to one of the VALUES values.
    /// </summary>
    private bool MatchesValuesConstraint()
    {
        var values = _pattern.Values;
        if (!values.HasValues) return true;

        // Get the variable name from VALUES
        var varName = _source.Slice(values.VarStart, values.VarLength);

        // Find the binding for this variable
        var bindingIdx = _bindingTable.FindBinding(varName);
        if (bindingIdx < 0)
        {
            // Variable not bound - this is valid in SPARQL (VALUES binds it)
            // For simplicity, we'll allow unbound (implementation could bind it)
            return true;
        }

        // Get the bound value
        var boundValue = _bindingTable.GetString(bindingIdx);

        // Check if it matches any VALUES value
        for (int i = 0; i < values.ValueCount; i++)
        {
            var (start, len) = values.GetValue(i);
            var valuesValue = _source.Slice(start, len);

            if (boundValue.SequenceEqual(valuesValue))
                return true;
        }

        // Bound value doesn't match any VALUES value
        return false;
    }

    public void Dispose()
    {
        _singleScan.Dispose();
        _multiScan.Dispose();
        _unionSingleScan.Dispose();
        _unionMultiScan.Dispose();
        _subQueryScan.Dispose();
    }
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
            bool hasNumValue = false;

            // For COUNT(*), we don't need a specific variable
            if (varHash != ComputeHash("*".AsSpan()))
            {
                var idx = bindings.FindBindingByHash(varHash);
                if (idx >= 0)
                {
                    valueStr = bindings.GetString(idx).ToString();
                    hasNumValue = double.TryParse(valueStr, out numValue);
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

            // Update accumulator based on function
            switch (func)
            {
                case AggregateFunction.Count:
                    _counts[i]++;
                    break;
                case AggregateFunction.Sum:
                    if (hasNumValue) _sums[i] += numValue;
                    break;
                case AggregateFunction.Avg:
                    if (hasNumValue)
                    {
                        _sums[i] += numValue;
                        _counts[i]++;
                    }
                    break;
                case AggregateFunction.Min:
                    if (hasNumValue && numValue < _mins[i])
                        _mins[i] = numValue;
                    break;
                case AggregateFunction.Max:
                    if (hasNumValue && numValue > _maxes[i])
                        _maxes[i] = numValue;
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
                AggregateFunction.Count => _counts[i].ToString(),
                AggregateFunction.Sum => _sums[i].ToString(),
                AggregateFunction.Avg => _counts[i] > 0 ? (_sums[i] / _counts[i]).ToString() : "0",
                AggregateFunction.Min => _mins[i] == double.MaxValue ? "" : _mins[i].ToString(),
                AggregateFunction.Max => _maxes[i] == double.MinValue ? "" : _maxes[i].ToString(),
                AggregateFunction.GroupConcat => _concatValues[i] != null
                    ? string.Join(_separators[i], _concatValues[i]!)
                    : "",
                AggregateFunction.Sample => _sampleValues[i] ?? "",
                _ => ""
            };
        }
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
}

/// <summary>
/// Results from CONSTRUCT query execution. Yields constructed triples.
/// Must be disposed to return pooled resources.
/// </summary>
