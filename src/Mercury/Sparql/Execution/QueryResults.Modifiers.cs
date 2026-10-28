using System;
using System.Collections.Generic;
using System.Globalization;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Sparql.Execution;

internal ref partial struct QueryResults
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
        if (_limitOffset.LimitReached(_returned))
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

            // ADR-047 cutover: pre-materialized rows from the tree carry only the joined pattern bindings — the lazy
            // per-row SELECT-expression evaluation the streaming/collection path applies (MoveNextUnorderedForCollection
            // line 235) never ran for them. Evaluate computed projections ((expr AS ?var)) now, before FILTER/DISTINCT
            // see them and before the projection reads them. Gated on _isMaterialized so the scan-collected ORDER BY path
            // (already evaluated during collection, _isMaterialized=false) is not re-evaluated — a second eval would
            // diverge for non-deterministic expressions (UUID/RAND/NOW). C2: gate on the SELECT clause actually having
            // computed projections (Function=None aggregates), not on _buffer — the sub-SELECT path has expressions to
            // evaluate but no outer buffer (it lends prefixes via _materializedPrefixes instead). A clause with no such
            // expressions makes the block a no-op, so this never fires spuriously.
            if (_isMaterialized && _selectClause.AggregateCount > 0)
            {
                // Per-row bnode seed so BNODE(str) yields a fresh blank node per row (same str → same bnode WITHIN a row,
                // different across rows), matching the streaming/collection path (W3C bnode01).
                FilterEvaluator.IncrementBnodeRowSeed();
                EvaluateSelectExpressions();
            }

            if (!KeepAfterDistinctAndOffset())
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The last solution modifiers, in the order SPARQL 1.1 §18.2.5 applies them: DISTINCT, then OFFSET. (LIMIT is checked
    /// before each row is produced.) Both presentations, ordered rows and grouped rows, call this once per row that every
    /// earlier modifier kept, with the row in the binding table. True when the row is returned; false when DISTINCT drops
    /// it as a repeat or OFFSET skips it.
    /// </summary>
    private bool KeepAfterDistinctAndOffset()
    {
        if (_distinct && !_seenHashes!.Add(ComputeBindingsHash()))
            return false;
        if (_skipped < _limitOffset.Offset)
        {
            _skipped++;
            return false;
        }
        _returned++;
        return true;
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
        long maxResultRows = _store?.MaxResultRows ?? 0; // unbounded-result guard: ORDER BY materializes all rows here
        while (MoveNextUnorderedForCollection())
        {
            // Materialize the current row
            var row = new MaterializedRow(_bindingTable);
            _sortedResults.Add(row);
            _bindingTable.Clear();
            ResultLimitExceededException.ThrowIfExceeded(maxResultRows, _sortedResults.Count);
        }

        // Sort the results
        if (_sortedResults.Count > 1)
        {
            var sourceStr = _source.ToString();
            _sortedResults.Sort(new MaterializedRowComparer(_orderBy, sourceStr, _buffer?.Prefixes ?? _materializedPrefixes));
        }

        // Reset for iteration
        _sortedIndex = -1;
        _skipped = 0;
        _returned = 0;
    }

    /// <summary>
    /// MoveNext variant for collecting results (no LIMIT/OFFSET applied) — the collect-then-present path for GROUP BY
    /// and ORDER BY. ADR-047 d2: only the materialized branch remains (the streaming scan branches are gone); it
    /// iterates the pre-materialized rows. The per-row post-processing below is a no-op for tree-materialized rows
    /// (their FILTER / EXISTS / MINUS / VALUES / OPTIONAL were applied during materialization, so those flags are
    /// false), but is preserved unchanged so collection stays correct for any path that does set them.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool MoveNextUnorderedForCollection()
    {
        while (true)
        {
            // Iterate through the pre-materialized rows.
            _materializedIndex++;
            if (_materializedIndex >= _sortedResults!.Count)
                return false;

            _bindingTable.Clear();
            var row = _sortedResults[_materializedIndex];
            for (int i = 0; i < row.BindingCount; i++)
            {
                _bindingTable.BindWithHash(row.GetHash(i), row.GetValue(i));
            }

            // Increment bnode row seed for this new row so BNODE(str) yields a fresh blank node per row.
            FilterEvaluator.IncrementBnodeRowSeed();

            EvaluateSelectExpressions();

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
        if (_limitOffset.LimitReached(_returned))
            return false;

        // Iterate through grouped results
        while (++_groupedIndex < _groupedResults!.Count)
        {
            // Load the grouped row into binding table
            var group = _groupedResults[_groupedIndex];
            _bindingTable.Clear();

            // Bind the grouping variables
            for (int i = 0; i < group.KeyCount; i++)
            {
                _bindingTable.BindWithHash(group.GetKeyHash(i), group.GetKeyValue(i));
            }

            // Bind the aggregate results
            // Skip empty values - they indicate errors (e.g., AVG with non-numeric values)
            // Per W3C SPARQL semantics, aggregates with errors should return UNBOUND (no binding)
            for (int i = 0; i < group.AggregateCount; i++)
            {
                var value = group.GetAggregateValue(i);
                if (value.Length > 0)
                {
                    _bindingTable.BindWithHash(group.GetAggregateHash(i), value);
                }
            }

            // Evaluate non-aggregate SELECT expressions that may contain aggregate functions
            // e.g., ((MIN(?p) + MAX(?p)) / 2 AS ?c)
            EvaluateGroupedSelectExpressions(group);

            if (!KeepAfterDistinctAndOffset())
                continue;
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
                // Unbounded-result guard: a GROUP BY over a high-cardinality key explodes the group set here, before
                // the result drain ever sees a row. One result row per group, so the group count is the result size.
                ResultLimitExceededException.ThrowIfExceeded(_store?.MaxResultRows ?? 0, groups.Count);
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

                // The condition's brackets end where the parser's expression token rule says
                int condStart = pos + 1;
                pos = new SparqlParser(span).BracketedGroupEndAt(pos);
                var condition = span.Slice(condStart, pos - 1 - condStart);

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
        var result = new System.Text.StringBuilder(expr.Length);
        int cursor = 0;

        // The aggregate calls in the expression, found by the parser's expression token rule
        foreach (var (function, nameStart, _, end) in new SparqlParser(expr.AsSpan()).AggregateCallsIn(0, expr.Length))
        {
            var aggExpr = expr.Substring(nameStart, end - nameStart); // e.g. "COUNT(?O)" or "COUNT(*)"

            // The matching aggregate of the SELECT clause, by its expression
            string? computedValue = null;
            for (int i = 0; i < group.AggregateCount; i++)
            {
                var selectAgg = GetAggregateExpressionString(i, source);
                if (selectAgg != null && AggregateExpressionsMatch(aggExpr, selectAgg))
                {
                    computedValue = group.GetAggregateValue(i).ToString();
                    break;
                }
            }

            // COUNT(*) in HAVING but not in SELECT: the group's row count
            if (computedValue == null && function == AggregateFunction.Count && aggExpr.Contains('*'))
                computedValue = group.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (computedValue == null)
                continue; // left as written
            result.Append(expr, cursor, nameStart - cursor).Append(computedValue);
            cursor = end;
        }

        result.Append(expr, cursor, expr.Length - cursor);
        return result.ToString();
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
        // Case, whitespace and the variable sigil do not matter: COUNT(?s) is count( $s )
        var sb = new System.Text.StringBuilder(expr.Length);
        foreach (var c in expr)
        {
            if (VariableName.IsSigil(c))
                sb.Append('?');
            else if (!char.IsWhiteSpace(c))
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
            var evaluator = new FilterEvaluator(expr);
            var value = evaluator.EvaluateToValue(
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                _buffer?.Prefixes ?? _materializedPrefixes, _source, baseIri);

            // Bind the result to the alias variable
            // Prefer StringValue if available (preserves original datatype annotation)
            switch (value.Type)
            {
                case ValueType.Integer:
                    // Use original string if available (preserves datatype), otherwise format as xsd:integer
                    if (!value.StringValue.IsEmpty)
                        _bindingTable.Bind(aliasVar, value.StringValue);
                    else
                        _bindingTable.Bind(aliasVar, $"\"{value.IntegerValue}\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());
                    break;
                case ValueType.Double:
                    // Use original string if available (preserves decimal vs double distinction)
                    if (!value.StringValue.IsEmpty)
                        _bindingTable.Bind(aliasVar, value.StringValue);
                    else
                        _bindingTable.Bind(aliasVar, $"\"{value.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"^^<http://www.w3.org/2001/XMLSchema#double>".AsSpan());
                    break;
                case ValueType.Boolean:
                    if (!value.StringValue.IsEmpty)
                        _bindingTable.Bind(aliasVar, value.StringValue);
                    else
                        _bindingTable.Bind(aliasVar, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.BlankNode:
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
            var evaluator = new FilterEvaluator(substitutedExpr.AsSpan());
            var value = evaluator.EvaluateToValue(
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                _buffer?.Prefixes ?? _materializedPrefixes, _source);

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
                case ValueType.BlankNode:
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
    private readonly PrefixMapping[]? _prefixes;
    // Per condition: true when it is one variable, read from the row; otherwise an expression, evaluated
    private readonly bool[] _isVariable;
    // An expression condition's value for a row, evaluated once per row rather than once per comparison
    private readonly Dictionary<MaterializedRow, string[]>? _values;

    public MaterializedRowComparer(OrderByClause orderBy, string source, PrefixMapping[]? prefixes = null)
    {
        _orderBy = orderBy;
        _source = source;
        _prefixes = prefixes;
        _isVariable = new bool[orderBy.Count];
        bool anyExpression = false;
        for (int i = 0; i < orderBy.Count; i++)
        {
            var cond = orderBy.GetCondition(i);
            _isVariable[i] = IsOneVariable(source.AsSpan(cond.ExpressionStart, cond.ExpressionLength));
            anyExpression |= !_isVariable[i];
        }
        if (anyExpression)
            _values = new Dictionary<MaterializedRow, string[]>(ReferenceEqualityComparer.Instance);
    }

    private static bool IsOneVariable(ReadOnlySpan<char> text)
    {
        if (text.Length < 2 || !VariableName.IsSigil(text[0]))
            return false;
        foreach (var c in text[1..])
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    public int Compare(MaterializedRow? a, MaterializedRow? b)
    {
        if (a == null || b == null) return 0;

        for (int i = 0; i < _orderBy.Count; i++)
        {
            var cond = _orderBy.GetCondition(i);
            ReadOnlySpan<char> aValue, bValue;
            if (_isVariable[i])
            {
                var varName = _source.AsSpan(cond.ExpressionStart, cond.ExpressionLength);
                aValue = a.GetValueByName(varName);
                bValue = b.GetValueByName(varName);
            }
            else
            {
                aValue = ExpressionValues(a)[i];
                bValue = ExpressionValues(b)[i];
            }

            int cmp = CompareValues(aValue, bValue);
            if (cmp != 0)
            {
                return cond.Direction == OrderDirection.Descending ? -cmp : cmp;
            }
        }
        return 0;
    }

    /// <summary>
    /// ADR-047 top-K — compare a CANDIDATE row still in its <see cref="BindingTable"/> against a kept
    /// <see cref="MaterializedRow"/>, WITHOUT materializing the candidate. Mirrors <see cref="Compare(MaterializedRow,
    /// MaterializedRow)"/> exactly — same ORDER BY terms, same value comparison, same direction — reading the
    /// candidate's values from the binding table instead of a row. Lets the streaming top-K heap reject a row that
    /// sorts after the worst kept without ever allocating it: only rows that ENTER the heap are materialized.
    /// </summary>
    public int CompareBindings(scoped ref BindingTable bindings, MaterializedRow row)
    {
        for (int i = 0; i < _orderBy.Count; i++)
        {
            var cond = _orderBy.GetCondition(i);
            var text = _source.AsSpan(cond.ExpressionStart, cond.ExpressionLength);
            ReadOnlySpan<char> aValue, bValue;
            if (_isVariable[i])
            {
                int idx = bindings.FindBinding(text);
                aValue = idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
                bValue = row.GetValueByName(text);
            }
            else
            {
                aValue = Evaluate(text, ref bindings);
                bValue = ExpressionValues(row)[i];
            }

            int cmp = CompareValues(aValue, bValue);
            if (cmp != 0)
                return cond.Direction == OrderDirection.Descending ? -cmp : cmp;
        }
        return 0;
    }

    /// <summary>The values of <paramref name="row"/>'s expression conditions (null at a variable's index).</summary>
    private string[] ExpressionValues(MaterializedRow row)
    {
        if (_values!.TryGetValue(row, out var values))
            return values;
        values = new string[_orderBy.Count];
        var bindingArray = System.Buffers.ArrayPool<Binding>.Shared.Rent(64);
        var charArray = System.Buffers.ArrayPool<char>.Shared.Rent(1 << 13);
        try
        {
            var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
            row.RestoreBindings(ref table);
            for (int i = 0; i < _orderBy.Count; i++)
            {
                if (_isVariable[i]) continue;
                var cond = _orderBy.GetCondition(i);
                values[i] = Evaluate(_source.AsSpan(cond.ExpressionStart, cond.ExpressionLength), ref table).ToString();
            }
            table.ReturnGrownBuffer();
        }
        finally
        {
            System.Buffers.ArrayPool<Binding>.Shared.Return(bindingArray);
            System.Buffers.ArrayPool<char>.Shared.Return(charArray);
        }
        _values[row] = values;
        return values;
    }

    /// <summary>
    /// The value of an expression condition as an RDF term, by the one [110] evaluator FILTER and BIND use; empty when
    /// it is unbound or an error, which sorts first (§15.1).
    /// </summary>
    private ReadOnlySpan<char> Evaluate(ReadOnlySpan<char> expression, scoped ref BindingTable bindings)
    {
        var evaluator = new FilterEvaluator(expression);
        var value = evaluator.EvaluateToValue(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer(),
            _prefixes, _source.AsSpan());
        return value.Type switch
        {
            ValueType.Unbound => ReadOnlySpan<char>.Empty,
            ValueType.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
            ValueType.Double => value.DoubleValue.ToString("G", CultureInfo.InvariantCulture),
            ValueType.Boolean => value.BooleanValue ? "true" : "false",
            _ => value.StringValue.ToString(),
        };
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
