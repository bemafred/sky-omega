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
            CollectAndSortResults();
        else if (!_sortedResultsMade)
            MakeSortedResults();

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

            if (!KeepAfterDistinctAndOffset())
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Pre-materialized rows (the tree's), made into the solutions ORDER BY sorts: each with its SELECT expressions
    /// (§18.2.4.4), then sorted, since §18.2.5 orders after them, so ORDER BY can read an expression's variable. The
    /// rows the collection path makes have their expressions already (CollectAndSortResults); evaluating them again
    /// would diverge for UUID, RAND and NOW.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void MakeSortedResults()
    {
        _sortedResultsMade = true;
        var rows = _sortedResults!;

        // The sub-SELECT path has expressions but no outer buffer (it lends its prefixes via _materializedPrefixes), so
        // the gate is the SELECT clause having computed projections (Function=None aggregates)
        if (_isMaterialized && _selectClause.AggregateCount > 0)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                _bindingTable.Clear();
                var row = rows[r];
                for (int i = 0; i < row.BindingCount; i++)
                    _bindingTable.BindWithHash(row.GetHash(i), row.GetValue(i));

                // Per-row bnode seed so BNODE(str) yields a fresh blank node per row (same str → same bnode WITHIN a
                // row, different across rows), matching the collection path (W3C bnode01)
                FilterEvaluator.IncrementBnodeRowSeed();
                EvaluateSelectExpressions();
                rows[r] = new MaterializedRow(_bindingTable);
            }
        }

        if (_orderBy.HasOrderBy && rows.Count > 1)
            rows.Sort(new MaterializedRowComparer(_orderBy, _source.ToString(), _buffer?.Prefixes ?? _materializedPrefixes));
        _bindingTable.Clear();
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
    /// Move to next result for GROUP BY queries. On the first call the solutions are grouped (with HAVING and the
    /// trailing VALUES), each group's solution is made with its SELECT expressions, and the solutions are ordered by
    /// ORDER BY: §18.2.5 orders after the SELECT expressions, so ORDER BY reads an aggregate's or expression's
    /// variable, and a trailing VALUES' variable. NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool MoveNextGrouped()
    {
        if (_groupedResults == null)
            CollectAndGroupResults();
        if (_groupedSolutions == null)
            MakeGroupedSolutions();

        if (_limitOffset.LimitReached(_returned))
            return false;

        while (++_groupedIndex < _groupedSolutions!.Count)
        {
            _bindingTable.Clear();
            _groupedSolutions[_groupedIndex].RestoreBindings(ref _bindingTable);
            if (!KeepAfterDistinctAndOffset())
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The grouped solutions: for each group HAVING kept, its GROUP BY variables and aggregates, what the trailing
    /// VALUES row it was joined with binds (§18.2.4.3), and its SELECT expressions (§18.2.4.4); then sorted by ORDER BY.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void MakeGroupedSolutions()
    {
        _groupedSolutions = new List<MaterializedRow>(_groupedResults!.Count);
        var comparer = _orderBy.HasOrderBy
            ? new MaterializedRowComparer(_orderBy, _source.ToString(), _buffer?.Prefixes ?? _materializedPrefixes)
            : null;
        for (int g = 0; g < _groupedResults.Count; g++)
        {
            var group = _groupedResults[g];
            BindGroup(group);

            if (_groupedValues != null)
            {
                var values = _groupedValues[g];
                for (int i = 0; i < values.BindingCount; i++)
                {
                    if (_bindingTable.FindBindingByHash(values.GetHash(i)) < 0)
                        _bindingTable.BindWithHash(values.GetHash(i), values.GetValue(i), values.GetValueType(i));
                }
            }

            // Non-aggregate SELECT expressions, which may contain aggregates: ((MIN(?p) + MAX(?p)) / 2 AS ?c)
            EvaluateGroupedSelectExpressions(group);
            var solution = new MaterializedRow(_bindingTable);
            _groupedSolutions.Add(solution);

            // An ORDER BY expression's value, with the group's aggregates (ORDER BY COUNT(*)); an error sorts first
            if (comparer != null)
            {
                var values = new string[_orderBy.Count];
                for (int c = 0; c < _orderBy.Count; c++)
                {
                    if (comparer.IsVariableCondition(c))
                        continue;
                    var condition = _orderBy.GetCondition(c);
                    var expression = WithAggregateValues(condition.ExpressionStart, condition.ExpressionLength, group);
                    values[c] = expression == null ? "" : comparer.Evaluate(expression, ref _bindingTable).ToString();
                }
                comparer.SetExpressionValues(solution, values);
            }
        }

        if (comparer != null && _groupedSolutions.Count > 1)
            _groupedSolutions.Sort(comparer);

        _groupedIndex = -1;
        _skipped = 0;
        _returned = 0;
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
                BindGroup(group);

                // HavingClause ::= 'HAVING' HavingCondition+, ANDed: each condition's aggregates are the group's
                // (§18.2.4.1), and an error, an aggregate's included, removes the group as FILTER does
                var condition = WithAggregateValues(_having.ExpressionStart, _having.ExpressionLength, group);
                if (condition == null || !EvaluateMultipleHavingConditions(condition, _bindingTable))
                    continue;
            }

            _groupedResults.Add(group);
        }

        if (_trailingValues != null)
            JoinTrailingValues();

        // Reset for iteration
        _groupedIndex = -1;
        _skipped = 0;
        _returned = 0;
    }

    /// <summary>
    /// The group's solution in the binding table: its GROUP BY variables and its aggregates' variables. A key the
    /// group's solutions leave unbound, and an aggregate that is an error, are unbound (§18.5); an aggregate inside an
    /// expression, HAVING or ORDER BY has no variable (its value is substituted, WithAggregateValues).
    /// </summary>
    private void BindGroup(GroupedRow group)
    {
        _bindingTable.Clear();
        for (int i = 0; i < group.KeyCount; i++)
        {
            var key = group.GetKeyValue(i);
            if (key.Length > 0)
                _bindingTable.BindWithHash(group.GetKeyHash(i), key);
        }
        for (int i = 0; i < group.AggregateCount; i++)
        {
            var value = group.GetAggregateValue(i);
            if (value.Length > 0 && _selectClause.GetAggregate(i).AliasLength > 0)
                _bindingTable.BindWithHash(group.GetAggregateHash(i), value);
        }
    }

    /// <summary>
    /// §18.2.4.3: a grouped query's trailing VALUES is joined with the groups that HAVING keeps, before the SELECT
    /// expressions. A group is kept once for each data row compatible with its solution, so a data row can bind a
    /// variable the SELECT clause projects, and one with no compatible group removes nothing but itself.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void JoinTrailingValues()
    {
        var joined = new List<GroupedRow>();
        var values = new List<MaterializedRow>();
        foreach (var group in _groupedResults!)
        {
            BindGroup(group);
            var solution = new MaterializedRow(_bindingTable);
            foreach (var row in _trailingValues!)
            {
                if (!TreeJoinExecutor.Compatible(solution, row))
                    continue;
                joined.Add(group);
                values.Add(row);
            }
        }
        _groupedResults = joined;
        _groupedValues = values;
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
    /// The expression at <c>[start, start + length)</c> of the source with each aggregate call in it replaced by the
    /// group's value of that aggregate: §18.2.4.1 computes the aggregates of the SELECT expressions, HAVING and ORDER BY
    /// for each group (the parser adds those of HAVING and ORDER BY to the SELECT clause, hidden). Null when one of
    /// them is an error, which makes the expression one.
    /// </summary>
    private string? WithAggregateValues(int start, int length, GroupedRow group)
    {
        var parser = new SparqlParser(_source);
        var text = new System.Text.StringBuilder(length);
        int cursor = start;
        foreach (var (function, nameStart, parenStart, end) in parser.AggregateCallsIn(start, length))
        {
            var call = parser.AggregateCallAt(function, parenStart);
            string? value = null;
            for (int i = 0; i < group.AggregateCount && value == null; i++)
            {
                if (parser.SameAggregate(_selectClause.GetAggregate(i), call))
                    value = group.GetAggregateValue(i).ToString();
            }
            if (string.IsNullOrEmpty(value))
                return null;
            text.Append(_source[cursor..nameStart]).Append(value);
            cursor = end;
        }
        text.Append(_source[cursor..(start + length)]);
        return text.ToString();
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

            value.BindTo(ref _bindingTable, aliasVar); // the term, or nothing for an error (ADR-056 D6)
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
        for (int i = 0; i < _selectClause.AggregateCount; i++)
        {
            var agg = _selectClause.GetAggregate(i);

            // Only process expressions (Function == None), not regular aggregates
            if (agg.Function != AggregateFunction.None) continue;

            // Skip if no expression to evaluate
            if (agg.VariableLength == 0) continue;

            // The expression with its aggregates' values, e.g. (MIN(?p) + MAX(?p)) / 2; an error leaves it unbound
            var aliasName = _source.Slice(agg.AliasStart, agg.AliasLength);
            var substitutedExpr = WithAggregateValues(agg.VariableStart, agg.VariableLength, group);
            if (substitutedExpr == null)
                continue;

            // Evaluate the expression
            var evaluator = new FilterEvaluator(substitutedExpr.AsSpan());
            var value = evaluator.EvaluateToValue(
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                _buffer?.Prefixes ?? _materializedPrefixes, _source);

            value.BindTo(ref _bindingTable, aliasName); // the term, or nothing for an error (ADR-056 D6)
        }
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
            _isVariable[i] = new SparqlParser(source.AsSpan()).IsOneVariable(cond.ExpressionStart, cond.ExpressionLength);
            anyExpression |= !_isVariable[i];
        }
        if (anyExpression)
            _values = new Dictionary<MaterializedRow, string[]>(ReferenceEqualityComparer.Instance);
    }

    /// <summary>Whether ORDER BY condition <paramref name="index"/> is one variable, read from the row.</summary>
    internal bool IsVariableCondition(int index) => _isVariable[index];

    /// <summary>
    /// The values of <paramref name="row"/>'s expression conditions, computed by the caller: a grouped solution's are
    /// evaluated with its group's aggregates, which the row does not hold.
    /// </summary>
    internal void SetExpressionValues(MaterializedRow row, string[] values)
    {
        if (_values != null)
            _values[row] = values;
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
    internal ReadOnlySpan<char> Evaluate(ReadOnlySpan<char> expression, scoped ref BindingTable bindings)
    {
        var evaluator = new FilterEvaluator(expression);
        var value = evaluator.EvaluateToValue(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer(),
            _prefixes, _source.AsSpan());
        return value.Type == ValueType.Unbound ? ReadOnlySpan<char>.Empty : value.Term();
    }

    /// <summary>
    /// The ORDER BY order of two terms (§15.1): unbound, then blank nodes, then IRIs, then literals. Literals are
    /// grouped by kind (numbers, booleans, strings, tagged strings, then other datatypes by datatype IRI), so the order
    /// is total; within a group they are ordered by the evaluator's <c>&lt;</c> (the operator mapping, ADR-056), and
    /// two that it doesn't order (equal values such as 1 and 01) by lexical form.
    /// </summary>
    internal static int CompareValues(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var aType = GetTermType(a);
        var bType = GetTermType(b);
        if (aType != bType)
            return aType.CompareTo(bType);
        // An IRI is ordered by its text, without the brackets: with them, the '>' that ends <…/s> sorts after the '1'
        // of <…/s1>
        if (aType == TermSortType.Iri)
            return a[1..^1].SequenceCompareTo(b[1..^1]);
        if (aType != TermSortType.Literal)
            return a.SequenceCompareTo(b);

        var x = Value.FromTerm(a);
        var y = Value.FromTerm(b);
        int group = LiteralGroup(x).CompareTo(LiteralGroup(y));
        if (group != 0)
            return group;
        if (x.Type == ValueType.Typed)
        {
            int datatype = x.Datatype.SequenceCompareTo(y.Datatype);
            if (datatype != 0)
                return datatype;
        }
        if (Value.Order(x, ComparisonOperator.Less, y) == Truth.True)
            return -1;
        if (Value.Order(x, ComparisonOperator.Greater, y) == Truth.True)
            return 1;
        return x.StringValue.SequenceCompareTo(y.StringValue);
    }

    /// <summary>The group a literal sorts in: numbers, booleans, strings, tagged strings, other datatypes.</summary>
    private static int LiteralGroup(scoped in Value v) => v.Type switch
    {
        ValueType.Integer or ValueType.Decimal or ValueType.Float or ValueType.Double => 0,
        ValueType.Boolean => 1,
        ValueType.String => 2,
        ValueType.LangString => 3,
        _ => 4,
    };

    private enum TermSortType { Unbound = 0, BlankNode = 1, Iri = 2, Literal = 3 }

    private static TermSortType GetTermType(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty) return TermSortType.Unbound;
        if (term.Length > 0 && term[0] == '<') return TermSortType.Iri;
        if (term.Length > 1 && term[0] == '_' && term[1] == ':') return TermSortType.BlankNode;
        return TermSortType.Literal; // Includes quoted strings and plain literals
    }
}
