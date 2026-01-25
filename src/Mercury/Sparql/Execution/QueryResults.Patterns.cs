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
            // Pass prefixes for prefix expansion in filter expressions (e.g., ?a = :s1)
            var result = _filterEvaluator.Evaluate(
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                _buffer.Prefixes,
                _source);

            if (!result) return false;
        }
        return true;
    }

    /// <summary>
    /// Evaluate all EXISTS/NOT EXISTS filters.
    /// Returns true if all filters pass, false if any filter fails.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
    /// Uses nested-loop join semantics: patterns share variables within the EXISTS block.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateExistsPatternFromSlot(PatternSlot existsSlot, PatternArray patterns)
    {
        if (_store == null || existsSlot.ExistsChildCount == 0)
            return false;

        // Collect triple patterns from EXISTS block with their graph context
        int existsStart = existsSlot.ExistsChildStart;
        int existsEnd = existsStart + existsSlot.ExistsChildCount;

        // Count actual triple patterns (including those inside GRAPH blocks)
        int tripleCount = 0;
        for (int p = existsStart; p < existsEnd; p++)
        {
            var kind = patterns[p].Kind;
            if (kind == PatternKind.Triple)
                tripleCount++;
            else if (kind == PatternKind.GraphHeader)
            {
                // Count triples within GRAPH block
                var graphSlot = patterns[p];
                int childEnd = graphSlot.ChildStartIndex + graphSlot.ChildCount;
                for (int c = graphSlot.ChildStartIndex; c < childEnd; c++)
                {
                    if (patterns[c].Kind == PatternKind.Triple)
                        tripleCount++;
                }
            }
        }

        if (tripleCount == 0)
            return false;

        // Collect pattern info (index + graph context)
        var patternInfos = new ExistsPatternInfo[tripleCount];
        int idx = 0;
        for (int p = existsStart; p < existsEnd && idx < tripleCount; p++)
        {
            var kind = patterns[p].Kind;
            if (kind == PatternKind.Triple)
            {
                // Pattern outside any GRAPH clause - use outer _graphContext
                patternInfos[idx++] = new ExistsPatternInfo
                {
                    PatternIndex = p,
                    GraphContext = null,
                    HasGraphClause = false
                };
            }
            else if (kind == PatternKind.GraphHeader)
            {
                // Resolve the graph term (could be variable bound from outer query or IRI)
                var graphSlot = patterns[p];
                string? graphContext = ResolveExistsGraphTerm(
                    graphSlot.GraphTermType,
                    graphSlot.GraphTermStart,
                    graphSlot.GraphTermLength);

                // Collect child triple patterns with the resolved graph context
                int childEnd = graphSlot.ChildStartIndex + graphSlot.ChildCount;
                for (int c = graphSlot.ChildStartIndex; c < childEnd && idx < tripleCount; c++)
                {
                    if (patterns[c].Kind == PatternKind.Triple)
                    {
                        patternInfos[idx++] = new ExistsPatternInfo
                        {
                            PatternIndex = c,
                            GraphContext = graphContext,
                            HasGraphClause = true
                        };
                    }
                }
                // Skip past the GraphHeader's children in the outer loop to avoid processing them twice
                p = childEnd - 1; // -1 because the for loop will increment p
            }
        }

        // Use nested-loop join: try to find ANY consistent binding across all patterns
        var existsBindings = new ExistsBinding[16]; // Max 16 EXISTS-local variables
        int bindingCount = 0;

        return EvaluateExistsJoinWithGraphContext(patterns, patternInfos, idx, 0, existsBindings, ref bindingCount);
    }

    /// <summary>
    /// Resolve graph term for GRAPH clause inside EXISTS.
    /// Returns resolved graph IRI or null if variable is unbound.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private string? ResolveExistsGraphTerm(TermType type, int start, int length)
    {
        if (type == TermType.Variable)
        {
            // Look up variable binding from outer query
            var varName = _source.Slice(start, length);
            var outerIdx = _bindingTable.FindBinding(varName);
            if (outerIdx >= 0)
            {
                var boundValue = _bindingTable.GetString(outerIdx);
                return boundValue.ToString();
            }
            // Variable unbound - return null (will fail matching)
            return null;
        }

        // IRI or prefixed name - expand and return
        var termValue = _source.Slice(start, length);
        var expanded = ExpandPrefixedName(termValue);
        return expanded.ToString();
    }

    /// <summary>
    /// Iterative nested-loop join for EXISTS patterns with per-pattern graph context.
    /// Returns true if any consistent binding exists for all patterns.
    /// Converted from recursive to iterative to avoid stack overflow (ADR-003).
    ///
    /// TERMINATION GUARANTEES:
    /// 1. Stack depth is bounded by patternCount (finite)
    /// 2. ResultIndex only increases (never resets within same state)
    /// 3. stackTop increases only when advancing to next pattern
    /// 4. stackTop decreases when results exhausted (backtracking)
    /// 5. Loop exits when: all patterns matched (true) OR stack empty (false)
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateExistsJoinWithGraphContext(PatternArray patterns, ExistsPatternInfo[] patternInfos,
        int patternCount, int currentPattern, ExistsBinding[] bindings, ref int bindingCount)
    {
        // Handle edge case: no patterns to match
        if (patternCount == 0 || currentPattern >= patternCount)
            return true;

        // Allocate state stack on heap (bounded by patternCount, typically small: 1-5)
        var stateStack = new ExistsJoinState[patternCount];
        int stackTop = 0;

        // Initialize first pattern state
        stateStack[0] = new ExistsJoinState
        {
            PatternIndex = currentPattern,
            BindingCountBefore = bindingCount,
            Results = null,
            ResultIndex = -1
        };
        stackTop = 1;

        // Main iteration loop
        while (stackTop > 0)
        {
            ref var state = ref stateStack[stackTop - 1];

            // Initialize results if not yet done (materialize query to list)
            if (state.Results == null)
            {
                var info = patternInfos[state.PatternIndex];
                var slot = patterns[info.PatternIndex];

                // Check graph context - if GRAPH clause but variable unbound, fail this branch
                if (info.HasGraphClause && info.GraphContext == null)
                {
                    stackTop--;
                    if (stackTop > 0)
                        bindingCount = stateStack[stackTop - 1].BindingCountBefore;
                    continue;
                }

                // Resolve terms using current bindings
                var subject = ResolveExistsTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength,
                    SlotTermPosition.Subject, bindings, bindingCount);
                var predicate = ResolveExistsTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength,
                    SlotTermPosition.Predicate, bindings, bindingCount);
                var obj = ResolveExistsTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength,
                    SlotTermPosition.Object, bindings, bindingCount);

                // Materialize query results to list (avoids ref struct lifetime issues)
                state.Results = MaterializeExistsQuery(subject, predicate, obj, info);
                state.ResultIndex = -1;
                state.BindingCountBefore = bindingCount;
            }

            // Advance to next result (PROGRESS: ResultIndex increases)
            state.ResultIndex++;

            // Check if we have more results to try
            if (state.ResultIndex < state.Results.Count)
            {
                var triple = state.Results[state.ResultIndex];
                var info = patternInfos[state.PatternIndex];
                var slot = patterns[info.PatternIndex];

                // Restore bindings to state before this pattern, then add new bindings
                bindingCount = state.BindingCountBefore;
                AddExistsBindingsFromMaterialized(slot, triple, bindings, ref bindingCount);

                // Check if we've matched all patterns
                if (state.PatternIndex + 1 >= patternCount)
                {
                    return true; // SUCCESS!
                }

                // Push next pattern onto stack (PROGRESS: PatternIndex increases)
                stateStack[stackTop] = new ExistsJoinState
                {
                    PatternIndex = state.PatternIndex + 1,
                    BindingCountBefore = bindingCount,
                    Results = null,
                    ResultIndex = -1
                };
                stackTop++;
            }
            else
            {
                // Results exhausted - backtrack (PROGRESS: stackTop decreases)
                stackTop--;
                if (stackTop > 0)
                    bindingCount = stateStack[stackTop - 1].BindingCountBefore;
            }
        }

        return false; // Stack empty, no complete match found
    }

    /// <summary>
    /// Materialize EXISTS query results to a list.
    /// This allows iterative traversal without ref struct lifetime issues.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedTriple> MaterializeExistsQuery(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj, ExistsPatternInfo info)
    {
        var results = new List<MaterializedTriple>();

        TemporalResultEnumerator enumerator;
        if (info.HasGraphClause)
        {
            enumerator = _store!.QueryCurrent(subject, predicate, obj, info.GraphContext!.AsSpan());
        }
        else if (_graphContext != null)
        {
            enumerator = _store!.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan());
        }
        else
        {
            enumerator = _store!.QueryCurrent(subject, predicate, obj);
        }

        try
        {
            while (enumerator.MoveNext())
            {
                var triple = enumerator.Current;
                results.Add(new MaterializedTriple
                {
                    Subject = triple.Subject.ToString(),
                    Predicate = triple.Predicate.ToString(),
                    Object = triple.Object.ToString()
                });
            }
        }
        finally
        {
            enumerator.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Add bindings from a materialized triple to EXISTS binding set.
    /// </summary>
    private void AddExistsBindingsFromMaterialized(PatternSlot slot, MaterializedTriple triple,
        ExistsBinding[] bindings, ref int count)
    {
        if (slot.SubjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.SubjectStart, slot.SubjectLength);
            var varHash = ComputeVariableHash(varName);
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Subject };
            }
        }

        if (slot.PredicateType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.PredicateStart, slot.PredicateLength);
            var varHash = ComputeVariableHash(varName);
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Predicate };
            }
        }

        if (slot.ObjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.ObjectStart, slot.ObjectLength);
            var varHash = ComputeVariableHash(varName);
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Object };
            }
        }
    }

    /// <summary>
    /// Iterative nested-loop join for EXISTS patterns.
    /// Returns true if any consistent binding exists for all patterns.
    /// Converted from recursive to iterative to avoid stack overflow (ADR-003).
    ///
    /// TERMINATION GUARANTEES:
    /// 1. Stack depth is bounded by patternCount (finite)
    /// 2. ResultIndex only increases (never resets within same state)
    /// 3. stackTop increases only when advancing to next pattern
    /// 4. stackTop decreases when results exhausted (backtracking)
    /// 5. Loop exits when: all patterns matched (true) OR stack empty (false)
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateExistsJoin(PatternArray patterns, int[] patternIndices, int patternCount,
        int currentPattern, ExistsBinding[] bindings, ref int bindingCount)
    {
        // Handle edge case: no patterns to match
        if (patternCount == 0 || currentPattern >= patternCount)
            return true;

        // Allocate state stack on heap (bounded by patternCount, typically small: 1-5)
        var stateStack = new ExistsJoinState[patternCount];
        int stackTop = 0;

        // Initialize first pattern state
        stateStack[0] = new ExistsJoinState
        {
            PatternIndex = currentPattern,
            BindingCountBefore = bindingCount,
            Results = null,
            ResultIndex = -1
        };
        stackTop = 1;

        // Main iteration loop
        while (stackTop > 0)
        {
            ref var state = ref stateStack[stackTop - 1];

            // Initialize results if not yet done (materialize query to list)
            if (state.Results == null)
            {
                var slot = patterns[patternIndices[state.PatternIndex]];

                // Resolve terms using current bindings
                var subject = ResolveExistsTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength,
                    SlotTermPosition.Subject, bindings, bindingCount);
                var predicate = ResolveExistsTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength,
                    SlotTermPosition.Predicate, bindings, bindingCount);
                var obj = ResolveExistsTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength,
                    SlotTermPosition.Object, bindings, bindingCount);

                // Materialize query results to list
                state.Results = MaterializeExistsQuerySimple(subject, predicate, obj);
                state.ResultIndex = -1;
                state.BindingCountBefore = bindingCount;
            }

            // Advance to next result (PROGRESS: ResultIndex increases)
            state.ResultIndex++;

            // Check if we have more results to try
            if (state.ResultIndex < state.Results.Count)
            {
                var triple = state.Results[state.ResultIndex];
                var slot = patterns[patternIndices[state.PatternIndex]];

                // Restore bindings to state before this pattern, then add new bindings
                bindingCount = state.BindingCountBefore;
                AddExistsBindingsFromMaterialized(slot, triple, bindings, ref bindingCount);

                // Check if we've matched all patterns
                if (state.PatternIndex + 1 >= patternCount)
                {
                    return true; // SUCCESS!
                }

                // Push next pattern onto stack (PROGRESS: PatternIndex increases)
                stateStack[stackTop] = new ExistsJoinState
                {
                    PatternIndex = state.PatternIndex + 1,
                    BindingCountBefore = bindingCount,
                    Results = null,
                    ResultIndex = -1
                };
                stackTop++;
            }
            else
            {
                // Results exhausted - backtrack (PROGRESS: stackTop decreases)
                stackTop--;
                if (stackTop > 0)
                    bindingCount = stateStack[stackTop - 1].BindingCountBefore;
            }
        }

        return false; // Stack empty, no complete match found
    }

    /// <summary>
    /// Materialize EXISTS query results to a list (simple version without graph info).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedTriple> MaterializeExistsQuerySimple(ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        var results = new List<MaterializedTriple>();

        var enumerator = _graphContext != null
            ? _store!.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
            : _store!.QueryCurrent(subject, predicate, obj);

        try
        {
            while (enumerator.MoveNext())
            {
                var triple = enumerator.Current;
                results.Add(new MaterializedTriple
                {
                    Subject = triple.Subject.ToString(),
                    Predicate = triple.Predicate.ToString(),
                    Object = triple.Object.ToString()
                });
            }
        }
        finally
        {
            enumerator.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Resolve a term in EXISTS pattern using both outer bindings and EXISTS-local bindings.
    /// </summary>
    private ReadOnlySpan<char> ResolveExistsTerm(TermType type, int start, int length,
        SlotTermPosition pos, ExistsBinding[] existsBindings, int bindingCount)
    {
        if (type == TermType.Variable)
        {
            var varName = _source.Slice(start, length);
            var varHash = ComputeVariableHash(varName);

            // First check EXISTS-local bindings
            for (int i = 0; i < bindingCount; i++)
            {
                if (existsBindings[i].VariableHash == varHash)
                    return existsBindings[i].Value.AsSpan();
            }

            // Then check outer query bindings
            var outerIdx = _bindingTable.FindBinding(varName);
            if (outerIdx >= 0)
                return _bindingTable.GetString(outerIdx);

            // Unbound - return empty for wildcard query
            return ReadOnlySpan<char>.Empty;
        }

        // Non-variable: use standard resolution
        return ResolveSlotTerm(type, start, length, pos);
    }

    /// <summary>
    /// Add bindings from a matched triple to EXISTS binding set.
    /// Only adds bindings for variables that are not yet bound.
    /// </summary>
    private void AddExistsBindingsFromTriple(PatternSlot slot, in ResolvedTemporalQuad triple,
        ExistsBinding[] bindings, ref int count)
    {
        // Subject
        if (slot.SubjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.SubjectStart, slot.SubjectLength);
            var varHash = ComputeVariableHash(varName);

            // Only add if not already bound (in EXISTS or outer)
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Subject.ToString() };
            }
        }

        // Predicate
        if (slot.PredicateType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.PredicateStart, slot.PredicateLength);
            var varHash = ComputeVariableHash(varName);

            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Predicate.ToString() };
            }
        }

        // Object
        if (slot.ObjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.ObjectStart, slot.ObjectLength);
            var varHash = ComputeVariableHash(varName);

            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Object.ToString() };
            }
        }
    }

    /// <summary>
    /// Check if a variable is already bound in EXISTS bindings.
    /// </summary>
    private static bool IsExistsVariableBound(int varHash, ExistsBinding[] bindings, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (bindings[i].VariableHash == varHash)
                return true;
        }
        return false;
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

            // Get base IRI for relative IRI resolution (buffer is non-null at this point)
            var baseIri = _buffer!.BaseUriLength > 0
                ? _source.Slice(_buffer.BaseUriStart, _buffer.BaseUriLength)
                : ReadOnlySpan<char>.Empty;

            // Evaluate the expression
            var evaluator = new BindExpressionEvaluator(expr,
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                baseIri);
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

            // Get base IRI for relative IRI resolution
            var baseIri = _buffer != null && _buffer.BaseUriLength > 0
                ? _source.Slice(_buffer.BaseUriStart, _buffer.BaseUriLength)
                : ReadOnlySpan<char>.Empty;

            // Evaluate the expression using BindExpressionEvaluator
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
    /// Check if current bindings match any MINUS block.
    /// Returns true if any MINUS block matches (solution should be excluded).
    /// Handles multiple MINUS blocks by evaluating each block independently.
    /// </summary>
    private bool MatchesMinusPattern()
    {
        if (_store == null || _buffer == null) return false;


        // If MINUS has OPTIONAL patterns, use the group evaluation logic
        if (_buffer.HasMinusOptionalPatterns)
        {
            return MatchesMinusPatternWithOptional();
        }

        // If there are multiple blocks, EXISTS filters, compound EXISTS refs, or nested MINUS, evaluate each block separately
        if (_buffer.MinusBlockCount > 1 || _buffer.HasMinusExists || _buffer.HasCompoundExistsRefs || _buffer.HasNestedMinus)
        {
            return MatchesMinusPatternMultiBlock();
        }

        var patterns = _buffer.GetPatterns();

        // Single block: For MINUS semantics: exclude if ALL MINUS patterns match
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
    /// Evaluate multiple MINUS blocks independently.
    /// A solution is excluded if ANY block matches.
    /// </summary>
    private bool MatchesMinusPatternMultiBlock()
    {
        if (_store == null || _buffer == null) return false;

        int blockCount = Math.Max(1, _buffer.MinusBlockCount);

        // Evaluate each MINUS block independently
        for (int block = 0; block < blockCount; block++)
        {
            if (MatchesSingleMinusBlock(block))
                return true; // This block matched - exclude the solution
        }

        return false; // No block matched - keep the solution
    }

    /// <summary>
    /// Evaluate a single MINUS block.
    /// Returns true if all patterns in the block match AND EXISTS filters pass.
    /// </summary>
    private bool MatchesSingleMinusBlock(int blockIndex)
    {
        if (_store == null || _buffer == null) return false;

        int patternStart = _buffer.GetMinusBlockStart(blockIndex);
        int patternEnd = _buffer.GetMinusBlockEnd(blockIndex);

        if (patternEnd <= patternStart) return false; // Empty block

        var patterns = _buffer.GetPatterns();

        // Collect pattern indices for this block
        var blockPatternIndices = new int[8];
        int blockPatternCount = 0;

        for (int i = 0; i < _buffer.PatternCount && blockPatternCount < 8; i++)
        {
            if (patterns[i].Kind != PatternKind.MinusTriple) continue;

            // Check if this pattern belongs to this block by counting MINUS patterns
            int minusIdx = 0;
            for (int j = 0; j < i; j++)
            {
                if (patterns[j].Kind == PatternKind.MinusTriple) minusIdx++;
            }

            if (minusIdx >= patternStart && minusIdx < patternEnd)
            {
                blockPatternIndices[blockPatternCount++] = i;
            }
        }

        if (blockPatternCount == 0) return false;

        // Check if this block has EXISTS filters
        bool hasBlockExists = false;
        for (int i = 0; i < _buffer.MinusExistsCount; i++)
        {
            if (_buffer.GetMinusExistsBlock(i) == blockIndex)
            {
                hasBlockExists = true;
                break;
            }
        }

        // Use nested-loop join to evaluate the block
        var minusBindings = new ExistsBinding[16];
        int bindingCount = 0;

        return EvaluateMinusBlockJoin(patterns, blockPatternIndices, blockPatternCount, 0,
            minusBindings, ref bindingCount, blockIndex, hasBlockExists);
    }

    /// <summary>
    /// Recursive nested-loop join for a single MINUS block.
    /// </summary>
    private bool EvaluateMinusBlockJoin(PatternArray patterns, int[] patternIndices, int patternCount,
        int currentPattern, ExistsBinding[] bindings, ref int bindingCount, int blockIndex, bool hasBlockExists)
    {
        if (currentPattern >= patternCount)
        {
            // All patterns in this block matched - now evaluate EXISTS filters for this block
            if (hasBlockExists && !EvaluateMinusBlockExistsFilters(bindings, bindingCount, blockIndex))
                return false; // EXISTS filter failed - this MINUS solution doesn't exclude

            // Check if this block has a compound filter with EXISTS
            if (_buffer != null && _buffer.HasMinusFilter && _buffer.MinusFilterBlock == blockIndex)
            {
                if (!EvaluateMinusBlockFilter(bindings, bindingCount, blockIndex))
                    return false; // Filter failed - this MINUS solution doesn't exclude
            }

            // Check if this block has nested MINUS blocks
            // If a nested MINUS excludes the solution, then the outer MINUS doesn't match
            // (the nested MINUS removes it from the outer MINUS's result set)
            if (_buffer != null && _buffer.HasNestedMinus)
            {
                if (IsExcludedByNestedMinus(bindings, bindingCount, blockIndex))
                    return false; // Nested MINUS excluded this - outer MINUS doesn't match
            }

            // Check domain overlap
            return HasMinusDomainOverlap(bindings, bindingCount);
        }

        var slot = patterns[patternIndices[currentPattern]];

        // Resolve terms using outer bindings AND MINUS-local bindings
        var subject = ResolveMinusExistsTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength,
            SlotTermPosition.Subject, bindings, bindingCount);
        var predicate = ResolveMinusExistsTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength,
            SlotTermPosition.Predicate, bindings, bindingCount);
        var obj = ResolveMinusExistsTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength,
            SlotTermPosition.Object, bindings, bindingCount);

        // Query the store
        var results = _graphContext != null
            ? _store!.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
            : _store!.QueryCurrent(subject, predicate, obj);

        try
        {
            while (results.MoveNext())
            {
                var triple = results.Current;
                int savedBindingCount = bindingCount;

                // Add bindings from this match for unbound variables
                AddMinusExistsBindings(slot, triple, bindings, ref bindingCount);

                // Recursively try remaining patterns
                if (EvaluateMinusBlockJoin(patterns, patternIndices, patternCount, currentPattern + 1,
                    bindings, ref bindingCount, blockIndex, hasBlockExists))
                    return true;

                // Backtrack
                bindingCount = savedBindingCount;
            }
        }
        finally
        {
            results.Dispose();
        }

        return false;
    }

    /// <summary>
    /// Evaluate EXISTS filters that belong to a specific MINUS block.
    /// </summary>
    private bool EvaluateMinusBlockExistsFilters(ExistsBinding[] minusBindings, int minusBindingCount, int blockIndex)
    {
        if (_buffer == null || !_buffer.HasMinusExists)
            return true;

        for (int i = 0; i < _buffer.MinusExistsCount; i++)
        {
            // Only evaluate EXISTS filters that belong to this block
            if (_buffer.GetMinusExistsBlock(i) != blockIndex)
                continue;

            // Skip EXISTS filters that are part of compound expressions
            // (they'll be evaluated during EvaluateMinusBlockFilter)
            if (IsExistsFilterPartOfCompoundExpression(i, blockIndex))
                continue;

            var existsFilter = _buffer.GetMinusExistsFilter(i);
            if (existsFilter.PatternCount == 0)
                continue;

            // Evaluate EXISTS patterns with combined bindings
            bool matches = EvaluateMinusExistsPatterns(existsFilter, minusBindings, minusBindingCount);

            // For NOT EXISTS: should NOT match
            // For EXISTS: should match
            if (existsFilter.Negated)
            {
                if (matches) return false; // NOT EXISTS failed - found a match
            }
            else
            {
                if (!matches) return false; // EXISTS failed - no match
            }
        }

        return true;
    }

    /// <summary>
    /// Check if a MinusExistsFilter is referenced by a CompoundExistsRef.
    /// </summary>
    private bool IsExistsFilterPartOfCompoundExpression(int existsFilterIndex, int blockIndex)
    {
        if (_buffer == null || !_buffer.HasCompoundExistsRefs)
            return false;

        for (int i = 0; i < _buffer.CompoundExistsRefCount; i++)
        {
            var existsRef = _buffer.GetCompoundExistsRef(i);
            if (existsRef.BlockIndex == blockIndex && existsRef.ExistsFilterIndex == existsFilterIndex)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if the current solution is excluded by any nested MINUS block belonging to the given outer block.
    /// </summary>
    /// <remarks>
    /// For a MINUS block with content: { base_patterns MINUS nested_1 MINUS nested_2 }
    /// The semantics are: solutions from base_patterns EXCEPT those matching nested_1 EXCEPT those matching nested_2.
    /// If a nested MINUS matches the solution, it removes it from the outer MINUS result set.
    /// </remarks>
    private bool IsExcludedByNestedMinus(ExistsBinding[] outerBindings, int outerBindingCount, int outerBlockIndex)
    {
        if (_buffer == null || !_buffer.HasNestedMinus || _store == null)
            return false;

        // Check each nested MINUS block that belongs to this outer block
        for (int nestedIdx = 0; nestedIdx < _buffer.NestedMinusCount; nestedIdx++)
        {
            var parentBlock = _buffer.GetNestedMinusParentBlock(nestedIdx);

            if (parentBlock != outerBlockIndex)
                continue;

            // This nested MINUS belongs to our outer block - evaluate it
            if (EvaluateNestedMinusBlock(nestedIdx, outerBindings, outerBindingCount))
            {
                // Nested MINUS matched - this solution is excluded from outer MINUS
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluate a single nested MINUS block to check if it matches the current solution.
    /// </summary>
    private bool EvaluateNestedMinusBlock(int nestedBlockIndex, ExistsBinding[] outerBindings, int outerBindingCount)
    {
        if (_buffer == null || _store == null)
            return false;

        int patternStart = _buffer.GetNestedMinusBlockStart(nestedBlockIndex);
        int patternEnd = _buffer.GetNestedMinusBlockEnd(nestedBlockIndex);

        if (patternEnd <= patternStart)
            return false; // Empty block

        int patternCount = patternEnd - patternStart;

        // Use nested-loop join to evaluate the nested MINUS patterns
        var nestedBindings = new ExistsBinding[16];
        int nestedBindingCount = 0;

        // Copy outer bindings as starting point
        for (int i = 0; i < outerBindingCount && i < 16; i++)
        {
            nestedBindings[nestedBindingCount++] = outerBindings[i];
        }

        return EvaluateNestedMinusJoin(nestedBlockIndex, patternStart, patternEnd, 0,
            nestedBindings, ref nestedBindingCount);
    }

    /// <summary>
    /// Recursive nested-loop join for a nested MINUS block.
    /// </summary>
    private bool EvaluateNestedMinusJoin(int nestedBlockIndex, int patternStart, int patternEnd,
        int currentPatternOffset, ExistsBinding[] bindings, ref int bindingCount)
    {
        if (_buffer == null || _store == null)
            return false;

        int currentPatternIdx = patternStart + currentPatternOffset;
        if (currentPatternIdx >= patternEnd)
        {
            // All patterns matched - now check EXISTS filter if present
            if (_buffer.HasNestedMinusExistsFilter(nestedBlockIndex))
            {
                var existsFilter = _buffer.GetNestedMinusExistsFilter(nestedBlockIndex);
                if (existsFilter.PatternCount > 0)
                {
                    bool matches = EvaluateMinusExistsPatterns(existsFilter, bindings, bindingCount);

                    // For NOT EXISTS: should NOT match
                    // For EXISTS: should match
                    if (existsFilter.Negated)
                    {
                        if (matches) return false; // NOT EXISTS failed - found a match
                    }
                    else
                    {
                        if (!matches) return false; // EXISTS failed - no match
                    }
                }
            }

            // All patterns matched and EXISTS filter passed (if any)
            // The nested MINUS matched this solution
            return true;
        }

        var pattern = _buffer.GetNestedMinusPattern(currentPatternIdx);

        // Resolve terms using current bindings (outer + nested-local)
        var subject = ResolveNestedMinusTerm(pattern.Subject, bindings, bindingCount);
        var predicate = ResolveNestedMinusTerm(pattern.Predicate, bindings, bindingCount);
        var obj = ResolveNestedMinusTerm(pattern.Object, bindings, bindingCount);

        // Query the store
        var results = _graphContext != null
            ? _store.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
            : _store.QueryCurrent(subject, predicate, obj);

        try
        {
            while (results.MoveNext())
            {
                var triple = results.Current;
                int savedBindingCount = bindingCount;

                // Add bindings from this match for unbound variables
                AddNestedMinusBindings(pattern, triple, bindings, ref bindingCount);

                // Recursively try remaining patterns
                if (EvaluateNestedMinusJoin(nestedBlockIndex, patternStart, patternEnd,
                    currentPatternOffset + 1, bindings, ref bindingCount))
                    return true;

                // Backtrack
                bindingCount = savedBindingCount;
            }
        }
        finally
        {
            results.Dispose();
        }

        return false;
    }

    /// <summary>
    /// Resolve a term from a nested MINUS pattern using current bindings.
    /// </summary>
    private ReadOnlySpan<char> ResolveNestedMinusTerm(Term term, ExistsBinding[] bindings, int bindingCount)
    {
        if (term.Type == TermType.Variable)
        {
            var varName = _source.Slice(term.Start, term.Length);
            int varHash = ComputeVariableHash(varName);

            // First check outer query bindings
            var outerIdx = _bindingTable.FindBinding(varName);
            if (outerIdx >= 0)
            {
                return _bindingTable.GetString(outerIdx);
            }

            // Then check nested bindings
            for (int i = 0; i < bindingCount; i++)
            {
                if (bindings[i].VariableHash == varHash)
                {
                    return bindings[i].Value.AsSpan();
                }
            }

            return ReadOnlySpan<char>.Empty;
        }

        // For IRIs and literals, expand prefixed names
        var termValue = _source.Slice(term.Start, term.Length);
        return ExpandPrefixedName(termValue);
    }

    /// <summary>
    /// Add bindings from a nested MINUS pattern match.
    /// </summary>
    private void AddNestedMinusBindings(TriplePattern pattern, in ResolvedTemporalQuad triple,
        ExistsBinding[] bindings, ref int bindingCount)
    {
        if (pattern.Subject.Type == TermType.Variable && bindingCount < bindings.Length)
        {
            var varName = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
            int varHash = ComputeVariableHash(varName);
            if (!HasNestedMinusBinding(varName, varHash, bindings, bindingCount))
            {
                bindings[bindingCount++] = new ExistsBinding
                {
                    VariableHash = varHash,
                    Value = triple.Subject.ToString()
                };
            }
        }

        if (pattern.Predicate.Type == TermType.Variable && bindingCount < bindings.Length)
        {
            var varName = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
            int varHash = ComputeVariableHash(varName);
            if (!HasNestedMinusBinding(varName, varHash, bindings, bindingCount))
            {
                bindings[bindingCount++] = new ExistsBinding
                {
                    VariableHash = varHash,
                    Value = triple.Predicate.ToString()
                };
            }
        }

        if (pattern.Object.Type == TermType.Variable && bindingCount < bindings.Length)
        {
            var varName = _source.Slice(pattern.Object.Start, pattern.Object.Length);
            int varHash = ComputeVariableHash(varName);
            if (!HasNestedMinusBinding(varName, varHash, bindings, bindingCount))
            {
                bindings[bindingCount++] = new ExistsBinding
                {
                    VariableHash = varHash,
                    Value = triple.Object.ToString()
                };
            }
        }
    }

    /// <summary>
    /// Check if a variable already has a binding in nested MINUS context.
    /// </summary>
    private bool HasNestedMinusBinding(ReadOnlySpan<char> varName, int varHash, ExistsBinding[] bindings, int bindingCount)
    {
        // Check outer query bindings first
        if (_bindingTable.FindBinding(varName) >= 0)
            return true;

        // Check nested bindings by hash
        for (int i = 0; i < bindingCount; i++)
        {
            if (bindings[i].VariableHash == varHash)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check domain overlap for nested MINUS.
    /// Returns true if there's overlap between nested bindings and outer query bindings.
    /// </summary>
    private bool HasMinusDomainOverlapWithBindings(ExistsBinding[] bindings, int bindingCount)
    {
        // For nested MINUS, we consider there's overlap if there are any bindings
        // The outer query already established bindings, and the nested MINUS extends them
        return bindingCount > 0;
    }

    /// <summary>
    /// Evaluate the MINUS filter expression for a specific block.
    /// Handles compound EXISTS refs by pre-evaluating them and substituting results.
    /// </summary>
    private bool EvaluateMinusBlockFilter(ExistsBinding[] minusBindings, int minusBindingCount, int blockIndex)
    {
        if (_buffer == null || !_buffer.HasMinusFilter)
            return true;

        // Get the filter expression
        var filterExpr = _source.Slice(_buffer.MinusFilterStart, _buffer.MinusFilterLength);

        // Build combined bindings (outer + MINUS-local)
        int totalBindings = _bindingTable.Count + minusBindingCount;
        int existingStringLen = _bindingTable.StringBufferLength;
        int extraStringLen = 0;

        // Calculate extra string space needed for MINUS bindings
        for (int i = 0; i < minusBindingCount; i++)
        {
            extraStringLen += minusBindings[i].Value?.Length ?? 0;
        }

        int totalStringLen = existingStringLen + extraStringLen;

        var rentedBindings = System.Buffers.ArrayPool<Binding>.Shared.Rent(totalBindings);
        var rentedStrings = System.Buffers.ArrayPool<char>.Shared.Rent(Math.Max(1, totalStringLen));

        try
        {
            int bindIdx = 0;

            // Copy outer bindings
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

            // Add MINUS-local bindings
            for (int i = 0; i < minusBindingCount; i++)
            {
                var existsBinding = minusBindings[i];
                var value = existsBinding.Value ?? "";
                value.AsSpan().CopyTo(rentedStrings.AsSpan(strPos));

                rentedBindings[bindIdx++] = new Binding
                {
                    VariableNameHash = existsBinding.VariableHash,
                    Type = BindingValueType.String,
                    StringOffset = strPos,
                    StringLength = value.Length
                };
                strPos += value.Length;
            }

            // Check for compound EXISTS refs that need substitution
            if (_buffer.HasCompoundExistsRefs && _buffer.CompoundExistsRefCount > 0)
            {
                return EvaluateMinusBlockFilterWithCompoundExists(
                    filterExpr, rentedBindings.AsSpan(0, bindIdx), bindIdx,
                    rentedStrings.AsSpan(0, strPos), minusBindings, minusBindingCount, blockIndex);
            }

            // No compound EXISTS - evaluate filter directly
            var evaluator = new FilterEvaluator(filterExpr);
            return evaluator.Evaluate(rentedBindings.AsSpan(0, bindIdx), bindIdx,
                rentedStrings.AsSpan(0, strPos), _buffer?.Prefixes, _source);
        }
        finally
        {
            System.Buffers.ArrayPool<Binding>.Shared.Return(rentedBindings);
            System.Buffers.ArrayPool<char>.Shared.Return(rentedStrings);
        }
    }

    /// <summary>
    /// Evaluate MINUS filter with compound EXISTS patterns.
    /// Pre-evaluates EXISTS patterns and substitutes "true"/"false" into the filter expression.
    /// </summary>
    private bool EvaluateMinusBlockFilterWithCompoundExists(
        ReadOnlySpan<char> filterExpr,
        ReadOnlySpan<Binding> bindings, int bindingCount,
        ReadOnlySpan<char> stringBuffer,
        ExistsBinding[] minusBindings, int minusBindingCount,
        int blockIndex)
    {
        if (_buffer == null) return true;

        // Pre-evaluate all compound EXISTS refs for this block
        // Build a list of (position, length, result) for substitution
        Span<(int Start, int Length, bool Result)> substitutions = stackalloc (int, int, bool)[2];
        int substitutionCount = 0;

        for (int i = 0; i < _buffer.CompoundExistsRefCount && substitutionCount < 2; i++)
        {
            var existsRef = _buffer.GetCompoundExistsRef(i);
            if (existsRef.BlockIndex != blockIndex)
                continue;

            // Get the EXISTS filter patterns
            var existsFilter = _buffer.GetMinusExistsFilter(existsRef.ExistsFilterIndex);

            // Evaluate the EXISTS patterns
            bool matches = EvaluateMinusExistsPatterns(existsFilter, minusBindings, minusBindingCount);

            // For NOT EXISTS, invert the result
            bool result = existsRef.Negated ? !matches : matches;

            substitutions[substitutionCount++] = (existsRef.StartInFilter, existsRef.Length, result);
        }

        if (substitutionCount == 0)
        {
            // No compound EXISTS for this block - evaluate directly
            var evaluator = new FilterEvaluator(filterExpr);
            return evaluator.Evaluate(bindings, bindingCount, stringBuffer, _buffer?.Prefixes, _source);
        }

        // Build modified filter expression with substitutions
        // Sort substitutions by position (descending) to avoid offset issues
        if (substitutionCount > 1 && substitutions[0].Start < substitutions[1].Start)
        {
            (substitutions[0], substitutions[1]) = (substitutions[1], substitutions[0]);
        }

        // Calculate new expression length
        var originalExpr = filterExpr.ToString();
        var sb = new System.Text.StringBuilder(originalExpr);

        // Apply substitutions from end to start
        for (int i = 0; i < substitutionCount; i++)
        {
            var (start, length, result) = substitutions[i];
            string replacement = result ? "true" : "false";
            sb.Remove(start, length);
            sb.Insert(start, replacement);
        }

        var modifiedExpr = sb.ToString().AsSpan();

        // Evaluate the modified filter expression
        var evalMod = new FilterEvaluator(modifiedExpr);
        return evalMod.Evaluate(bindings, bindingCount, stringBuffer, _buffer?.Prefixes, _source);
    }

    /// <summary>
    /// Check MINUS patterns when EXISTS/NOT EXISTS is inside MINUS.
    /// Uses nested-loop join to find valid MINUS solutions, then evaluates EXISTS filters.
    /// Returns true if any MINUS solution (satisfying all patterns AND EXISTS filters) has domain overlap.
    /// </summary>
    private bool MatchesMinusPatternWithExists()
    {
        if (_store == null || _buffer == null) return false;

        var patterns = _buffer.GetPatterns();

        // Collect MINUS pattern slots
        var minusSlotIndices = new int[8];
        int minusSlotCount = 0;

        for (int i = 0; i < _buffer.PatternCount && minusSlotCount < 8; i++)
        {
            if (patterns[i].Kind == PatternKind.MinusTriple)
                minusSlotIndices[minusSlotCount++] = i;
        }

        if (minusSlotCount == 0)
            return false;

        // Use nested-loop join to enumerate all MINUS solutions
        var minusBindings = new ExistsBinding[16];
        int bindingCount = 0;

        return EvaluateMinusJoinWithExists(patterns, minusSlotIndices, minusSlotCount, 0, minusBindings, ref bindingCount);
    }

    /// <summary>
    /// Recursive nested-loop join for MINUS patterns with EXISTS evaluation.
    /// Returns true if any valid MINUS solution (with matching EXISTS filters) has domain overlap.
    /// </summary>
    private bool EvaluateMinusJoinWithExists(PatternArray patterns, int[] patternIndices, int patternCount,
        int currentPattern, ExistsBinding[] bindings, ref int bindingCount)
    {
        if (currentPattern >= patternCount)
        {
            // All MINUS patterns matched - now evaluate EXISTS filters
            if (!EvaluateMinusExistsFilters(bindings, bindingCount))
                return false; // EXISTS filter failed - this MINUS solution doesn't exclude

            // MINUS solution is valid - check domain overlap
            return HasMinusDomainOverlap(bindings, bindingCount);
        }

        var slot = patterns[patternIndices[currentPattern]];

        // Resolve terms using outer bindings AND MINUS-local bindings
        var subject = ResolveMinusExistsTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength,
            SlotTermPosition.Subject, bindings, bindingCount);
        var predicate = ResolveMinusExistsTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength,
            SlotTermPosition.Predicate, bindings, bindingCount);
        var obj = ResolveMinusExistsTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength,
            SlotTermPosition.Object, bindings, bindingCount);

        // Query the store
        var results = _graphContext != null
            ? _store!.QueryCurrent(subject, predicate, obj, _graphContext.AsSpan())
            : _store!.QueryCurrent(subject, predicate, obj);

        try
        {
            while (results.MoveNext())
            {
                var triple = results.Current;

                // Save binding count to restore on backtrack
                int savedBindingCount = bindingCount;

                // Add bindings from this match for unbound variables
                AddMinusExistsBindings(slot, triple, bindings, ref bindingCount);

                // Recursively try remaining patterns
                if (EvaluateMinusJoinWithExists(patterns, patternIndices, patternCount, currentPattern + 1, bindings, ref bindingCount))
                    return true; // Found a valid MINUS solution that excludes

                // Backtrack
                bindingCount = savedBindingCount;
            }
        }
        finally
        {
            results.Dispose();
        }

        return false;
    }

    /// <summary>
    /// Resolve a term for MINUS evaluation using outer bindings and MINUS-local bindings.
    /// </summary>
    private ReadOnlySpan<char> ResolveMinusExistsTerm(TermType type, int start, int length,
        SlotTermPosition pos, ExistsBinding[] minusBindings, int bindingCount)
    {
        if (type == TermType.Variable)
        {
            var varName = _source.Slice(start, length);
            var varHash = ComputeVariableHash(varName);

            // First check MINUS-local bindings
            for (int i = 0; i < bindingCount; i++)
            {
                if (minusBindings[i].VariableHash == varHash)
                    return minusBindings[i].Value.AsSpan();
            }

            // Then check outer query bindings
            var outerIdx = _bindingTable.FindBinding(varName);
            if (outerIdx >= 0)
                return _bindingTable.GetString(outerIdx);

            // Unbound - return empty for wildcard query
            return ReadOnlySpan<char>.Empty;
        }

        return ResolveSlotTerm(type, start, length, pos);
    }

    /// <summary>
    /// Add bindings from a matched triple to MINUS binding set.
    /// </summary>
    private void AddMinusExistsBindings(PatternSlot slot, in ResolvedTemporalQuad triple,
        ExistsBinding[] bindings, ref int count)
    {
        // Subject
        if (slot.SubjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.SubjectStart, slot.SubjectLength);
            var varHash = ComputeVariableHash(varName);

            // Only add if not already bound (in MINUS or outer)
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Subject.ToString() };
            }
        }

        // Predicate
        if (slot.PredicateType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.PredicateStart, slot.PredicateLength);
            var varHash = ComputeVariableHash(varName);

            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Predicate.ToString() };
            }
        }

        // Object
        if (slot.ObjectType == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(slot.ObjectStart, slot.ObjectLength);
            var varHash = ComputeVariableHash(varName);

            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Object.ToString() };
            }
        }
    }

    /// <summary>
    /// Evaluate EXISTS filters inside MINUS with combined outer + MINUS bindings.
    /// Returns true if all EXISTS filters pass (for NOT EXISTS, no match should exist).
    /// </summary>
    private bool EvaluateMinusExistsFilters(ExistsBinding[] minusBindings, int minusBindingCount)
    {
        if (_buffer == null || !_buffer.HasMinusExists)
            return true;

        for (int i = 0; i < _buffer.MinusExistsCount; i++)
        {
            var existsFilter = _buffer.GetMinusExistsFilter(i);
            if (existsFilter.PatternCount == 0)
                continue;

            // Evaluate EXISTS patterns with combined bindings
            bool matches = EvaluateMinusExistsPatterns(existsFilter, minusBindings, minusBindingCount);

            // For NOT EXISTS: should NOT match
            // For EXISTS: should match
            if (existsFilter.Negated)
            {
                if (matches) return false; // NOT EXISTS failed - found a match
            }
            else
            {
                if (!matches) return false; // EXISTS failed - no match
            }
        }

        return true;
    }

    /// <summary>
    /// Check if EXISTS patterns match with combined outer + MINUS bindings.
    /// </summary>
    private bool EvaluateMinusExistsPatterns(ExistsFilter existsFilter, ExistsBinding[] minusBindings, int minusBindingCount)
    {
        if (_store == null || existsFilter.PatternCount == 0)
            return false;

        // Collect pattern indices (use inline storage since ExistsFilter has max 4 patterns)
        int patternCount = existsFilter.PatternCount;
        if (patternCount == 0)
            return false;

        // Use nested-loop join for EXISTS patterns
        var existsBindings = new ExistsBinding[16];
        int existsBindingCount = 0;

        // Copy MINUS bindings as starting point for EXISTS evaluation
        for (int i = 0; i < minusBindingCount && i < existsBindings.Length; i++)
        {
            existsBindings[existsBindingCount++] = minusBindings[i];
        }

        return EvaluateMinusExistsJoin(existsFilter, 0, existsBindings, ref existsBindingCount);
    }

    /// <summary>
    /// Recursive join for EXISTS patterns inside MINUS.
    /// </summary>
    private bool EvaluateMinusExistsJoin(ExistsFilter existsFilter, int currentPattern,
        ExistsBinding[] bindings, ref int bindingCount)
    {
        if (currentPattern >= existsFilter.PatternCount)
            return true; // All patterns matched

        var pattern = existsFilter.GetPattern(currentPattern);

        // Resolve terms using outer + MINUS + EXISTS bindings
        var subject = ResolveMinusExistsFilterTerm(pattern.Subject, bindings, bindingCount);
        var predicate = ResolveMinusExistsFilterTerm(pattern.Predicate, bindings, bindingCount);
        var obj = ResolveMinusExistsFilterTerm(pattern.Object, bindings, bindingCount);

        var results = _store!.QueryCurrent(subject, predicate, obj);
        try
        {
            while (results.MoveNext())
            {
                var triple = results.Current;
                int savedBindingCount = bindingCount;

                // Add bindings for unbound variables
                AddMinusExistsFilterBindings(pattern, triple, bindings, ref bindingCount);

                // Recursively try remaining patterns
                if (EvaluateMinusExistsJoin(existsFilter, currentPattern + 1, bindings, ref bindingCount))
                    return true;

                bindingCount = savedBindingCount;
            }
        }
        finally
        {
            results.Dispose();
        }

        return false;
    }

    /// <summary>
    /// Resolve term for EXISTS filter inside MINUS using combined bindings.
    /// </summary>
    private ReadOnlySpan<char> ResolveMinusExistsFilterTerm(Term term, ExistsBinding[] bindings, int bindingCount)
    {
        if (term.Type == TermType.Variable)
        {
            var varName = _source.Slice(term.Start, term.Length);
            var varHash = ComputeVariableHash(varName);

            // Check combined bindings (MINUS + EXISTS)
            for (int i = 0; i < bindingCount; i++)
            {
                if (bindings[i].VariableHash == varHash)
                    return bindings[i].Value.AsSpan();
            }

            // Check outer bindings
            var outerIdx = _bindingTable.FindBinding(varName);
            if (outerIdx >= 0)
                return _bindingTable.GetString(outerIdx);

            return ReadOnlySpan<char>.Empty;
        }

        // Non-variable - expand if needed
        var termSpan = _source.Slice(term.Start, term.Length);

        // Handle 'a' shorthand
        if (termSpan.Length == 1 && termSpan[0] == 'a')
            return SyntheticTermHelper.RdfType.AsSpan();

        // Handle prefixed names
        if (_buffer?.Prefixes != null && termSpan.Length > 0 && termSpan[0] != '<' && termSpan[0] != '"')
        {
            var colonIdx = termSpan.IndexOf(':');
            if (colonIdx >= 0)
            {
                var prefix = termSpan.Slice(0, colonIdx + 1);
                var localName = termSpan.Slice(colonIdx + 1);

                foreach (var mapping in _buffer.Prefixes)
                {
                    var mappingPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);
                    if (prefix.SequenceEqual(mappingPrefix))
                    {
                        var iriBase = _source.Slice(mapping.IriStart, mapping.IriLength);
                        // Strip angle brackets and build expanded IRI
                        var iriContent = iriBase;
                        if (iriContent.Length >= 2 && iriContent[0] == '<' && iriContent[^1] == '>')
                            iriContent = iriContent.Slice(1, iriContent.Length - 2);
                        // Store in _expandedSubject and return span
                        _expandedSubject = $"<{iriContent.ToString()}{localName.ToString()}>";
                        return _expandedSubject.AsSpan();
                    }
                }
            }
        }

        return termSpan;
    }

    /// <summary>
    /// Add bindings from EXISTS filter pattern match.
    /// </summary>
    private void AddMinusExistsFilterBindings(TriplePattern pattern, in ResolvedTemporalQuad triple,
        ExistsBinding[] bindings, ref int count)
    {
        if (pattern.Subject.Type == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(pattern.Subject.Start, pattern.Subject.Length);
            var varHash = ComputeVariableHash(varName);
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Subject.ToString() };
            }
        }

        if (pattern.Predicate.Type == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
            var varHash = ComputeVariableHash(varName);
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Predicate.ToString() };
            }
        }

        if (pattern.Object.Type == TermType.Variable && count < bindings.Length)
        {
            var varName = _source.Slice(pattern.Object.Start, pattern.Object.Length);
            var varHash = ComputeVariableHash(varName);
            if (!IsExistsVariableBound(varHash, bindings, count) && _bindingTable.FindBinding(varName) < 0)
            {
                bindings[count++] = new ExistsBinding { VariableHash = varHash, Value = triple.Object.ToString() };
            }
        }
    }

    /// <summary>
    /// Check if a MINUS solution has domain overlap with the outer query result.
    /// Domain overlap means all shared variables have the same values.
    /// </summary>
    private bool HasMinusDomainOverlap(ExistsBinding[] minusBindings, int minusBindingCount)
    {
        // Check each MINUS binding against outer bindings
        for (int i = 0; i < minusBindingCount; i++)
        {
            var varHash = minusBindings[i].VariableHash;
            var minusValue = minusBindings[i].Value;

            // Check if this variable is bound in outer query by hash
            var outerIdx = _bindingTable.FindBindingByHash(varHash);
            if (outerIdx >= 0)
            {
                // Shared variable - check values match
                var outerValue = _bindingTable.GetString(outerIdx);
                if (!minusValue.AsSpan().Equals(outerValue, StringComparison.Ordinal))
                {
                    // Values don't match - no domain overlap for this solution
                    return false;
                }
            }
        }

        // All shared variables have matching values - domain overlap exists
        return true;
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
    /// Binding for variables within EXISTS block during join evaluation.
    /// </summary>
    private struct ExistsBinding
    {
        public int VariableHash;
        public string Value;
    }

    /// <summary>
    /// Info for a pattern within EXISTS block, including its graph context.
    /// </summary>
    private struct ExistsPatternInfo
    {
        public int PatternIndex;
        public string? GraphContext; // The resolved graph IRI, or null if variable unbound
        public bool HasGraphClause;  // true if pattern is inside GRAPH clause
    }

    /// <summary>
    /// State for iterative EXISTS join evaluation.
    /// Replaces recursive call stack to avoid stack overflow (ADR-003).
    /// Uses materialized results (List) instead of ref struct enumerator.
    /// </summary>
    private struct ExistsJoinState
    {
        public int PatternIndex;           // Which pattern we're processing (0 to patternCount-1)
        public int BindingCountBefore;     // Binding count before this pattern (for backtrack)
        public List<MaterializedTriple>? Results;  // Materialized query results
        public int ResultIndex;            // Current position in Results (-1 = not started)
    }

    /// <summary>
    /// Materialized triple for EXISTS evaluation.
    /// Stores resolved strings to avoid ref struct lifetime issues.
    /// </summary>
    private struct MaterializedTriple
    {
        public string Subject;
        public string Predicate;
        public string Object;
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
