using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref partial struct QueryResults
{
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
            var sourceStr = _source.ToString();
            _sortedResults.Sort(new MaterializedRowComparer(_orderBy, sourceStr));
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
                if (!_isSubQuery && _hasUnion && !_unionBranchActive)
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
}

/// <summary>
/// Comparer for sorting materialized rows by ORDER BY conditions.
/// Uses a class instead of lambda closure to avoid stack overflow from capturing large structs.
/// </summary>
internal sealed class MaterializedRowComparer : IComparer<MaterializedRow>
{
    private readonly OrderByClause _orderBy;
    private readonly string _source;

    public MaterializedRowComparer(OrderByClause orderBy, string source)
    {
        _orderBy = orderBy;
        _source = source;
    }

    public int Compare(MaterializedRow? a, MaterializedRow? b)
    {
        if (a == null || b == null) return 0;

        for (int i = 0; i < _orderBy.Count; i++)
        {
            var cond = _orderBy.GetCondition(i);
            var varName = _source.AsSpan(cond.VariableStart, cond.VariableLength);

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

    private static int CompareValues(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (double.TryParse(a, out var aNum) && double.TryParse(b, out var bNum))
        {
            return aNum.CompareTo(bNum);
        }
        return a.SequenceCompareTo(b);
    }
}
