using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref partial struct QueryResults
{
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
        _defaultGraphUnionScan.Dispose();
    }
}
