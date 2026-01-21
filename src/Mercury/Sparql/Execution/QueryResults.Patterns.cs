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
    private enum SlotTermPosition { Subject, Predicate, Object, Graph }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveSlotTerm(TermType termType, int start, int length, SlotTermPosition position = SlotTermPosition.Subject)
    {
        if (termType != TermType.Variable)
        {
            // Constant - check if it's a prefixed name that needs expansion
            var termSpan = _source.Slice(start, length);

            // Handle 'a' shorthand for rdf:type (SPARQL keyword)
            if (termSpan.Length == 1 && termSpan[0] == 'a')
            {
                return SyntheticTermHelper.RdfType.AsSpan();
            }

            // Handle numeric literals (integers and decimals)
            // Query literal like "9" needs to match stored "9"^^<xsd:integer>
            if (termSpan.Length > 0 && IsNumericLiteral(termSpan))
            {
                var expanded = ExpandNumericLiteral(termSpan);
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
    /// Check if a term is a numeric literal (integer or decimal).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNumericLiteral(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty) return false;

        var first = term[0];
        // Numeric literals start with digit, +, or -
        // But not if it looks like a prefixed name (contains :) or IRI (<)
        if (first == '+' || first == '-')
        {
            return term.Length > 1 && char.IsDigit(term[1]);
        }
        return char.IsDigit(first);
    }

    /// <summary>
    /// Expand a numeric literal to its typed RDF form.
    /// Integer: "9" -> "\"9\"^^&lt;http://www.w3.org/2001/XMLSchema#integer&gt;"
    /// Decimal: "9.5" -> "\"9.5\"^^&lt;http://www.w3.org/2001/XMLSchema#decimal&gt;"
    /// </summary>
    private static string ExpandNumericLiteral(ReadOnlySpan<char> term)
    {
        // Check if it's a decimal (contains .)
        var hasDecimal = term.Contains('.') || term.Contains('e') || term.Contains('E');

        if (hasDecimal)
        {
            return string.Concat("\"", term, "\"^^<http://www.w3.org/2001/XMLSchema#decimal>");
        }
        else
        {
            return string.Concat("\"", term, "\"^^<http://www.w3.org/2001/XMLSchema#integer>");
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

        // Track current graph context for GRAPH patterns inside EXISTS
        string? existsGraphContext = _graphContext;

        for (int p = existsStart; p < existsEnd; p++)
        {
            var patternSlot = patterns[p];

            // Handle GRAPH patterns inside EXISTS - resolve graph variable from outer bindings
            if (patternSlot.Kind == PatternKind.GraphHeader)
            {
                // Resolve the graph term - could be a variable bound in outer query
                var graphTerm = ResolveSlotTerm(patternSlot.GraphTermType, patternSlot.GraphTermStart, patternSlot.GraphTermLength, SlotTermPosition.Graph);

                // Set graph context for child patterns
                // Empty string (<>) means default graph - use null context
                existsGraphContext = graphTerm.Length == 0 ? null : graphTerm.ToString();

                // Now evaluate child patterns of this GRAPH clause
                int graphChildStart = patternSlot.ChildStartIndex;
                int graphChildEnd = graphChildStart + patternSlot.ChildCount;

                for (int gc = graphChildStart; gc < graphChildEnd; gc++)
                {
                    var childSlot = patterns[gc];
                    if (childSlot.Kind != PatternKind.Triple) continue;

                    var subject = ResolveSlotTerm(childSlot.SubjectType, childSlot.SubjectStart, childSlot.SubjectLength, SlotTermPosition.Subject);
                    var predicate = ResolveSlotTerm(childSlot.PredicateType, childSlot.PredicateStart, childSlot.PredicateLength, SlotTermPosition.Predicate);
                    var obj = ResolveSlotTerm(childSlot.ObjectType, childSlot.ObjectStart, childSlot.ObjectLength, SlotTermPosition.Object);

                    var results = existsGraphContext != null
                        ? _store.QueryCurrent(subject, predicate, obj, existsGraphContext.AsSpan())
                        : _store.QueryCurrent(subject, predicate, obj);
                    try
                    {
                        if (!results.MoveNext())
                            return false; // No match for this pattern in the specified graph
                    }
                    finally
                    {
                        results.Dispose();
                    }
                }

                // Skip past the graph children in the main loop (they're already processed)
                p = graphChildEnd - 1;
                continue;
            }

            if (patternSlot.Kind != PatternKind.Triple) continue;

            // Resolve terms - use bound values for variables, expand prefixed names
            var subject2 = ResolveSlotTerm(patternSlot.SubjectType, patternSlot.SubjectStart, patternSlot.SubjectLength, SlotTermPosition.Subject);
            var predicate2 = ResolveSlotTerm(patternSlot.PredicateType, patternSlot.PredicateStart, patternSlot.PredicateLength, SlotTermPosition.Predicate);
            var obj2 = ResolveSlotTerm(patternSlot.ObjectType, patternSlot.ObjectStart, patternSlot.ObjectLength, SlotTermPosition.Object);

            // Query the store - use graph context if inside a GRAPH clause
            var results2 = existsGraphContext != null
                ? _store.QueryCurrent(subject2, predicate2, obj2, existsGraphContext.AsSpan())
                : _store.QueryCurrent(subject2, predicate2, obj2);
            try
            {
                if (!results2.MoveNext())
                    return false; // No match for this pattern
            }
            finally
            {
                results2.Dispose();
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

        // If MINUS has OPTIONAL patterns, use the group evaluation logic
        if (_buffer.HasMinusOptionalPatterns)
        {
            return MatchesMinusPatternWithOptional();
        }

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
    /// Check MINUS patterns when there are OPTIONAL patterns inside MINUS.
    /// Executes MINUS as a group pattern with left-outer-join semantics for OPTIONAL.
    /// Returns true if any MINUS solution has domain overlap with matching values.
    /// </summary>
    private bool MatchesMinusPatternWithOptional()
    {
        if (_store == null || _buffer == null) return false;

        var patterns = _buffer.GetPatterns();

        // Collect MINUS pattern slots (both required and optional)
        Span<int> minusSlotIndices = stackalloc int[8];
        int minusSlotCount = 0;

        for (int i = 0; i < _buffer.PatternCount && minusSlotCount < 8; i++)
        {
            if (patterns[i].Kind == PatternKind.MinusTriple)
            {
                minusSlotIndices[minusSlotCount++] = i;
            }
        }

        if (minusSlotCount == 0)
            return false;

        // Separate required and optional patterns
        Span<int> requiredIndices = stackalloc int[8];
        Span<int> optionalIndices = stackalloc int[8];
        int requiredCount = 0;
        int optionalCount = 0;

        for (int i = 0; i < minusSlotCount; i++)
        {
            if (_buffer.IsMinusOptional(i))
                optionalIndices[optionalCount++] = minusSlotIndices[i];
            else
                requiredIndices[requiredCount++] = minusSlotIndices[i];
        }

        // Execute required patterns first to get base MINUS solutions
        // For simplicity, we only support one required pattern (the common case)
        if (requiredCount == 0)
            return false;

        var requiredSlot = patterns[requiredIndices[0]];

        // Query for required pattern matches
        var requiredSubject = ResolveSlotTerm(requiredSlot.SubjectType, requiredSlot.SubjectStart, requiredSlot.SubjectLength, SlotTermPosition.Subject);
        var requiredPredicate = ResolveSlotTerm(requiredSlot.PredicateType, requiredSlot.PredicateStart, requiredSlot.PredicateLength, SlotTermPosition.Predicate);
        var requiredObject = ResolveSlotTerm(requiredSlot.ObjectType, requiredSlot.ObjectStart, requiredSlot.ObjectLength, SlotTermPosition.Object);

        var requiredResults = _graphContext != null
            ? _store.QueryCurrent(requiredSubject, requiredPredicate, requiredObject, _graphContext.AsSpan())
            : _store.QueryCurrent(requiredSubject, requiredPredicate, requiredObject);

        // Use heap-allocated array for MINUS bindings (contains string which is managed)
        var minusBindings = new MinusBinding[16];

        try
        {
            // For each required pattern match, try to extend with optional patterns
            while (requiredResults.MoveNext())
            {
                var requiredTriple = requiredResults.Current;

                // Build MINUS solution from required pattern
                int minusBindingCount = 0;

                // Add bindings from required pattern
                AddMinusBindingsFromTriple(requiredSlot, requiredTriple, minusBindings, ref minusBindingCount);

                // Try each optional pattern to extend the MINUS solution
                for (int i = 0; i < optionalCount; i++)
                {
                    var optSlot = patterns[optionalIndices[i]];
                    TryExtendMinusSolutionWithOptional(optSlot, minusBindings, ref minusBindingCount);
                }

                // Check if this MINUS solution excludes the main solution
                if (MinusSolutionExcludesMain(minusBindings, minusBindingCount))
                {
                    return true; // Found a MINUS solution that excludes main
                }
            }
        }
        finally
        {
            requiredResults.Dispose();
        }

        return false; // No MINUS solution excludes the main solution
    }

    /// <summary>
    /// Binding in a MINUS solution: variable name hash + value.
    /// </summary>
    private struct MinusBinding
    {
        public int VariableHash;
        public int VarStart;
        public int VarLength;
        public string Value; // Heap allocation but only for MINUS checking
    }

    /// <summary>
    /// Add bindings to MINUS solution from a matched triple.
    /// </summary>
    private void AddMinusBindingsFromTriple(PatternSlot slot, in ResolvedTemporalQuad triple,
        MinusBinding[] bindings, ref int count)
    {
        if (slot.SubjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.SubjectStart, slot.SubjectLength);
            bindings[count++] = new MinusBinding
            {
                VariableHash = ComputeVariableHash(varName),
                VarStart = slot.SubjectStart,
                VarLength = slot.SubjectLength,
                Value = triple.Subject.ToString()
            };
        }
        if (slot.PredicateType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.PredicateStart, slot.PredicateLength);
            bindings[count++] = new MinusBinding
            {
                VariableHash = ComputeVariableHash(varName),
                VarStart = slot.PredicateStart,
                VarLength = slot.PredicateLength,
                Value = triple.Predicate.ToString()
            };
        }
        if (slot.ObjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.ObjectStart, slot.ObjectLength);
            bindings[count++] = new MinusBinding
            {
                VariableHash = ComputeVariableHash(varName),
                VarStart = slot.ObjectStart,
                VarLength = slot.ObjectLength,
                Value = triple.Object.ToString()
            };
        }
    }

    /// <summary>
    /// Try to extend the MINUS solution with an OPTIONAL pattern.
    /// If the pattern matches, adds bindings; if not, variables remain unbound.
    /// </summary>
    private void TryExtendMinusSolutionWithOptional(PatternSlot optSlot,
        MinusBinding[] bindings, ref int bindingCount)
    {
        if (_store == null) return;

        // Resolve terms using MINUS bindings so far
        var subject = ResolveMinusSlotTerm(optSlot.SubjectType, optSlot.SubjectStart, optSlot.SubjectLength,
            bindings, bindingCount, SlotTermPosition.Subject);
        var predicate = ResolveMinusSlotTerm(optSlot.PredicateType, optSlot.PredicateStart, optSlot.PredicateLength,
            bindings, bindingCount, SlotTermPosition.Predicate);
        var obj = ResolveMinusSlotTerm(optSlot.ObjectType, optSlot.ObjectStart, optSlot.ObjectLength,
            bindings, bindingCount, SlotTermPosition.Object);

        var results = _graphContext != null
            ? _store.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
            : _store.QueryCurrent(subject, predicate, obj);

        try
        {
            if (results.MoveNext())
            {
                // Pattern matched - add bindings from first match
                var triple = results.Current;
                AddMinusBindingsFromTripleIfNew(optSlot, triple, bindings, ref bindingCount);
            }
            // If no match, variables from this OPTIONAL remain unbound in MINUS solution
        }
        finally
        {
            results.Dispose();
        }
    }

    /// <summary>
    /// Resolve a slot term using MINUS bindings only (not main bindings).
    /// This is critical for OPTIONAL patterns inside MINUS: we must NOT constrain
    /// the OPTIONAL to match main solution values. Instead, OPTIONAL binds freely
    /// and we check domain overlap AFTER getting the complete MINUS solution.
    /// </summary>
    private ReadOnlySpan<char> ResolveMinusSlotTerm(TermType type, int start, int length,
        MinusBinding[] bindings, int bindingCount, SlotTermPosition pos)
    {
        if (type == TermType.Variable)
        {
            var varName = _source.Slice(start, length);
            var varHash = ComputeVariableHash(varName);

            // Check MINUS bindings only - NOT main bindings!
            // Main bindings would incorrectly constrain OPTIONAL patterns to match main values
            for (int i = 0; i < bindingCount; i++)
            {
                if (bindings[i].VariableHash == varHash)
                {
                    return bindings[i].Value.AsSpan();
                }
            }

            // Unbound - return empty for wildcard
            return ReadOnlySpan<char>.Empty;
        }

        // For non-variables, use normal resolution
        return ResolveSlotTerm(type, start, length, pos);
    }

    /// <summary>
    /// Add bindings from triple only if not already bound.
    /// </summary>
    private void AddMinusBindingsFromTripleIfNew(PatternSlot slot, in ResolvedTemporalQuad triple,
        MinusBinding[] bindings, ref int count)
    {
        if (slot.SubjectType == TermType.Variable)
        {
            var varName = _source.Slice(slot.SubjectStart, slot.SubjectLength);
            var varHash = ComputeVariableHash(varName);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (bindings[i].VariableHash == varHash) { found = true; break; }
            }
            if (!found && count < bindings.Length)
            {
                bindings[count++] = new MinusBinding
                {
                    VariableHash = varHash,
                    VarStart = slot.SubjectStart,
                    VarLength = slot.SubjectLength,
                    Value = triple.Subject.ToString()
                };
            }
        }
        if (slot.PredicateType == TermType.Variable)
        {
            var varName = _source.Slice(slot.PredicateStart, slot.PredicateLength);
            var varHash = ComputeVariableHash(varName);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (bindings[i].VariableHash == varHash) { found = true; break; }
            }
            if (!found && count < bindings.Length)
            {
                bindings[count++] = new MinusBinding
                {
                    VariableHash = varHash,
                    VarStart = slot.PredicateStart,
                    VarLength = slot.PredicateLength,
                    Value = triple.Predicate.ToString()
                };
            }
        }
        if (slot.ObjectType == TermType.Variable)
        {
            var varName = _source.Slice(slot.ObjectStart, slot.ObjectLength);
            var varHash = ComputeVariableHash(varName);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (bindings[i].VariableHash == varHash) { found = true; break; }
            }
            if (!found && count < bindings.Length)
            {
                bindings[count++] = new MinusBinding
                {
                    VariableHash = varHash,
                    VarStart = slot.ObjectStart,
                    VarLength = slot.ObjectLength,
                    Value = triple.Object.ToString()
                };
            }
        }
    }

    /// <summary>
    /// Check if a MINUS solution excludes the main solution.
    /// Exclusion happens when:
    /// 1. There is domain overlap (shared variables that are BOUND in BOTH solutions)
    /// 2. All overlapping variables have the same values
    /// </summary>
    private bool MinusSolutionExcludesMain(MinusBinding[] minusBindings, int bindingCount)
    {
        bool hasOverlap = false;

        for (int i = 0; i < bindingCount; i++)
        {
            var varName = _source.Slice(minusBindings[i].VarStart, minusBindings[i].VarLength);

            // Check if this variable is bound in the main solution
            var mainIdx = _bindingTable.FindBinding(varName);
            if (mainIdx >= 0)
            {
                // Both bound - check if values match
                hasOverlap = true;
                var mainValue = _bindingTable.GetString(mainIdx);
                var minusValue = minusBindings[i].Value.AsSpan();

                if (!mainValue.SequenceEqual(minusValue))
                {
                    // Values differ - no exclusion from this MINUS solution
                    return false;
                }
            }
            // If variable is only in MINUS (not in main), it doesn't affect domain overlap
        }

        // Exclude if there was overlap and all overlapping values matched
        return hasOverlap;
    }

    /// <summary>
    /// Check if a single MINUS pattern matches the current bindings.
    /// SPARQL MINUS semantics: exclude only if domains overlap AND pattern matches.
    /// If there are no shared variables between current bindings and MINUS pattern,
    /// the solution is NOT excluded (domain disjointness rule).
    /// If MINUS has a FILTER, only exclude if both pattern matches AND filter evaluates to true.
    /// </summary>
    private bool MatchesSingleMinusPatternFromSlot(PatternSlot slot)
    {
        if (_store == null || _buffer == null) return false;

        // First check for domain disjointness - if no shared variables, don't exclude
        // Per SPARQL spec: dom(μ) ∩ dom(μ') = ∅ means solution is kept
        bool hasSharedVariable = false;

        // Track which variables in MINUS pattern are unbound (need their values from matches)
        bool subjectIsUnbound = false;
        bool predicateIsUnbound = false;
        bool objectIsUnbound = false;
        int subjectVarStart = 0, subjectVarLen = 0;
        int predicateVarStart = 0, predicateVarLen = 0;
        int objectVarStart = 0, objectVarLen = 0;

        if (slot.SubjectType == TermType.Variable)
        {
            var varName = _source.Slice(slot.SubjectStart, slot.SubjectLength);
            if (_bindingTable.FindBinding(varName) >= 0)
                hasSharedVariable = true;
            else
            {
                subjectIsUnbound = true;
                subjectVarStart = slot.SubjectStart;
                subjectVarLen = slot.SubjectLength;
            }
        }
        if (slot.PredicateType == TermType.Variable)
        {
            var varName = _source.Slice(slot.PredicateStart, slot.PredicateLength);
            if (_bindingTable.FindBinding(varName) >= 0)
                hasSharedVariable = true;
            else
            {
                predicateIsUnbound = true;
                predicateVarStart = slot.PredicateStart;
                predicateVarLen = slot.PredicateLength;
            }
        }
        if (slot.ObjectType == TermType.Variable)
        {
            var varName = _source.Slice(slot.ObjectStart, slot.ObjectLength);
            if (_bindingTable.FindBinding(varName) >= 0)
                hasSharedVariable = true;
            else
            {
                objectIsUnbound = true;
                objectVarStart = slot.ObjectStart;
                objectVarLen = slot.ObjectLength;
            }
        }

        // Domain disjointness: no shared variables means solution is never excluded
        if (!hasSharedVariable)
            return false;

        // Resolve terms using current bindings, expand prefixed names
        var subject = ResolveSlotTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, SlotTermPosition.Subject);
        var predicate = ResolveSlotTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, SlotTermPosition.Predicate);
        var obj = ResolveSlotTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, SlotTermPosition.Object);

        // Check if MINUS has a filter that needs evaluation
        bool hasMinusFilter = _buffer.HasMinusFilter;

        // Query the store to see if this pattern matches - use graph context if inside a GRAPH clause
        var results = _graphContext != null
            ? _store.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
            : _store.QueryCurrent(subject, predicate, obj);
        try
        {
            if (!hasMinusFilter)
            {
                // No filter - just check if any match exists
                return results.MoveNext();
            }

            // With FILTER: iterate over all matches and evaluate filter for each
            var filterExpr = _source.Slice(_buffer.MinusFilterStart, _buffer.MinusFilterLength);

            while (results.MoveNext())
            {
                var triple = results.Current;

                // Evaluate the MINUS filter with combined bindings
                if (EvaluateMinusFilterWithTriple(filterExpr, triple, subjectIsUnbound, subjectVarStart, subjectVarLen,
                    predicateIsUnbound, predicateVarStart, predicateVarLen, objectIsUnbound, objectVarStart, objectVarLen))
                {
                    return true; // Filter matched - exclude this solution
                }
            }

            // No match satisfied the filter - don't exclude
            return false;
        }
        finally
        {
            results.Dispose();
        }
    }

    /// <summary>
    /// Evaluate MINUS filter expression with a matched triple's bindings.
    /// Creates combined bindings from current bindings + MINUS pattern bindings.
    /// </summary>
    private bool EvaluateMinusFilterWithTriple(
        ReadOnlySpan<char> filterExpr,
        in ResolvedTemporalQuad triple,
        bool subjectIsUnbound, int subjectVarStart, int subjectVarLen,
        bool predicateIsUnbound, int predicateVarStart, int predicateVarLen,
        bool objectIsUnbound, int objectVarStart, int objectVarLen)
    {
        // Get string values for unbound variables from matched triple
        ReadOnlySpan<char> subjectValue = subjectIsUnbound ? triple.Subject : default;
        ReadOnlySpan<char> predicateValue = predicateIsUnbound ? triple.Predicate : default;
        ReadOnlySpan<char> objectValue = objectIsUnbound ? triple.Object : default;

        // Calculate combined buffer size
        int extraBufferSize = subjectValue.Length + predicateValue.Length + objectValue.Length;
        int newBindingCount = (subjectIsUnbound ? 1 : 0) + (predicateIsUnbound ? 1 : 0) + (objectIsUnbound ? 1 : 0);
        int totalBindings = _bindingTable.Count + newBindingCount;
        int existingStringLen = _bindingTable.StringBufferLength;
        int totalStringLen = existingStringLen + extraBufferSize;

        // Use ArrayPool for both bindings and strings (stackalloc can't be passed to FilterEvaluator)
        var rentedBindings = System.Buffers.ArrayPool<Binding>.Shared.Rent(totalBindings);
        var rentedStrings = System.Buffers.ArrayPool<char>.Shared.Rent(Math.Max(1, totalStringLen));

        try
        {
            int bindIdx = 0;

            // Copy current bindings
            for (int i = 0; i < _bindingTable.Count; i++)
            {
                rentedBindings[bindIdx++] = _bindingTable.Get(i);
            }

            // Copy existing string buffer
            if (existingStringLen > 0)
            {
                _bindingTable.CopyStringsTo(rentedStrings.AsSpan());
            }
            int strPos = existingStringLen;

            // Add new MINUS variable bindings with correct Binding struct fields
            if (subjectIsUnbound && subjectVarLen > 0)
            {
                subjectValue.CopyTo(rentedStrings.AsSpan(strPos));
                var varName = _source.Slice(subjectVarStart, subjectVarLen);
                rentedBindings[bindIdx++] = new Binding
                {
                    VariableNameHash = ComputeVariableHash(varName),
                    Type = BindingValueType.Uri,
                    StringOffset = strPos,
                    StringLength = subjectValue.Length
                };
                strPos += subjectValue.Length;
            }
            if (predicateIsUnbound && predicateVarLen > 0)
            {
                predicateValue.CopyTo(rentedStrings.AsSpan(strPos));
                var varName = _source.Slice(predicateVarStart, predicateVarLen);
                rentedBindings[bindIdx++] = new Binding
                {
                    VariableNameHash = ComputeVariableHash(varName),
                    Type = BindingValueType.Uri,
                    StringOffset = strPos,
                    StringLength = predicateValue.Length
                };
                strPos += predicateValue.Length;
            }
            if (objectIsUnbound && objectVarLen > 0)
            {
                objectValue.CopyTo(rentedStrings.AsSpan(strPos));
                var varName = _source.Slice(objectVarStart, objectVarLen);
                // Determine type - could be URI or literal
                var valueType = objectValue.Length > 0 && objectValue[0] == '<'
                    ? BindingValueType.Uri
                    : BindingValueType.String;
                rentedBindings[bindIdx++] = new Binding
                {
                    VariableNameHash = ComputeVariableHash(varName),
                    Type = valueType,
                    StringOffset = strPos,
                    StringLength = objectValue.Length
                };
                strPos += objectValue.Length;
            }

            // Evaluate the filter expression with prefix expansion support
            var evaluator = new FilterEvaluator(filterExpr);
            return evaluator.Evaluate(rentedBindings.AsSpan(0, bindIdx), bindIdx, rentedStrings.AsSpan(0, strPos),
                _buffer?.Prefixes, _source);
        }
        finally
        {
            System.Buffers.ArrayPool<Binding>.Shared.Return(rentedBindings);
            System.Buffers.ArrayPool<char>.Shared.Return(rentedStrings);
        }
    }

    /// <summary>
    /// Compute FNV-1a hash for variable name (matches BindingTable and FilterEvaluator).
    /// </summary>
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
    /// Compare a bound value with a VALUES value, handling quoted strings and numeric literals.
    /// </summary>
    private static bool CompareValuesMatch(ReadOnlySpan<char> boundValue, ReadOnlySpan<char> valuesValue)
    {
        // Direct match
        if (boundValue.SequenceEqual(valuesValue))
            return true;

        // Handle numeric literals in VALUES (e.g., 25, 30) matching typed literals (e.g., "25"^^<xsd:integer>)
        if (valuesValue.Length > 0 && IsNumericLiteral(valuesValue))
        {
            // Bound value might be a typed literal like "25"^^<xsd:integer>
            if (boundValue.Length >= 2 && boundValue[0] == '"')
            {
                // Find the second quote (end of literal value)
                int endQuote = -1;
                for (int i = 1; i < boundValue.Length; i++)
                {
                    if (boundValue[i] == '"')
                    {
                        endQuote = i;
                        break;
                    }
                }
                if (endQuote > 1)
                {
                    var boundNumeric = boundValue.Slice(1, endQuote - 1);
                    if (boundNumeric.SequenceEqual(valuesValue))
                        return true;
                }
            }
        }

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
