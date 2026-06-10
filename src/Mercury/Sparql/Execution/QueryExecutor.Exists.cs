using System.Collections.Generic;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// FILTER (NOT) EXISTS evaluation — applied to materialized rows after pattern matching, on both the default and
/// (since the ADR-045 cutover) the GRAPH path. Extracted from the former QueryExecutor.Graph.cs, whose divergent
/// GRAPH-execution methods were deleted once TreeJoinExecutor became the live GRAPH path; these shared EXISTS
/// helpers were the only live part and remain here.
/// </summary>
internal partial class QueryExecutor
{
    // Storage for expanded prefix terms in EXISTS evaluation (prevents span-over-temporary)
    private string? _existsExpandedSubject;
    private string? _existsExpandedPredicate;
    private string? _existsExpandedObject;

    /// <summary>
    /// Filter materialized rows by evaluating EXISTS/NOT EXISTS filters.
    /// Returns a new list containing only rows that pass all EXISTS filters.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<MaterializedRow> FilterResultsByExists(List<MaterializedRow> rows, string? graphContext)
    {
        if (!_buffer.HasExists || _buffer.ExistsFilterCount == 0)
            return rows;

        var filtered = new List<MaterializedRow>();
        var patterns = _buffer.GetPatterns();
        var bindings = new Binding[16];
        var bindingTable = new BindingTable(bindings, _stringBuffer);

        foreach (var row in rows)
        {
            // Load row bindings into binding table
            bindingTable.Clear();
            for (int i = 0; i < row.BindingCount; i++)
            {
                bindingTable.BindWithHash(row.GetHash(i), row.GetValue(i));
            }

            // Evaluate all EXISTS/NOT EXISTS filters
            if (EvaluateExistsFiltersForRow(patterns, bindingTable, graphContext))
            {
                filtered.Add(row);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Evaluate all EXISTS/NOT EXISTS filters for a single row.
    /// Returns true if all filters pass.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateExistsFiltersForRow(PatternArray patterns, BindingTable bindingTable, string? graphContext)
    {
        int existsSeen = 0;

        for (int i = 0; i < _buffer.PatternCount && existsSeen < _buffer.ExistsFilterCount; i++)
        {
            var kind = patterns[i].Kind;
            if (kind != PatternKind.ExistsHeader && kind != PatternKind.NotExistsHeader)
                continue;

            existsSeen++;
            var slot = patterns[i];
            var matches = EvaluateExistsPatternForRow(slot, patterns, bindingTable, graphContext);

            // EXISTS: must match at least once
            // NOT EXISTS: must not match at all
            if (slot.IsNegatedExists)
            {
                if (matches) return false; // NOT EXISTS failed
            }
            else
            {
                if (!matches) return false; // EXISTS failed
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluate a single EXISTS pattern for a row.
    /// Uses iterative nested-loop join with materialization (ADR-003 compliant).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateExistsPatternForRow(PatternSlot existsSlot, PatternArray patterns,
        BindingTable bindingTable, string? graphContext)
    {
        if (existsSlot.ExistsChildCount == 0)
            return false;

        int existsStart = existsSlot.ExistsChildStart;
        int existsEnd = existsStart + existsSlot.ExistsChildCount;

        // Count triple patterns
        int tripleCount = 0;
        for (int p = existsStart; p < existsEnd; p++)
        {
            var kind = patterns[p].Kind;
            if (kind == PatternKind.Triple)
                tripleCount++;
            else if (kind == PatternKind.GraphHeader)
            {
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

        // For single pattern, use simple evaluation
        if (tripleCount == 1)
        {
            return EvaluateSingleExistsPattern(existsStart, existsEnd, patterns, bindingTable, graphContext);
        }

        // For multiple patterns, use iterative nested-loop join
        return EvaluateMultiExistsPattern(existsStart, existsEnd, patterns, bindingTable, graphContext);
    }

    /// <summary>
    /// Evaluate a single EXISTS pattern (one triple pattern).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateSingleExistsPattern(int existsStart, int existsEnd, PatternArray patterns,
        BindingTable bindingTable, string? graphContext)
    {
        for (int p = existsStart; p < existsEnd; p++)
        {
            var kind = patterns[p].Kind;
            PatternSlot slot;
            string? patternGraphContext = graphContext;

            if (kind == PatternKind.Triple)
            {
                slot = patterns[p];
            }
            else if (kind == PatternKind.GraphHeader)
            {
                var graphSlot = patterns[p];
                patternGraphContext = ResolveGraphContextForExists(graphSlot, bindingTable);
                if (patternGraphContext == null && graphSlot.GraphTermType == TermType.Variable)
                    continue; // Variable graph unbound

                int childEnd = graphSlot.ChildStartIndex + graphSlot.ChildCount;
                for (int c = graphSlot.ChildStartIndex; c < childEnd; c++)
                {
                    if (patterns[c].Kind == PatternKind.Triple)
                    {
                        slot = patterns[c];
                        return QueryExistsPattern(slot, bindingTable, patternGraphContext);
                    }
                }
                continue;
            }
            else
            {
                continue;
            }

            return QueryExistsPattern(slot, bindingTable, patternGraphContext);
        }

        return false;
    }

    /// <summary>
    /// Evaluate multiple EXISTS patterns using iterative nested-loop join.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateMultiExistsPattern(int existsStart, int existsEnd, PatternArray patterns,
        BindingTable bindingTable, string? graphContext)
    {
        // Collect pattern info with graph context
        var patternInfos = new List<(int PatternIndex, string? GraphContext)>();

        for (int p = existsStart; p < existsEnd; p++)
        {
            var kind = patterns[p].Kind;
            if (kind == PatternKind.Triple)
            {
                patternInfos.Add((p, graphContext));
            }
            else if (kind == PatternKind.GraphHeader)
            {
                var graphSlot = patterns[p];
                var patternGraphContext = ResolveGraphContextForExists(graphSlot, bindingTable);

                int childEnd = graphSlot.ChildStartIndex + graphSlot.ChildCount;
                for (int c = graphSlot.ChildStartIndex; c < childEnd; c++)
                {
                    if (patterns[c].Kind == PatternKind.Triple)
                    {
                        patternInfos.Add((c, patternGraphContext));
                    }
                }
                p = childEnd - 1; // Skip past children
            }
        }

        if (patternInfos.Count == 0)
            return false;

        // Use iterative nested-loop join with explicit stack
        return EvaluateExistsJoinIterative(patterns, patternInfos, bindingTable);
    }

    /// <summary>
    /// Iterative nested-loop join for EXISTS evaluation.
    /// Uses heap-allocated state to avoid stack overflow (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool EvaluateExistsJoinIterative(PatternArray patterns,
        List<(int PatternIndex, string? GraphContext)> patternInfos, BindingTable outerBindings)
    {
        int patternCount = patternInfos.Count;
        if (patternCount == 0)
            return true;

        // State for each level: materialized results and current index
        var stateStack = new (List<(string S, string P, string O)>? Results, int Index, Dictionary<int, string>? LocalBindings)[patternCount];
        int stackTop = 0;

        // Initialize first level
        stateStack[0] = (null, -1, null);
        stackTop = 1;

        while (stackTop > 0)
        {
            ref var state = ref stateStack[stackTop - 1];

            // Materialize results if not yet done
            if (state.Results == null)
            {
                var (patternIdx, graphCtx) = patternInfos[stackTop - 1];
                // Note: graphCtx being null is normal for default graph queries.
                // Only skip if there's a specific issue with graph variable resolution.

                var slot = patterns[patternIdx];
                state.Results = MaterializeExistsQueryForRow(slot, outerBindings, state.LocalBindings, graphCtx);
                state.Index = -1;
            }

            // Advance to next result
            state.Index++;

            if (state.Index < state.Results.Count)
            {
                var (s, p, o) = state.Results[state.Index];

                // Add local bindings from this match
                var slot = patterns[patternInfos[stackTop - 1].PatternIndex];
                var localBindings = state.LocalBindings ?? new Dictionary<int, string>();

                AddLocalBindingsFromMatch(slot, s, p, o, outerBindings, localBindings);
                state.LocalBindings = localBindings;

                // Check if all patterns matched
                if (stackTop >= patternCount)
                {
                    return true; // Success!
                }

                // Push next level
                stateStack[stackTop] = (null, -1, new Dictionary<int, string>(localBindings));
                stackTop++;
            }
            else
            {
                // Backtrack
                stackTop--;
            }
        }

        return false;
    }

    /// <summary>
    /// Materialize EXISTS query results for a single pattern.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<(string S, string P, string O)> MaterializeExistsQueryForRow(PatternSlot slot,
        BindingTable outerBindings, Dictionary<int, string>? localBindings, string? graphContext)
    {
        var results = new List<(string S, string P, string O)>();

        var subject = ResolveExistsTermForRow(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, outerBindings, localBindings, ExistsTermPosition.Subject);
        var predicate = ResolveExistsTermForRow(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, outerBindings, localBindings, ExistsTermPosition.Predicate);
        var obj = ResolveExistsTermForRow(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, outerBindings, localBindings, ExistsTermPosition.Object);

        var enumerator = graphContext != null
            ? _store.QueryCurrent(subject, predicate, obj, graphContext.AsSpan())
            : _store.QueryCurrent(subject, predicate, obj);

        try
        {
            while (enumerator.MoveNext())
            {
                var triple = enumerator.Current;
                results.Add((triple.Subject.ToString(), triple.Predicate.ToString(), triple.Object.ToString()));
            }
        }
        finally
        {
            enumerator.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Term position for EXISTS prefix expansion storage.
    /// </summary>
    private enum ExistsTermPosition { Subject, Predicate, Object }

    /// <summary>
    /// Resolve a term for EXISTS evaluation using outer bindings and local bindings.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private ReadOnlySpan<char> ResolveExistsTermForRow(TermType type, int start, int length,
        BindingTable outerBindings, Dictionary<int, string>? localBindings,
        ExistsTermPosition position = ExistsTermPosition.Subject)
    {
        if (type != TermType.Variable)
        {
            // Constant - return as-is (with prefix expansion if needed)
            var termSpan = _source.AsSpan().Slice(start, length);

            // Handle 'a' shorthand for rdf:type (SPARQL keyword)
            if (termSpan.Length == 1 && termSpan[0] == 'a')
            {
                return SyntheticTermHelper.RdfType.AsSpan();
            }

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
                        var mappedPrefix = _source.AsSpan().Slice(mapping.PrefixStart, mapping.PrefixLength);
                        if (prefix.SequenceEqual(mappedPrefix))
                        {
                            var iriNs = _source.AsSpan().Slice(mapping.IriStart, mapping.IriLength);
                            var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);
                            // Store expanded IRI in position-specific field to keep string alive
                            var expanded = string.Concat(nsWithoutClose, localName, ">");
                            switch (position)
                            {
                                case ExistsTermPosition.Subject:
                                    _existsExpandedSubject = expanded;
                                    return _existsExpandedSubject.AsSpan();
                                case ExistsTermPosition.Predicate:
                                    _existsExpandedPredicate = expanded;
                                    return _existsExpandedPredicate.AsSpan();
                                default:
                                    _existsExpandedObject = expanded;
                                    return _existsExpandedObject.AsSpan();
                            }
                        }
                    }
                }
            }

            return termSpan;
        }

        var varName = _source.AsSpan().Slice(start, length);
        var varHash = ComputeVarHash(varName);

        // Check local bindings first
        if (localBindings != null && localBindings.TryGetValue(varHash, out var localValue))
        {
            return localValue.AsSpan();
        }

        // Check outer bindings
        var idx = outerBindings.FindBinding(varName);
        if (idx >= 0)
        {
            return outerBindings.GetString(idx);
        }

        // Unbound - wildcard
        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// Add bindings from a matched triple to local bindings.
    /// </summary>
    private void AddLocalBindingsFromMatch(PatternSlot slot, string s, string p, string o,
        BindingTable outerBindings, Dictionary<int, string> localBindings)
    {
        if (slot.SubjectType == TermType.Variable)
        {
            var varName = _source.AsSpan().Slice(slot.SubjectStart, slot.SubjectLength);
            var hash = ComputeVarHash(varName);
            if (!localBindings.ContainsKey(hash) && outerBindings.FindBinding(varName) < 0)
            {
                localBindings[hash] = s;
            }
        }

        if (slot.PredicateType == TermType.Variable)
        {
            var varName = _source.AsSpan().Slice(slot.PredicateStart, slot.PredicateLength);
            var hash = ComputeVarHash(varName);
            if (!localBindings.ContainsKey(hash) && outerBindings.FindBinding(varName) < 0)
            {
                localBindings[hash] = p;
            }
        }

        if (slot.ObjectType == TermType.Variable)
        {
            var varName = _source.AsSpan().Slice(slot.ObjectStart, slot.ObjectLength);
            var hash = ComputeVarHash(varName);
            if (!localBindings.ContainsKey(hash) && outerBindings.FindBinding(varName) < 0)
            {
                localBindings[hash] = o;
            }
        }
    }

    private static int ComputeVarHash(ReadOnlySpan<char> varName) => Fnv1a.Hash(varName);

    /// <summary>
    /// Resolve graph context for EXISTS evaluation.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private string? ResolveGraphContextForExists(PatternSlot graphSlot, BindingTable bindingTable)
    {
        if (graphSlot.GraphTermType == TermType.Variable)
        {
            var varName = _source.AsSpan().Slice(graphSlot.GraphTermStart, graphSlot.GraphTermLength);
            var idx = bindingTable.FindBinding(varName);
            if (idx >= 0)
            {
                return bindingTable.GetString(idx).ToString();
            }
            return null; // Unbound variable
        }

        // IRI - expand prefixed names
        var rawIri = _source.AsSpan().Slice(graphSlot.GraphTermStart, graphSlot.GraphTermLength);
        return ExpandPrefixedName(rawIri).ToString();
    }

    /// <summary>
    /// Query store for a single EXISTS pattern and return whether it has any matches.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool QueryExistsPattern(PatternSlot slot, BindingTable bindingTable, string? graphContext)
    {
        var subject = ResolveExistsTermForRow(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, bindingTable, null, ExistsTermPosition.Subject);
        var predicate = ResolveExistsTermForRow(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, bindingTable, null, ExistsTermPosition.Predicate);
        var obj = ResolveExistsTermForRow(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, bindingTable, null, ExistsTermPosition.Object);

        var enumerator = graphContext != null
            ? _store.QueryCurrent(subject, predicate, obj, graphContext.AsSpan())
            : _store.QueryCurrent(subject, predicate, obj);

        try
        {
            return enumerator.MoveNext();
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}
