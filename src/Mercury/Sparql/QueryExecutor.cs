using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql;

/// <summary>
/// Zero-allocation SPARQL query executor using specialized operators.
///
/// Execution model:
/// 1. Parse query → Query struct with triple patterns + filters
/// 2. Build execution plan → Stack of operators
/// 3. Execute → Pull-based iteration through operator pipeline
/// </summary>
public ref struct QueryExecutor
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly Query _query;

    public QueryExecutor(TripleStore store, ReadOnlySpan<char> source, Query query)
    {
        _store = store;
        _source = source;
        _query = query;
    }

    /// <summary>
    /// Execute a parsed query and return results.
    /// Caller must hold read lock on store and call Dispose on results.
    /// </summary>
    public QueryResults Execute()
    {
        var pattern = _query.WhereClause.Pattern;
        var limit = _query.SolutionModifier.Limit;
        var offset = _query.SolutionModifier.Offset;
        var distinct = _query.SelectClause.Distinct;
        var orderBy = _query.SolutionModifier.OrderBy;

        if (pattern.PatternCount == 0)
            return QueryResults.Empty();

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

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
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable);

            return new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer, limit, offset, distinct, orderBy);
        }

        // No required patterns but have optional - need special handling
        if (requiredCount == 0)
        {
            // All patterns are optional - start with empty bindings and try to match optionals
            // For now, just return empty (proper implementation would need different semantics)
            return QueryResults.Empty();
        }

        // Multiple required patterns - need join
        return ExecuteWithJoins(pattern, bindings, stringBuffer, limit, offset, distinct, orderBy);
    }

    /// <summary>
    /// Execute an ASK query and return true if any result exists.
    /// Caller must hold read lock on store.
    /// </summary>
    public bool ExecuteAsk()
    {
        var pattern = _query.WhereClause.Pattern;

        if (pattern.PatternCount == 0)
            return false;

        // Build binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
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
            var scan = new TriplePatternScan(_store, _source, tp, bindingTable);

            // For ASK, we just need to know if any result exists
            // No need for LIMIT/OFFSET/DISTINCT/ORDER BY
            var results = new QueryResults(scan, pattern, _source, _store, bindings, stringBuffer);
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
        var multiScan = new MultiPatternScan(_store, _source, pattern);
        var multiResults = new QueryResults(multiScan, pattern, _source, _store, bindings, stringBuffer);
        try
        {
            return multiResults.MoveNext();
        }
        finally
        {
            multiResults.Dispose();
        }
    }

    private QueryResults ExecuteWithJoins(GraphPattern pattern, Binding[] bindings, char[] stringBuffer,
        int limit, int offset, bool distinct, OrderByClause orderBy)
    {
        // Use nested loop join for required patterns only
        return new QueryResults(
            new MultiPatternScan(_store, _source, pattern),
            pattern,
            _source,
            _store,
            bindings,
            stringBuffer,
            limit,
            offset,
            distinct,
            orderBy);
    }
}

