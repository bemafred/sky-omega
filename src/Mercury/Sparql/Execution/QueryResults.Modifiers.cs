using System;
using System.Collections.Generic;
using System.Globalization;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref partial struct QueryResults
{
    /// <summary>
    /// Move to next result for ORDER BY queries.
    /// Collects all results on first call, sorts them, then iterates.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
            // Load the materialized row into binding table
            var row = _sortedResults[_sortedIndex];
            _bindingTable.Clear();
            for (int i = 0; i < row.BindingCount; i++)
            {
                _bindingTable.BindWithHash(row.GetHash(i), row.GetValue(i));
            }

            // Apply regular FILTER clauses (for pre-materialized results from GRAPH clauses)
            if (_hasFilters)
            {
                if (!EvaluateFilters())
                    continue; // FILTER condition failed
            }

            // Apply EXISTS/NOT EXISTS filters (for pre-materialized results from GRAPH clauses)
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

            // Apply DISTINCT - skip duplicate rows
            // This must happen after loading bindings so we can compute the hash
            if (_distinct)
            {
                var hash = ComputeBindingsHash();
                if (!_seenHashes!.Add(hash))
                    continue; // Duplicate, try next row
            }

            // Apply OFFSET
            if (_skipped < _offset)
            {
                _skipped++;
                continue;
            }

            _returned++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collect all results and sort them according to ORDER BY.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// MoveNext variant for collecting results (no LIMIT/OFFSET applied).
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool MoveNextUnorderedForCollection()
    {
        while (true)
        {
            bool hasNext;

            if (_isMaterialized)
            {
                // Iterate through pre-materialized results
                _materializedIndex++;
                if (_materializedIndex >= _sortedResults!.Count)
                    return false;

                // Load bindings from materialized row
                _bindingTable.Clear();
                var row = _sortedResults[_materializedIndex];
                for (int i = 0; i < row.BindingCount; i++)
                {
                    _bindingTable.BindWithHash(row.GetHash(i), row.GetValue(i));
                }
                hasNext = true;
            }
            else if (_isSubQuery)
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

            // Increment bnode row seed for this new row - ensures BNODE(str) produces
            // different bnodes for different rows (same string in same row → same bnode)
            BindExpressionEvaluator.IncrementBnodeRowSeed();

            // Evaluate BIND expressions before FILTER (BIND may create variables used in FILTER)
            if (_hasBinds)
            {
                EvaluateBindExpressions();
            }

            // Evaluate non-aggregate SELECT expressions (e.g., (HOURS(?date) AS ?x))
            EvaluateSelectExpressions();

            // Note: Don't clear binding table on rejection - the scan's TruncateTo handles resetting.
            // Clearing here breaks the scan's internal binding count tracking.
            if (_hasFilters && !EvaluateFilters())
                continue;

            // Apply EXISTS/NOT EXISTS filters
            if (_hasExists && !EvaluateExistsFilters())
                continue;

            // Apply MINUS - exclude matching rows
            if (_hasMinus)
            {
                if (MatchesMinusPattern())
                    continue;
            }

            // Apply VALUES - check if bound value matches any VALUES value (inline VALUES in patterns)
            if (_hasValues)
            {
                if (!MatchesValuesConstraint())
                    continue;
            }

            // Apply post-query VALUES - check if bound value matches (VALUES after WHERE clause)
            if (_hasPostQueryValues)
            {
                if (!MatchesPostQueryValuesConstraint())
                    continue;
            }

            if (_distinct)
            {
                var hash = ComputeBindingsHash();
                if (!_seenHashes!.Add(hash))
                    continue;
            }

            return true;
        }
    }

    /// <summary>
    /// Move to next result for GROUP BY queries.
    /// Collects all results on first call, groups them, then iterates.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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

            // Evaluate non-aggregate SELECT expressions that may contain aggregate functions
            // e.g., ((MIN(?p) + MAX(?p)) / 2 AS ?c)
            EvaluateGroupedSelectExpressions(group);

            _returned++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collect all results and group them according to GROUP BY.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void CollectAndGroupResults()
    {
        var groups = new Dictionary<string, GroupedRow>();
        var sourceStr = _source.ToString();

        // Collect all results using the streaming approach
        while (MoveNextUnorderedForCollection())
        {
            // Evaluate GROUP BY expressions and bind results to alias variables
            EvaluateGroupByExpressions();

            // Build group key from GROUP BY variables/aliases
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
            // Note: Do NOT call _bindingTable.Clear() here!
            // The scan manages its own binding state via TruncateTo() for backtracking.
            // Calling Clear() resets _stringOffset to 0, corrupting string data that
            // the scan's stored _bindingCountN values still reference.
        }

        // Handle implicit aggregation with empty result set:
        // When there are aggregates but no GROUP BY and no matching rows,
        // SPARQL requires returning one row with default aggregate values (0 for COUNT/AVG/SUM, etc.)
        if (groups.Count == 0 && _selectClause.HasRealAggregates && !_groupBy.HasGroupBy)
        {
            // Create an empty group with default aggregate values
            _bindingTable.Clear();
            var emptyGroup = new GroupedRow(_groupBy, _selectClause, _bindingTable, sourceStr);
            // Don't call UpdateAggregates - we want the default values (0)
            groups[""] = emptyGroup;
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
                // HAVING can reference aggregates by alias (e.g., ?count > 2) OR by expression (e.g., COUNT(?x) > 2)
                // W3C Grammar: HavingClause ::= 'HAVING' HavingCondition+
                // Multiple conditions are ANDed together: (cond1) (cond2) means cond1 AND cond2
                var havingExpr = _source.Slice(_having.ExpressionStart, _having.ExpressionLength);
                var substitutedExpr = SubstituteAggregatesInHaving(havingExpr, group, sourceStr);

                // Evaluate all conditions - each (expression) must be true
                if (!EvaluateMultipleHavingConditions(substitutedExpr, _bindingTable))
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
    /// Evaluate multiple HAVING conditions (ANDed together).
    /// Handles format: (cond1) (cond2) ... where all conditions must be true.
    /// Also handles single condition: (cond1) or just: cond1
    /// </summary>
    private static bool EvaluateMultipleHavingConditions(string expr, BindingTable bindingTable)
    {
        var span = expr.AsSpan().Trim();
        if (span.Length == 0)
            return true;

        // Check if the expression starts with '(' - multiple conditions format
        if (span[0] == '(')
        {
            int pos = 0;
            while (pos < span.Length)
            {
                // Skip whitespace
                while (pos < span.Length && char.IsWhiteSpace(span[pos]))
                    pos++;

                if (pos >= span.Length)
                    break;

                if (span[pos] != '(')
                    break; // No more conditions

                // Find matching closing paren
                pos++; // Skip '('
                int condStart = pos;
                int depth = 1;
                while (pos < span.Length && depth > 0)
                {
                    if (span[pos] == '(') depth++;
                    else if (span[pos] == ')') depth--;
                    if (depth > 0) pos++;
                }

                var condition = span.Slice(condStart, pos - condStart);
                pos++; // Skip closing ')'

                // Evaluate this condition
                var evaluator = new FilterEvaluator(condition);
                if (!evaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer()))
                    return false; // This condition failed - AND fails
            }

            return true; // All conditions passed
        }

        // Single condition without outer parens - evaluate directly
        var singleEvaluator = new FilterEvaluator(span);
        return singleEvaluator.Evaluate(bindingTable.GetBindings(), bindingTable.Count, bindingTable.GetStringBuffer());
    }

    /// <summary>
    /// Substitute aggregate expressions in HAVING clause with their computed values.
    /// E.g., "COUNT(?O) > 2" becomes "5 > 2" where 5 is the computed COUNT value.
    /// </summary>
    private string SubstituteAggregatesInHaving(ReadOnlySpan<char> havingExpr, GroupedRow group, string source)
    {
        var expr = havingExpr.ToString();

        // Match aggregate patterns and substitute with computed values
        // Patterns: COUNT(*), COUNT(?var), SUM(?var), AVG(?var), MIN(?var), MAX(?var), SAMPLE(?var), GROUP_CONCAT(?var)
        var aggregateFunctions = new[] { "COUNT", "SUM", "AVG", "MIN", "MAX", "SAMPLE", "GROUP_CONCAT" };

        foreach (var funcName in aggregateFunctions)
        {
            int searchStart = 0;
            while (true)
            {
                // Find the function name (case-insensitive)
                int funcIdx = expr.IndexOf(funcName, searchStart, StringComparison.OrdinalIgnoreCase);
                if (funcIdx < 0) break;

                // Find opening paren
                int parenStart = funcIdx + funcName.Length;
                while (parenStart < expr.Length && char.IsWhiteSpace(expr[parenStart]))
                    parenStart++;

                if (parenStart >= expr.Length || expr[parenStart] != '(')
                {
                    searchStart = funcIdx + 1;
                    continue;
                }

                // Find matching closing paren
                int depth = 1;
                int parenEnd = parenStart + 1;
                while (parenEnd < expr.Length && depth > 0)
                {
                    if (expr[parenEnd] == '(') depth++;
                    else if (expr[parenEnd] == ')') depth--;
                    parenEnd++;
                }
                parenEnd--; // Back to the closing paren

                // Extract the full aggregate expression: e.g., "COUNT(?O)" or "COUNT(*)"
                var aggExpr = expr.Substring(funcIdx, parenEnd - funcIdx + 1);

                // Find matching aggregate in the group by comparing with stored aggregates
                string? computedValue = null;
                for (int i = 0; i < group.AggregateCount; i++)
                {
                    // Get the aggregate's alias and look it up in the binding table
                    var aliasHash = group.GetAggregateHash(i);
                    var aliasValue = group.GetAggregateValue(i);

                    // Check if this aggregate matches by comparing the function and variable
                    // We need to match the aggregate expression from SelectClause
                    var selectAgg = GetAggregateExpressionString(i, source);
                    if (selectAgg != null && AggregateExpressionsMatch(aggExpr, selectAgg))
                    {
                        computedValue = aliasValue.ToString();
                        break;
                    }
                }

                // If aggregate not found in SELECT, handle HAVING-only aggregates
                if (computedValue == null)
                {
                    // COUNT(*) in HAVING but not in SELECT - use row count
                    if (funcName.Equals("COUNT", StringComparison.OrdinalIgnoreCase) &&
                        aggExpr.Contains("*"))
                    {
                        computedValue = group.RowCount.ToString();
                    }
                }

                if (computedValue != null)
                {
                    // Replace the aggregate expression with the computed value
                    expr = expr.Substring(0, funcIdx) + computedValue + expr.Substring(parenEnd + 1);
                    searchStart = funcIdx + computedValue.Length;
                }
                else
                {
                    searchStart = parenEnd + 1;
                }
            }
        }

        return expr;
    }

    /// <summary>
    /// Get the string representation of an aggregate expression from the SELECT clause.
    /// </summary>
    private string? GetAggregateExpressionString(int index, string source)
    {
        if (_selectClause.AggregateCount <= index)
            return null;

        var agg = _selectClause.GetAggregate(index);
        var funcName = agg.Function switch
        {
            AggregateFunction.Count => "COUNT",
            AggregateFunction.Sum => "SUM",
            AggregateFunction.Avg => "AVG",
            AggregateFunction.Min => "MIN",
            AggregateFunction.Max => "MAX",
            AggregateFunction.Sample => "SAMPLE",
            AggregateFunction.GroupConcat => "GROUP_CONCAT",
            _ => null
        };

        if (funcName == null) return null;

        // Build the expression string
        if (agg.VariableLength == 0)
        {
            // COUNT(*) case
            return agg.Distinct ? $"{funcName}(DISTINCT *)" : $"{funcName}(*)";
        }

        var variable = source.AsSpan(agg.VariableStart, agg.VariableLength).ToString();
        return agg.Distinct ? $"{funcName}(DISTINCT {variable})" : $"{funcName}({variable})";
    }

    /// <summary>
    /// Check if two aggregate expressions match (e.g., "COUNT(?O)" matches "COUNT(?O)").
    /// Handles case-insensitivity and whitespace differences.
    /// </summary>
    private static bool AggregateExpressionsMatch(string expr1, string expr2)
    {
        // Normalize: remove whitespace, uppercase
        var norm1 = NormalizeAggregateExpr(expr1);
        var norm2 = NormalizeAggregateExpr(expr2);
        return norm1.Equals(norm2, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAggregateExpr(string expr)
    {
        var sb = new System.Text.StringBuilder(expr.Length);
        foreach (var c in expr)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Evaluate GROUP BY expressions and bind results to alias variables.
    /// E.g., for GROUP BY ((?O1 + ?O2) AS ?O12), evaluates (?O1 + ?O2) and binds to ?O12.
    /// </summary>
    private void EvaluateGroupByExpressions()
    {
        for (int i = 0; i < _groupBy.Count; i++)
        {
            if (!_groupBy.IsExpression(i))
                continue; // Simple variable, already bound

            var (aliasStart, aliasLen) = _groupBy.GetVariable(i);
            var (exprStart, exprLen) = _groupBy.GetExpression(i);

            var expr = _source.Slice(exprStart, exprLen);
            var aliasVar = _source.Slice(aliasStart, aliasLen);

            // Get base IRI for relative IRI resolution
            var baseIri = _buffer != null && _buffer.BaseUriLength > 0
                ? _source.Slice(_buffer.BaseUriStart, _buffer.BaseUriLength)
                : ReadOnlySpan<char>.Empty;

            // Evaluate the expression using existing bindings
            var evaluator = new BindExpressionEvaluator(expr,
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                baseIri);
            var value = evaluator.Evaluate();

            // Bind the result to the alias variable
            switch (value.Type)
            {
                case ValueType.Integer:
                    _bindingTable.Bind(aliasVar, value.IntegerValue);
                    break;
                case ValueType.Double:
                    _bindingTable.Bind(aliasVar, value.DoubleValue);
                    break;
                case ValueType.Boolean:
                    _bindingTable.Bind(aliasVar, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.Uri:
                    _bindingTable.Bind(aliasVar, value.StringValue);
                    break;
            }
        }
    }

    /// <summary>
    /// Move to next result for non-ORDER BY queries (streaming).
    /// </summary>

    /// <summary>
    /// Evaluate non-aggregate SELECT expressions in grouped results.
    /// For expressions containing aggregate functions (e.g., (MIN(?p) + MAX(?p)) / 2 AS ?c),
    /// this method substitutes computed aggregate values and evaluates the expression.
    /// </summary>
    private void EvaluateGroupedSelectExpressions(GroupedRow group)
    {
        var sourceStr = _source.ToString();

        for (int i = 0; i < _selectClause.AggregateCount; i++)
        {
            var agg = _selectClause.GetAggregate(i);

            // Only process expressions (Function == None), not regular aggregates
            if (agg.Function != AggregateFunction.None) continue;

            // Skip if no expression to evaluate
            if (agg.VariableLength == 0) continue;

            // Get expression and alias
            var expr = _source.Slice(agg.VariableStart, agg.VariableLength).ToString();
            var aliasName = _source.Slice(agg.AliasStart, agg.AliasLength);

            // Substitute aggregate expressions with computed values
            // This handles expressions like (MIN(?p) + MAX(?p)) / 2
            var substitutedExpr = SubstituteAggregatesInExpression(expr, group, sourceStr);

            // If substitution failed (aggregate not found), leave unbound
            if (string.IsNullOrEmpty(substitutedExpr))
                continue;

            // Evaluate the expression
            var evaluator = new BindExpressionEvaluator(
                substitutedExpr.AsSpan(),
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                ReadOnlySpan<char>.Empty);
            var value = evaluator.Evaluate();

            // Bind the result if valid
            switch (value.Type)
            {
                case ValueType.Integer:
                    _bindingTable.Bind(aliasName, value.IntegerValue);
                    break;
                case ValueType.Double:
                    _bindingTable.Bind(aliasName, value.DoubleValue);
                    break;
                case ValueType.Boolean:
                    _bindingTable.Bind(aliasName, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.Uri:
                    _bindingTable.Bind(aliasName, value.StringValue);
                    break;
                // Unbound values are not bound (leave the variable unset)
            }
        }
    }

    /// <summary>
    /// Substitute aggregate expressions in an expression with their computed values.
    /// Returns null if any aggregate produces an error.
    /// </summary>
    private string? SubstituteAggregatesInExpression(string expr, GroupedRow group, string source)
    {
        var aggregateFunctions = new[] { "COUNT", "SUM", "AVG", "MIN", "MAX", "SAMPLE", "GROUP_CONCAT" };

        foreach (var funcName in aggregateFunctions)
        {
            int searchStart = 0;
            while (true)
            {
                int funcIdx = expr.IndexOf(funcName, searchStart, StringComparison.OrdinalIgnoreCase);
                if (funcIdx < 0) break;

                // Find opening paren
                int parenStart = funcIdx + funcName.Length;
                while (parenStart < expr.Length && char.IsWhiteSpace(expr[parenStart]))
                    parenStart++;

                if (parenStart >= expr.Length || expr[parenStart] != '(')
                {
                    searchStart = funcIdx + 1;
                    continue;
                }

                // Find matching closing paren
                int depth = 1;
                int parenEnd = parenStart + 1;
                while (parenEnd < expr.Length && depth > 0)
                {
                    if (expr[parenEnd] == '(') depth++;
                    else if (expr[parenEnd] == ')') depth--;
                    parenEnd++;
                }
                parenEnd--; // Back to the closing paren

                var aggExpr = expr.Substring(funcIdx, parenEnd - funcIdx + 1);

                // Try to find matching aggregate in the group
                string? computedValue = null;
                for (int i = 0; i < group.AggregateCount; i++)
                {
                    var selectAgg = GetAggregateExpressionString(i, source);
                    if (selectAgg != null && AggregateExpressionsMatch(aggExpr, selectAgg))
                    {
                        var aggValue = group.GetAggregateValue(i).ToString();
                        // If aggregate has error (empty), return null to indicate error
                        if (string.IsNullOrEmpty(aggValue))
                            return null;
                        computedValue = aggValue;
                        break;
                    }
                }

                if (computedValue != null)
                {
                    expr = expr.Substring(0, funcIdx) + computedValue + expr.Substring(parenEnd + 1);
                    searchStart = funcIdx + computedValue.Length;
                }
                else
                {
                    // Aggregate not found in tracked aggregates - can't evaluate
                    return null;
                }
            }
        }

        return expr;
    }
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
        // SPARQL ORDER BY term type ordering: Unbound < Blank nodes < IRIs < Literals
        var aType = GetTermType(a);
        var bType = GetTermType(b);

        if (aType != bType)
        {
            return aType.CompareTo(bType);
        }

        // Same type - compare by value
        // For numeric literals, compare numerically
        if (aType == TermSortType.Literal)
        {
            var aLex = GetLexicalForm(a);
            var bLex = GetLexicalForm(b);

            if (double.TryParse(aLex, NumberStyles.Float, CultureInfo.InvariantCulture, out var aNum) &&
                double.TryParse(bLex, NumberStyles.Float, CultureInfo.InvariantCulture, out var bNum))
            {
                return aNum.CompareTo(bNum);
            }

            // Non-numeric literals - compare lexical forms
            return aLex.SequenceCompareTo(bLex);
        }

        // For IRIs and blank nodes, compare the full representation
        return a.SequenceCompareTo(b);
    }

    private enum TermSortType { Unbound = 0, BlankNode = 1, Iri = 2, Literal = 3 }

    private static TermSortType GetTermType(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty) return TermSortType.Unbound;
        if (term.Length > 0 && term[0] == '<') return TermSortType.Iri;
        if (term.Length > 1 && term[0] == '_' && term[1] == ':') return TermSortType.BlankNode;
        return TermSortType.Literal; // Includes quoted strings and plain literals
    }

    private static ReadOnlySpan<char> GetLexicalForm(ReadOnlySpan<char> literal)
    {
        // Extract lexical form from quoted literal: "value"@lang or "value"^^<type>
        if (literal.Length >= 2 && literal[0] == '"')
        {
            // Find closing quote
            var closeQuote = literal.LastIndexOf('"');
            if (closeQuote > 0)
            {
                return literal.Slice(1, closeQuote - 1);
            }
        }
        return literal;
    }
}
