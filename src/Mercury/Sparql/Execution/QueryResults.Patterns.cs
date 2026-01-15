using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref partial struct QueryResults
{
    private void TryMatchOptionalPatterns()
    {
        if (_store == null || _buffer == null) return;

        var patterns = _buffer.GetPatterns();
        var optionalFlags = _buffer.OptionalFlags;

        // Iterate through patterns checking optional flag
        for (int i = 0; i < _buffer.PatternCount && i < 32; i++)
        {
            if ((optionalFlags & (1u << i)) == 0) continue;

            var slot = patterns[i];
            if (slot.Kind != PatternKind.Triple) continue;

            TryMatchSingleOptionalPatternFromSlot(slot);
        }
    }

    /// <summary>
    /// Try to match a single optional pattern from a PatternSlot and bind its variables.
    /// </summary>
    private void TryMatchSingleOptionalPatternFromSlot(PatternSlot slot)
    {
        if (_store == null) return;

        // Resolve terms - variables that are already bound use their value,
        // unbound variables become wildcards
        var subject = ResolveSlotTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength);
        var predicate = ResolveSlotTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength);
        var obj = ResolveSlotTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength);

        // Query the store
        var results = _store.QueryCurrent(subject, predicate, obj);
        try
        {
            if (results.MoveNext())
            {
                var triple = results.Current;

                // Bind any unbound variables from the result
                TryBindSlotVariable(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, triple.Subject);
                TryBindSlotVariable(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, triple.Predicate);
                TryBindSlotVariable(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, triple.Object);
            }
            // If no match, we just don't add bindings (left outer join semantics)
        }
        finally
        {
            results.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveSlotTerm(TermType termType, int start, int length)
    {
        if (termType != TermType.Variable)
        {
            // Constant - use source text
            return _source.Slice(start, length);
        }

        // Check if variable is already bound
        var varName = _source.Slice(start, length);
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
    private void TryBindSlotVariable(TermType termType, int start, int length, ReadOnlySpan<char> value)
    {
        if (termType != TermType.Variable) return;

        var varName = _source.Slice(start, length);

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
        if (_store == null || _buffer == null) return false;

        // Count only triple patterns in union branch (not BINDs, FILTERs, etc.)
        var unionTripleCount = _buffer.UnionBranchTripleCount;

        // Handle BIND-only UNION branches (no triple patterns in union, just BINDs)
        if (unionTripleCount == 0)
        {
            if (!_hasUnionBindsOnly) return false;

            // Re-create scan for first branch patterns so we can iterate again
            // The second branch BINDs will be evaluated via _unionBranchActive flag
            return InitializeFirstBranchScanForUnion();
        }

        // Clear bindings from first branch before starting union branch
        _bindingTable.Clear();

        var patterns = _buffer.GetPatterns();
        var unionStart = _buffer.UnionStartIndex;

        if (unionTripleCount == 1)
        {
            // Single union pattern - use simple scan
            // Find the first Triple pattern after union start
            for (int i = unionStart; i < _buffer.PatternCount; i++)
            {
                if (patterns[i].Kind == PatternKind.Triple)
                {
                    var slot = patterns[i];
                    var tp = SlotToTriplePattern(slot);
                    _unionSingleScan = new TriplePatternScan(_store, _source, tp, _bindingTable);
                    _unionIsMultiPattern = false;
                    return true;
                }
            }
            return false;
        }
        else
        {
            // Multiple union patterns - use multi-pattern scan with union mode
            // Create a temporary GraphPattern from union patterns
            var unionPattern = new GraphPattern();
            for (int i = unionStart; i < _buffer.PatternCount; i++)
            {
                if (patterns[i].Kind == PatternKind.Triple)
                {
                    unionPattern.AddPattern(SlotToTriplePattern(patterns[i]));
                }
            }
            _unionMultiScan = new MultiPatternScan(_store, _source, unionPattern, unionMode: false, default, _buffer?.Prefixes);
            _unionIsMultiPattern = true;
            return true;
        }
    }

    /// <summary>
    /// Initialize scan for first branch patterns to re-iterate for BIND-only UNION.
    /// Used when the UNION branch contains only BIND expressions (no triple patterns).
    /// </summary>
    private bool InitializeFirstBranchScanForUnion()
    {
        if (_store == null || _buffer == null) return false;

        // Clear bindings from first branch before re-scanning
        _bindingTable.Clear();

        var patterns = _buffer.GetPatterns();
        var unionStart = _buffer.UnionStartIndex;

        // Count triple patterns before union start (first branch)
        int firstBranchPatternCount = 0;
        for (int i = 0; i < unionStart; i++)
        {
            if (patterns[i].Kind == PatternKind.Triple)
                firstBranchPatternCount++;
        }

        if (firstBranchPatternCount == 0)
        {
            // No patterns in first branch either - this shouldn't happen for valid UNION
            return false;
        }

        if (firstBranchPatternCount == 1)
        {
            // Single pattern - use simple scan
            for (int i = 0; i < unionStart; i++)
            {
                if (patterns[i].Kind == PatternKind.Triple)
                {
                    var slot = patterns[i];
                    var tp = SlotToTriplePattern(slot);
                    _unionSingleScan = new TriplePatternScan(_store, _source, tp, _bindingTable);
                    _unionIsMultiPattern = false;
                    return true;
                }
            }
            return false;
        }
        else
        {
            // Multiple patterns - use multi-pattern scan
            var firstBranchPattern = new GraphPattern();
            for (int i = 0; i < unionStart; i++)
            {
                if (patterns[i].Kind == PatternKind.Triple)
                {
                    firstBranchPattern.AddPattern(SlotToTriplePattern(patterns[i]));
                }
            }
            _unionMultiScan = new MultiPatternScan(_store, _source, firstBranchPattern, unionMode: false, default, _buffer?.Prefixes);
            _unionIsMultiPattern = true;
            return true;
        }
    }

    /// <summary>
    /// Convert a PatternSlot to a TriplePattern for use with existing scan operators.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TriplePattern SlotToTriplePattern(PatternSlot slot)
    {
        return new TriplePattern
        {
            Subject = new Term
            {
                Type = slot.SubjectType,
                Start = slot.SubjectStart,
                Length = slot.SubjectLength
            },
            Predicate = new Term
            {
                Type = slot.PredicateType,
                Start = slot.PredicateStart,
                Length = slot.PredicateLength
            },
            Object = new Term
            {
                Type = slot.ObjectType,
                Start = slot.ObjectStart,
                Length = slot.ObjectLength
            }
        };
    }

    private bool EvaluateFilters()
    {
        if (_buffer == null) return true;

        var patterns = _buffer.GetPatterns();
        int filtersSeen = 0;

        for (int i = 0; i < _buffer.PatternCount && filtersSeen < _buffer.FilterCount; i++)
        {
            if (patterns[i].Kind != PatternKind.Filter) continue;
            filtersSeen++;

            var slot = patterns[i];
            var filterExpr = _source.Slice(slot.FilterStart, slot.FilterLength);

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
        if (_store == null || _buffer == null) return true;

        var patterns = _buffer.GetPatterns();
        int existsSeen = 0;

        for (int i = 0; i < _buffer.PatternCount && existsSeen < _buffer.ExistsFilterCount; i++)
        {
            var kind = patterns[i].Kind;
            if (kind != PatternKind.ExistsHeader && kind != PatternKind.NotExistsHeader) continue;
            existsSeen++;

            var slot = patterns[i];
            var matches = EvaluateExistsPatternFromSlot(slot, patterns);

            // EXISTS: must match at least once
            // NOT EXISTS: must not match at all
            if (slot.IsNegatedExists)
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
    private bool EvaluateExistsPatternFromSlot(PatternSlot existsSlot, PatternArray patterns)
    {
        if (_store == null || existsSlot.ExistsChildCount == 0)
            return false;

        // For each pattern in EXISTS block, substitute bound variables and query the store
        // All patterns must match for EXISTS to succeed (conjunction)
        int existsStart = existsSlot.ExistsChildStart;
        int existsEnd = existsStart + existsSlot.ExistsChildCount;

        for (int p = existsStart; p < existsEnd; p++)
        {
            var patternSlot = patterns[p];
            if (patternSlot.Kind != PatternKind.Triple) continue;

            // Resolve terms - use bound values for variables
            var subject = ResolveSlotTerm(patternSlot.SubjectType, patternSlot.SubjectStart, patternSlot.SubjectLength);
            var predicate = ResolveSlotTerm(patternSlot.PredicateType, patternSlot.PredicateStart, patternSlot.PredicateLength);
            var obj = ResolveSlotTerm(patternSlot.ObjectType, patternSlot.ObjectStart, patternSlot.ObjectLength);

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
    /// Evaluate BIND expressions and add bindings to the binding table.
    /// When UNION has BIND-only branches, only evaluates BINDs for the current branch.
    /// </summary>
    private void EvaluateBindExpressions()
    {
        if (_buffer == null) return;

        var patterns = _buffer.GetPatterns();
        int bindsSeen = 0;

        // Determine which BINDs to evaluate based on current branch
        // For BIND-only UNION branches:
        //   - First branch (not active): evaluate BINDs 0 to _firstBranchBindCount-1
        //   - Second branch (active): evaluate BINDs _firstBranchBindCount to BindCount-1
        int bindStart = 0;
        int bindEnd = _buffer.BindCount;

        if (_hasUnionBindsOnly)
        {
            if (_unionBranchActive)
            {
                bindStart = _firstBranchBindCount;
            }
            else
            {
                bindEnd = _firstBranchBindCount;
            }
        }

        for (int i = 0; i < _buffer.PatternCount && bindsSeen < _buffer.BindCount; i++)
        {
            if (patterns[i].Kind != PatternKind.Bind) continue;

            // Check if this BIND should be evaluated in the current branch
            if (bindsSeen < bindStart || bindsSeen >= bindEnd)
            {
                bindsSeen++;
                continue;
            }
            bindsSeen++;

            var slot = patterns[i];
            var expr = _source.Slice(slot.BindExprStart, slot.BindExprLength);
            var varName = _source.Slice(slot.BindVarStart, slot.BindVarLength);

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
        if (_store == null || _buffer == null) return false;

        var patterns = _buffer.GetPatterns();

        // For MINUS semantics: exclude if ALL MINUS patterns match
        bool allMatch = true;
        int minusFound = 0;

        for (int i = 0; i < _buffer.PatternCount; i++)
        {
            if (patterns[i].Kind != PatternKind.MinusTriple) continue;
            minusFound++;

            var slot = patterns[i];
            if (!MatchesSingleMinusPatternFromSlot(slot))
            {
                allMatch = false;
                break;
            }
        }

        // All MINUS patterns matched - exclude this solution
        return allMatch && minusFound > 0;
    }

    /// <summary>
    /// Check if a single MINUS pattern matches the current bindings.
    /// </summary>
    private bool MatchesSingleMinusPatternFromSlot(PatternSlot slot)
    {
        if (_store == null) return false;

        // Resolve terms using current bindings
        var subject = ResolveSlotTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength);
        var predicate = ResolveSlotTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength);
        var obj = ResolveSlotTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength);

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
    /// Check if the current bindings match the VALUES constraint.
    /// The VALUES variable must be bound to one of the VALUES values.
    /// </summary>
    private bool MatchesValuesConstraint()
    {
        if (_buffer == null || !_buffer.HasValues) return true;

        var patterns = _buffer.GetPatterns();

        // Find VALUES header
        for (int i = 0; i < _buffer.PatternCount; i++)
        {
            if (patterns[i].Kind != PatternKind.ValuesHeader) continue;

            var valuesSlot = patterns[i];

            // Get the variable name from VALUES
            var varName = _source.Slice(valuesSlot.ValuesVarStart, valuesSlot.ValuesVarLength);

            // Find the binding for this variable
            var bindingIdx = _bindingTable.FindBinding(varName);
            if (bindingIdx < 0)
            {
                // Variable not bound - allow (VALUES would bind it in a more complete impl)
                return true;
            }

            // Get the bound value
            var boundValue = _bindingTable.GetString(bindingIdx);

            // Check if it matches any VALUES entry
            int entryCount = valuesSlot.ValuesEntryCount;
            for (int e = i + 1; e <= i + entryCount && e < _buffer.PatternCount; e++)
            {
                var entrySlot = patterns[e];
                if (entrySlot.Kind != PatternKind.ValuesEntry) continue;

                var valuesValue = _source.Slice(entrySlot.ValuesEntryStart, entrySlot.ValuesEntryLength);
                if (boundValue.SequenceEqual(valuesValue))
                    return true;
            }

            // Bound value doesn't match any VALUES value
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        _singleScan.Dispose();
        _multiScan.Dispose();
        _unionSingleScan.Dispose();
        _unionMultiScan.Dispose();
        _subQueryScan.Dispose();
        _defaultGraphUnionScan.Dispose();
        _crossGraphScan.Dispose();
    }
}