/// <summary>
/// Results from query execution. Must be disposed to return pooled resources.
/// </summary>
public ref struct QueryResults
{
    private TriplePatternScan _singleScan;
    private MultiPatternScan _multiScan;
    private GraphPattern _pattern;
    private ReadOnlySpan<char> _source;
    private TripleStore? _store;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private BindingTable _bindingTable;
    private readonly bool _hasFilters;
    private readonly bool _hasOptional;
    private readonly bool _isMultiPattern;
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

    public static QueryResults Empty()
    {
        var result = new QueryResults();
        result._isEmpty = true;
        return result;
    }

    internal QueryResults(TriplePatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default)
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
    }

    internal QueryResults(MultiPatternScan scan, GraphPattern pattern, ReadOnlySpan<char> source,
        TripleStore store, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false, OrderByClause orderBy = default)
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

            if (_unionBranchActive)
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
                if (_hasUnion && !_unionBranchActive)
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

            if (_unionBranchActive)
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
                if (_hasUnion && !_unionBranchActive)
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
/// Scans a single triple pattern against the store.
/// Binds matching values to variables.
/// </summary>
public ref struct TriplePatternScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly TriplePattern _pattern;
    private TemporalResultEnumerator _enumerator;
    private bool _initialized;
    private readonly BindingTable _initialBindings;

    public TriplePatternScan(TripleStore store, ReadOnlySpan<char> source,
        TriplePattern pattern, BindingTable initialBindings)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _initialBindings = initialBindings;
        _initialized = false;
        _enumerator = default;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (!_initialized)
        {
            Initialize();
            _initialized = true;
        }

        while (_enumerator.MoveNext())
        {
            var triple = _enumerator.Current;

            bindings.Clear();

            // Bind variables to values from result
            if (!TryBindVariable(_pattern.Subject, triple.Subject, ref bindings))
                continue;
            if (!TryBindVariable(_pattern.Predicate, triple.Predicate, ref bindings))
                continue;
            if (!TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                continue;

            return true;
        }

        return false;
    }

    private void Initialize()
    {
        // Resolve terms to spans for querying
        var subject = ResolveTermForQuery(_pattern.Subject);
        var predicate = ResolveTermForQuery(_pattern.Predicate);
        var obj = ResolveTermForQuery(_pattern.Object);

        _enumerator = _store.QueryCurrent(subject, predicate, obj);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForQuery(Term term)
    {
        // Variables become wildcards (empty span)
        if (term.IsVariable)
            return ReadOnlySpan<char>.Empty;

        // IRIs and literals use their source text
        return _source.Slice(term.Start, term.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(Term term, ReadOnlySpan<char> value, ref BindingTable bindings)
    {
        if (!term.IsVariable)
        {
            // Constant - verify it matches (should already match from query)
            return true;
        }

        var varName = _source.Slice(term.Start, term.Length);

        // Check if already bound (from earlier pattern in join)
        var existingIndex = bindings.FindBinding(varName);
        if (existingIndex >= 0)
        {
            // Must match existing binding
            var existingValue = bindings.GetString(existingIndex);
            return value.SequenceEqual(existingValue);
        }

        // Bind new variable
        bindings.Bind(varName, value);
        return true;
    }

    public void Dispose()
    {
        _enumerator.Dispose();
    }
}

/// <summary>
/// Executes multiple triple patterns using nested loop join.
/// Due to ref struct limitations, we store resolved term strings in fixed buffers
/// to avoid span scoping issues with enumerator initialization.
/// </summary>
public ref struct MultiPatternScan
{
    private readonly TripleStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly GraphPattern _pattern;
    private readonly bool _unionMode;

    // Current state for each pattern level
    private TemporalResultEnumerator _enum0;
    private TemporalResultEnumerator _enum1;
    private TemporalResultEnumerator _enum2;
    private TemporalResultEnumerator _enum3;

    private int _currentLevel;
    private bool _init0, _init1, _init2, _init3;
    private bool _exhausted;

    // Track binding count at entry to each level for rollback
    private int _bindingCount0, _bindingCount1, _bindingCount2, _bindingCount3;

    public MultiPatternScan(TripleStore store, ReadOnlySpan<char> source, GraphPattern pattern, bool unionMode = false)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _unionMode = unionMode;
        _currentLevel = 0;
        _init0 = _init1 = _init2 = _init3 = false;
        _exhausted = false;
        _enum0 = default;
        _enum1 = default;
        _enum2 = default;
        _enum3 = default;
        _bindingCount0 = _bindingCount1 = _bindingCount2 = _bindingCount3 = 0;
    }

    /// <summary>
    /// Get pattern at index, respecting union mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TriplePattern GetPatternAt(int index)
    {
        return _unionMode ? _pattern.GetUnionPattern(index) : _pattern.GetPattern(index);
    }

    /// <summary>
    /// Get the number of patterns to process.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetPatternCount()
    {
        return _unionMode ? _pattern.UnionBranchPatternCount : _pattern.RequiredPatternCount;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        // Get pattern count based on mode
        var patternCount = Math.Min(GetPatternCount(), 4); // Support up to 4 patterns
        if (patternCount == 0)
            return false;

        while (true)
        {
            bool advanced;
            switch (_currentLevel)
            {
                case 0: advanced = TryAdvanceLevel0(ref bindings); break;
                case 1: advanced = TryAdvanceLevel1(ref bindings); break;
                case 2: advanced = TryAdvanceLevel2(ref bindings); break;
                case 3: advanced = TryAdvanceLevel3(ref bindings); break;
                default: advanced = false; break;
            }

            if (advanced)
            {
                if (_currentLevel == patternCount - 1)
                {
                    // At deepest level - we have a complete result
                    return true;
                }
                else
                {
                    // Go deeper
                    _currentLevel++;
                    SetInitialized(_currentLevel, false);
                }
            }
            else
            {
                // Current level exhausted, backtrack
                if (_currentLevel == 0)
                {
                    _exhausted = true;
                    return false;
                }

                // Go up
                _currentLevel--;
            }
        }
    }

    private void SetInitialized(int level, bool value)
    {
        switch (level)
        {
            case 0: _init0 = value; break;
            case 1: _init1 = value; break;
            case 2: _init2 = value; break;
            case 3: _init3 = value; break;
        }
    }

    private bool TryAdvanceLevel0(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(0);

        if (!_init0)
        {
            _bindingCount0 = bindings.Count;
            InitializeEnumerator0(pattern, ref bindings);
            _init0 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount0);
        }

        return TryAdvanceEnumerator(ref _enum0, pattern, ref bindings);
    }

    private bool TryAdvanceLevel1(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(1);

        if (!_init1)
        {
            _bindingCount1 = bindings.Count;
            InitializeEnumerator1(pattern, ref bindings);
            _init1 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount1);
        }

        return TryAdvanceEnumerator(ref _enum1, pattern, ref bindings);
    }

    private bool TryAdvanceLevel2(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(2);

        if (!_init2)
        {
            _bindingCount2 = bindings.Count;
            InitializeEnumerator2(pattern, ref bindings);
            _init2 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount2);
        }

        return TryAdvanceEnumerator(ref _enum2, pattern, ref bindings);
    }

    private bool TryAdvanceLevel3(scoped ref BindingTable bindings)
    {
        var pattern = GetPatternAt(3);

        if (!_init3)
        {
            _bindingCount3 = bindings.Count;
            InitializeEnumerator3(pattern, ref bindings);
            _init3 = true;
        }
        else
        {
            // Rolling back bindings from previous attempt at this level
            bindings.TruncateTo(_bindingCount3);
        }

        return TryAdvanceEnumerator(ref _enum3, pattern, ref bindings);
    }

    // Each init method resolves terms and creates the enumerator.
    // Terms resolve to either a slice of _source (for constants) or empty span (for unbound variables).
    // Bound variables need their value copied to avoid span escaping issues.
    private void InitializeEnumerator0(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum0);
    }

    private void InitializeEnumerator1(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum1);
    }

    private void InitializeEnumerator2(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum2);
    }

    private void InitializeEnumerator3(TriplePattern pattern, scoped ref BindingTable bindings)
    {
        ResolveAndQuery(pattern, ref bindings, out _enum3);
    }

    private void ResolveAndQuery(TriplePattern pattern, scoped ref BindingTable bindings, out TemporalResultEnumerator enumerator)
    {
        // For each term, resolve to a span. Constants come from _source (stable),
        // unbound variables become empty span. Bound variables need special handling.
        ReadOnlySpan<char> subject, predicate, obj;

        // Resolve subject
        if (!pattern.Subject.IsVariable)
            subject = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
        else
        {
            var varName = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
            var idx = bindings.FindBinding(varName);
            subject = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        // Resolve predicate
        if (!pattern.Predicate.IsVariable)
            predicate = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
        else
        {
            var varName = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
            var idx = bindings.FindBinding(varName);
            predicate = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        // Resolve object
        if (!pattern.Object.IsVariable)
            obj = _source.Slice(pattern.Object.Start, pattern.Object.Length);
        else
        {
            var varName = _source.Slice(pattern.Object.Start, pattern.Object.Length);
            var idx = bindings.FindBinding(varName);
            obj = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
        }

        enumerator = _store.QueryCurrent(subject, predicate, obj);
    }

    private bool TryAdvanceEnumerator(ref TemporalResultEnumerator enumerator,
        TriplePattern pattern, scoped ref BindingTable bindings)
    {
        while (enumerator.MoveNext())
        {
            var triple = enumerator.Current;

            // Try to bind variables, checking consistency with existing bindings
            if (TryBindVariable(pattern.Subject, triple.Subject, ref bindings) &&
                TryBindVariable(pattern.Predicate, triple.Predicate, ref bindings) &&
                TryBindVariable(pattern.Object, triple.Object, ref bindings))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(Term term, ReadOnlySpan<char> value, scoped ref BindingTable bindings)
    {
        if (!term.IsVariable)
            return true;

        var varName = _source.Slice(term.Start, term.Length);
        var existingIndex = bindings.FindBinding(varName);

        if (existingIndex >= 0)
        {
            var existingValue = bindings.GetString(existingIndex);
            return value.SequenceEqual(existingValue);
        }

        bindings.Bind(varName, value);
        return true;
    }

    public void Dispose()
    {
        _enum0.Dispose();
        _enum1.Dispose();
        _enum2.Dispose();
        _enum3.Dispose();
    }
}

/// <summary>
/// Evaluates BIND expressions and returns the computed value.
/// Supports: variables, literals, arithmetic (+, -, *, /), parentheses.
/// </summary>
public ref struct BindExpressionEvaluator
{
    private ReadOnlySpan<char> _expression;
    private int _position;
    private ReadOnlySpan<Binding> _bindingData;
    private ReadOnlySpan<char> _bindingStrings;
    private int _bindingCount;

    public BindExpressionEvaluator(ReadOnlySpan<char> expression,
        ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer)
    {
        _expression = expression;
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
    }

    /// <summary>
    /// Evaluate the expression and return a Value.
    /// </summary>
    public Value Evaluate()
    {
        _position = 0;
        return ParseAdditive();
    }

    /// <summary>
    /// Additive := Multiplicative (('+' | '-') Multiplicative)*
    /// </summary>
    private Value ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd()) break;

            var ch = Peek();
            if (ch == '+')
            {
                Advance();
                var right = ParseMultiplicative();
                left = Add(left, right);
            }
            else if (ch == '-')
            {
                Advance();
                var right = ParseMultiplicative();
                left = Subtract(left, right);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    /// <summary>
    /// Multiplicative := Unary (('*' | '/') Unary)*
    /// </summary>
    private Value ParseMultiplicative()
    {
        var left = ParseUnary();

        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd()) break;

            var ch = Peek();
            if (ch == '*')
            {
                Advance();
                var right = ParseUnary();
                left = Multiply(left, right);
            }
            else if (ch == '/')
            {
                Advance();
                var right = ParseUnary();
                left = Divide(left, right);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    /// <summary>
    /// Unary := '-'? Primary
    /// </summary>
    private Value ParseUnary()
    {
        SkipWhitespace();

        if (Peek() == '-')
        {
            Advance();
            var val = ParsePrimary();
            return Negate(val);
        }

        return ParsePrimary();
    }

    /// <summary>
    /// Primary := '(' Additive ')' | Variable | Literal | FunctionCall
    /// </summary>
    private Value ParsePrimary()
    {
        SkipWhitespace();

        var ch = Peek();

        // Parenthesized expression
        if (ch == '(')
        {
            Advance();
            var result = ParseAdditive();
            SkipWhitespace();
            if (Peek() == ')') Advance();
            return result;
        }

        // Variable
        if (ch == '?')
        {
            return ParseVariable();
        }

        // String literal
        if (ch == '"')
        {
            return ParseStringLiteral();
        }

        // Numeric literal
        if (IsDigit(ch))
        {
            return ParseNumericLiteral();
        }

        // Function call or boolean literal
        if (IsLetter(ch))
        {
            return ParseFunctionOrLiteral();
        }

        return new Value { Type = ValueType.Unbound };
    }

    private Value ParseVariable()
    {
        var start = _position;
        Advance(); // Skip '?'

        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        var varName = _expression.Slice(start, _position - start);
        var index = FindBinding(varName);

        if (index < 0)
            return new Value { Type = ValueType.Unbound };

        ref readonly var binding = ref _bindingData[index];
        return binding.Type switch
        {
            BindingValueType.Integer => new Value { Type = ValueType.Integer, IntegerValue = binding.IntegerValue },
            BindingValueType.Double => new Value { Type = ValueType.Double, DoubleValue = binding.DoubleValue },
            BindingValueType.Boolean => new Value { Type = ValueType.Boolean, BooleanValue = binding.BooleanValue },
            BindingValueType.String => new Value { Type = ValueType.String, StringValue = _bindingStrings.Slice(binding.StringOffset, binding.StringLength) },
            BindingValueType.Uri => new Value { Type = ValueType.Uri, StringValue = _bindingStrings.Slice(binding.StringOffset, binding.StringLength) },
            _ => new Value { Type = ValueType.Unbound }
        };
    }

    private int FindBinding(ReadOnlySpan<char> variableName)
    {
        var hash = ComputeVariableHash(variableName);
        for (int i = 0; i < _bindingCount; i++)
        {
            if (_bindingData[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    private static int ComputeVariableHash(ReadOnlySpan<char> value)
    {
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }

    private Value ParseStringLiteral()
    {
        Advance(); // Skip '"'
        var start = _position;

        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\\') Advance();
            Advance();
        }

        var str = _expression.Slice(start, _position - start);

        if (!IsAtEnd()) Advance(); // Skip closing '"'

        return new Value { Type = ValueType.String, StringValue = str };
    }

    private Value ParseNumericLiteral()
    {
        var start = _position;

        while (!IsAtEnd() && IsDigit(Peek()))
            Advance();

        if (!IsAtEnd() && Peek() == '.')
        {
            Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();

            var str = _expression.Slice(start, _position - start);
            if (double.TryParse(str, out var d))
                return new Value { Type = ValueType.Double, DoubleValue = d };
        }
        else
        {
            var str = _expression.Slice(start, _position - start);
            if (long.TryParse(str, out var i))
                return new Value { Type = ValueType.Integer, IntegerValue = i };
        }

        return new Value { Type = ValueType.Unbound };
    }

    private Value ParseFunctionOrLiteral()
    {
        var start = _position;

        while (!IsAtEnd() && IsLetterOrDigit(Peek()))
            Advance();

        var name = _expression.Slice(start, _position - start);

        // Boolean literals
        if (name.Equals("true", StringComparison.OrdinalIgnoreCase))
            return new Value { Type = ValueType.Boolean, BooleanValue = true };
        if (name.Equals("false", StringComparison.OrdinalIgnoreCase))
            return new Value { Type = ValueType.Boolean, BooleanValue = false };

        // Function call
        SkipWhitespace();
        if (Peek() == '(')
        {
            Advance();
            var arg = ParseAdditive();
            SkipWhitespace();
            if (Peek() == ')') Advance();

            // STR function
            if (name.Equals("STR", StringComparison.OrdinalIgnoreCase))
                return arg;

            // STRLEN function
            if (name.Equals("STRLEN", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                    return new Value { Type = ValueType.Integer, IntegerValue = arg.StringValue.Length };
                return new Value { Type = ValueType.Unbound };
            }

            // UCASE function
            if (name.Equals("UCASE", StringComparison.OrdinalIgnoreCase))
                return arg; // Would need buffer for actual uppercase

            // LCASE function
            if (name.Equals("LCASE", StringComparison.OrdinalIgnoreCase))
                return arg; // Would need buffer for actual lowercase
        }

        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Coerce a Value to a number. Strings are parsed as numbers.
    /// Returns NaN if coercion fails.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CoerceToNumber(Value val)
    {
        return val.Type switch
        {
            ValueType.Integer => val.IntegerValue,
            ValueType.Double => val.DoubleValue,
            ValueType.String => TryParseNumber(val.StringValue),
            _ => double.NaN
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double TryParseNumber(ReadOnlySpan<char> s)
    {
        if (double.TryParse(s, out var result))
            return result;
        return double.NaN;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Add(Value left, Value right)
    {
        // Both integers - return integer
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue + right.IntegerValue };

        // Coerce to numbers (handles strings)
        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r))
            return new Value { Type = ValueType.Unbound };

        // Check if result is integer
        var result = l + r;
        if (result == Math.Floor(result) && result >= long.MinValue && result <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = (long)result };
        return new Value { Type = ValueType.Double, DoubleValue = result };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Subtract(Value left, Value right)
    {
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue - right.IntegerValue };

        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r))
            return new Value { Type = ValueType.Unbound };

        var result = l - r;
        if (result == Math.Floor(result) && result >= long.MinValue && result <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = (long)result };
        return new Value { Type = ValueType.Double, DoubleValue = result };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Multiply(Value left, Value right)
    {
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue * right.IntegerValue };

        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r))
            return new Value { Type = ValueType.Unbound };

        var result = l * r;
        if (result == Math.Floor(result) && result >= long.MinValue && result <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = (long)result };
        return new Value { Type = ValueType.Double, DoubleValue = result };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Divide(Value left, Value right)
    {
        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r) || r == 0)
            return new Value { Type = ValueType.Unbound };
        return new Value { Type = ValueType.Double, DoubleValue = l / r };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Negate(Value val)
    {
        if (val.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = -val.IntegerValue };

        var n = CoerceToNumber(val);
        if (double.IsNaN(n))
            return new Value { Type = ValueType.Unbound };

        if (n == Math.Floor(n) && n >= long.MinValue && n <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = -(long)n };
        return new Value { Type = ValueType.Double, DoubleValue = -n };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipWhitespace()
    {
        while (!IsAtEnd() && IsWhitespace(Peek()))
            Advance();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAtEnd() => _position >= _expression.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => IsAtEnd() ? '\0' : _expression[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Advance() => IsAtEnd() ? '\0' : _expression[_position++];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhitespace(char ch) => ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetterOrDigit(char ch) => IsLetter(ch) || IsDigit(ch);
}
