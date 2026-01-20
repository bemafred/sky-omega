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
        // unbound variables become wildcards, prefixed names are expanded
        var subject = ResolveSlotTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, SlotTermPosition.Subject);
        var predicate = ResolveSlotTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, SlotTermPosition.Predicate);
        var obj = ResolveSlotTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, SlotTermPosition.Object);

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

    /// <summary>
    /// Term position for prefix expansion.
    /// </summary>
    private enum SlotTermPosition { Subject, Predicate, Object }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveSlotTerm(TermType termType, int start, int length, SlotTermPosition position = SlotTermPosition.Subject)
    {
        if (termType != TermType.Variable)
        {
            // Constant - check if it's a prefixed name that needs expansion
            var termSpan = _source.Slice(start, length);

            // Check for prefixed name: not starting with < or ", contains :
            if (_buffer?.Prefixes != null && termSpan.Length > 0 &&
                termSpan[0] != '<' && termSpan[0] != '"')
            {
                var colonIdx = termSpan.IndexOf(':');
                if (colonIdx >= 0)
                {
                    var prefix = termSpan.Slice(0, colonIdx + 1);
                    var localName = termSpan.Slice(colonIdx + 1);

                    foreach (var mapping in _buffer.Prefixes)
                    {
                        var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);

                        if (prefix.SequenceEqual(mappedPrefix))
                        {
                            var iriNs = _source.Slice(mapping.IriStart, mapping.IriLength);
                            var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);

                            // Build expanded IRI and store as string
                            var expanded = string.Concat(nsWithoutClose, localName, ">");

                            // Store in position-specific field and return span over it
                            switch (position)
                            {
                                case SlotTermPosition.Subject:
                                    _expandedSubject = expanded;
                                    return _expandedSubject.AsSpan();
                                case SlotTermPosition.Predicate:
                                    _expandedPredicate = expanded;
                                    return _expandedPredicate.AsSpan();
                                default:
                                    _expandedObject = expanded;
                                    return _expandedObject.AsSpan();
                            }
                        }
                    }
                }
            }

            return termSpan;
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
    /// Compute a hash of bindings for DISTINCT checking.
    /// For SELECT *, hashes all bindings.
    /// For SELECT ?x ?y, hashes only the projected variables.
    /// Uses FNV-1a hash combined across binding values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComputeBindingsHash()
    {
        unchecked
        {
            int hash = (int)2166136261; // FNV offset basis

            // If SELECT * or no explicit variables, hash all bindings
            if (_selectClause.SelectAll || _selectClause.ProjectedVariableCount == 0)
            {
                for (int i = 0; i < _bindingTable.Count; i++)
                {
                    var value = _bindingTable.GetString(i);
                    foreach (var ch in value)
                    {
                        hash = (hash ^ ch) * 16777619; // FNV prime
                    }
                    hash = (hash ^ '|') * 16777619; // Separator between bindings
                }
            }
            else
            {
                // Only hash the projected variables for DISTINCT
                for (int i = 0; i < _selectClause.ProjectedVariableCount; i++)
                {
                    var (start, length) = _selectClause.GetProjectedVariable(i);
                    if (length > 0)
                    {
                        var varName = _source.Slice(start, length);
                        var bindingIdx = _bindingTable.FindBinding(varName);
                        if (bindingIdx >= 0)
                        {
                            var value = _bindingTable.GetString(bindingIdx);
                            foreach (var ch in value)
                            {
                                hash = (hash ^ ch) * 16777619; // FNV prime
                            }
                        }
                        hash = (hash ^ '|') * 16777619; // Separator between bindings
                    }
                }
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

            // Resolve terms - use bound values for variables, expand prefixed names
            var subject = ResolveSlotTerm(patternSlot.SubjectType, patternSlot.SubjectStart, patternSlot.SubjectLength, SlotTermPosition.Subject);
            var predicate = ResolveSlotTerm(patternSlot.PredicateType, patternSlot.PredicateStart, patternSlot.PredicateLength, SlotTermPosition.Predicate);
            var obj = ResolveSlotTerm(patternSlot.ObjectType, patternSlot.ObjectStart, patternSlot.ObjectLength, SlotTermPosition.Object);

            // Query the store - use graph context if inside a GRAPH clause
            var results = _graphContext != null
                ? _store.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
                : _store.QueryCurrent(subject, predicate, obj);
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

            // Skip BINDs that were evaluated inline by MultiPatternScan.
            // BINDs with AfterPatternIndex >= 0 are evaluated after the specified pattern
            // during the scan's MoveNext() loop, so they shouldn't be re-evaluated here.
            // Only skip when actually using MultiPatternScan (_isMultiPattern is true).
            // For single-pattern queries using TriplePatternScan, BINDs must be evaluated here.
            if (_isMultiPattern && slot.BindAfterPatternIndex >= 0)
                continue;

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
    /// Evaluate non-aggregate SELECT expressions (e.g., (HOURS(?date) AS ?x)).
    /// These are stored with AggregateFunction.None and should be evaluated per row.
    /// </summary>
    private void EvaluateSelectExpressions()
    {
        if (_selectClause.AggregateCount == 0) return;

        for (int i = 0; i < _selectClause.AggregateCount; i++)
        {
            var agg = _selectClause.GetAggregate(i);

            // Only evaluate non-aggregate expressions (Function == None)
            if (agg.Function != AggregateFunction.None) continue;

            // Skip if no expression to evaluate
            if (agg.VariableLength == 0) continue;

            // Get expression and alias
            var expr = _source.Slice(agg.VariableStart, agg.VariableLength);
            var aliasName = _source.Slice(agg.AliasStart, agg.AliasLength);

            // Evaluate the expression using BindExpressionEvaluator
            var evaluator = new BindExpressionEvaluator(expr,
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer());
            var value = evaluator.Evaluate();

            // Bind the result to the alias variable
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

        // Resolve terms using current bindings, expand prefixed names
        var subject = ResolveSlotTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, SlotTermPosition.Subject);
        var predicate = ResolveSlotTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, SlotTermPosition.Predicate);
        var obj = ResolveSlotTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, SlotTermPosition.Object);

        // Query the store to see if this pattern matches - use graph context if inside a GRAPH clause
        var results = _graphContext != null
            ? _store.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
            : _store.QueryCurrent(subject, predicate, obj);
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
                // Expand prefixed names to full URIs for comparison
                var expandedValue = ExpandPrefixedName(valuesValue);
                if (CompareValuesMatch(boundValue, expandedValue))
                    return true;
            }

            // Bound value doesn't match any VALUES value
            return false;
        }

        return true;
    }

    /// <summary>
    /// Check if the current bindings match the post-query VALUES constraint.
    /// For multi-variable VALUES, all variables in a row must match (UNDEF matches anything).
    /// Post-query VALUES appears after the WHERE clause (not inline in patterns).
    /// </summary>
    private bool MatchesPostQueryValuesConstraint()
    {
        if (_buffer == null || !_buffer.HasPostQueryValues) return true;

        var postValues = _buffer.PostQueryValues;
        if (!postValues.HasValues) return true;

        int varCount = postValues.VariableCount;
        if (varCount == 0) return true;

        int rowCount = postValues.RowCount;
        if (rowCount == 0) return true;

        // For each row, check if ALL variables match their bound values
        // A row matches if every variable either:
        // 1. Has UNDEF (matches any bound value or unbound)
        // 2. Has a value that matches the bound value
        for (int row = 0; row < rowCount; row++)
        {
            bool rowMatches = true;

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
                    // Variable not bound - doesn't match this non-UNDEF value
                    rowMatches = false;
                    break;
                }

                var boundValue = _bindingTable.GetString(bindingIdx);
                var valuesValue = _source.Slice(valStart, valLength);

                // Expand prefixed names to full IRIs for comparison
                var expandedValue = ExpandPrefixedName(valuesValue);

                // Handle string literal comparison (values03 uses quoted strings)
                if (!CompareValuesMatch(boundValue, expandedValue))
                {
                    rowMatches = false;
                    break;
                }
            }

            if (rowMatches)
                return true;
        }

        // No row matched
        return false;
    }

    /// <summary>
    /// Compare a bound value with a VALUES value, handling quoted strings.
    /// </summary>
    private static bool CompareValuesMatch(ReadOnlySpan<char> boundValue, ReadOnlySpan<char> valuesValue)
    {
        // Direct match
        if (boundValue.SequenceEqual(valuesValue))
            return true;

        // Handle quoted string literals - strip quotes for comparison
        if (valuesValue.Length >= 2 && valuesValue[0] == '"')
        {
            // Find end of string value (before language tag or datatype)
            int endQuote = valuesValue.LastIndexOf('"');
            if (endQuote > 0)
            {
                var unquotedValue = valuesValue.Slice(1, endQuote - 1);

                // Also strip quotes from bound value if present
                if (boundValue.Length >= 2 && boundValue[0] == '"')
                {
                    int boundEndQuote = boundValue.LastIndexOf('"');
                    if (boundEndQuote > 0)
                    {
                        var unquotedBound = boundValue.Slice(1, boundEndQuote - 1);
                        if (unquotedBound.SequenceEqual(unquotedValue))
                            return true;
                    }
                }
                else
                {
                    // Bound value might be unquoted already
                    if (boundValue.SequenceEqual(unquotedValue))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Expand a prefixed name to its full IRI using the buffer's prefix mappings.
    /// Returns the original span if not a prefixed name or no matching prefix found.
    /// </summary>
    private ReadOnlySpan<char> ExpandPrefixedName(ReadOnlySpan<char> term)
    {
        // Skip if already a full IRI, literal, or blank node
        if (term.Length == 0 || term[0] == '<' || term[0] == '"' || term[0] == '_')
            return term;

        // Look for colon indicating prefixed name
        var colonIdx = term.IndexOf(':');
        if (colonIdx < 0 || _buffer?.Prefixes == null)
            return term;

        // Include the colon in the prefix (stored prefixes include trailing colon, e.g., "ex:")
        var prefixWithColon = term.Slice(0, colonIdx + 1);
        var localPart = term.Slice(colonIdx + 1);

        // Find matching prefix in buffer
        foreach (var mapping in _buffer.Prefixes)
        {
            var mappingPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);
            if (prefixWithColon.SequenceEqual(mappingPrefix))
            {
                // Found matching prefix, expand to full IRI
                // The IRI is stored with angle brackets, e.g., "<http://example.org/>"
                var iriBase = _source.Slice(mapping.IriStart, mapping.IriLength);

                // Strip angle brackets from IRI base if present, then build full IRI
                var iriContent = iriBase;
                if (iriContent.Length >= 2 && iriContent[0] == '<' && iriContent[^1] == '>')
                    iriContent = iriContent.Slice(1, iriContent.Length - 2);

                // Build full IRI: <base + localPart>
                // Store in _expandedSubject (reusing storage field)
                _expandedSubject = $"<{iriContent.ToString()}{localPart.ToString()}>";
                return _expandedSubject.AsSpan();
            }
        }

        return term;
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
