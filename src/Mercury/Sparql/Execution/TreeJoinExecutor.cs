using System;
using System.Buffers;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

// ═══════════════════════════════════════════════════════════════════════════
// ADR-045 Step 4 — the zero-GC tree pattern executor (cutover foundation).
//
// This is the production form of the GraphTreeEvaluator model: it walks the recursive PatternArray tree the
// recursive parser produces, threading the ACTIVE GRAPH as a per-pattern parameter ("a default graph is also a
// graph"), and evaluates the join ZERO-GC over a BindingTable — reusing the engine's own TriplePatternScan
// (which self-manages BindingTable backtracking via TruncateTo, and already evaluates property paths). The
// continuation is plain recursion: a scan is a ref-struct stack local per frame and `ref BindingTable` is threaded
// through, so there are no closures and no per-step allocation. Solutions are materialized into MaterializedRow at
// the leaf (bounded by the result size), to be fed downstream into QueryResults.FromMaterializedSimple — the same
// shared modifier/aggregation layer the default path uses.
//
// FOUNDATION (this increment): BGP runs (plain triples) joined zero-GC, with GRAPH and nested groups threading the
// active graph per pattern. This is the divergent case the cutover deletes — `GRAPH <g> { BGP }` evaluated through
// the same path as the default graph, by construction.
//
// FOLLOWS (subsequent increments toward the cutover): property paths (reconcile the tree's path-span with
// TriplePatternScan's base-IRI+Type form), then the composing operators (UNION / OPTIONAL / MINUS / (NOT) EXISTS /
// FILTER / BIND / VALUES / sub-SELECT / SERVICE) over materialized rows — as the GraphTreeEvaluator model does —
// then wiring into QueryExecutor to replace QueryExecutor.Graph.cs, and the BenchmarkDotNet allocation gate.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Zero-GC executor for the BGP core of the ADR-045 pattern tree (see file header). Reuses
/// <see cref="TriplePatternScan"/> for graph-parameterized, backtracking scans.
/// </summary>
internal sealed class TreeJoinExecutor
{
    private readonly QuadStore _store;
    private readonly string _source;

    public TreeJoinExecutor(QuadStore store, string source)
    {
        _store = store;
        _source = source;
    }

    /// <summary>
    /// Evaluate the BGP-shaped group at <paramref name="rootHeader"/> into materialized rows, threading the active
    /// graph per pattern. The hot path (the nested-loop scan/join) is zero-GC; only the materialized result rows
    /// allocate. The foundation rejects non-BGP nodes — composing operators are a later increment.
    /// </summary>
    public List<MaterializedRow> Evaluate(ref PatternArray pa, int rootHeader, string activeGraph)
    {
        var patterns = new List<TriplePattern>();
        var graphs = new List<string>();
        Flatten(ref pa, rootHeader, activeGraph, patterns, graphs);

        var results = new List<MaterializedRow>();

        // Pool the binding buffers so the hot path allocates nothing beyond the bounded materialized result rows.
        const int maxBindings = 256;
        const int charCapacity = 1 << 16;
        var bindingArray = ArrayPool<Binding>.Shared.Rent(maxBindings);
        var charArray = ArrayPool<char>.Shared.Rent(charCapacity);
        try
        {
            var bindings = new BindingTable(bindingArray.AsSpan(0, maxBindings), charArray.AsSpan(0, charCapacity));
            JoinAt(patterns.ToArray(), graphs.ToArray(), 0, ref bindings, results);
            return results;
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }
    }

    /// <summary>
    /// Flatten a BGP-shaped subtree into a per-pattern (triple, active-graph) sequence: a triple takes the current
    /// active graph; a GRAPH header rebinds it for its subtree; a nested group threads it through unchanged.
    /// </summary>
    private void Flatten(ref PatternArray pa, int headerIndex, string activeGraph,
        List<TriplePattern> patterns, List<string> graphs)
    {
        var childIndices = new List<int>();
        var e = pa.EnumerateDirectChildren(headerIndex);
        while (e.MoveNext()) childIndices.Add(e.CurrentIndex);

        foreach (int ci in childIndices)
        {
            var slot = pa[ci];
            switch (slot.Kind)
            {
                case PatternKind.Triple:
                    if (slot.PathKind != PathType.None)
                        throw new NotSupportedException("Property paths are a later increment of the zero-GC tree executor (reconcile the tree's path-span with TriplePatternScan's base-IRI form).");
                    patterns.Add(TripleFromSlot(slot));
                    graphs.Add(activeGraph);
                    break;
                case PatternKind.GraphHeader:
                    Flatten(ref pa, ci, _source.Substring(slot.GraphTermStart, slot.GraphTermLength), patterns, graphs);
                    break;
                case PatternKind.GroupHeader:
                    Flatten(ref pa, ci, activeGraph, patterns, graphs);
                    break;
                default:
                    throw new NotSupportedException($"{slot.Kind} is a composing operator — a later increment of the zero-GC tree executor (the foundation handles BGP + GRAPH + nested groups).");
            }
        }
    }

    /// <summary>
    /// Recursive nested-loop join (the continuation is the recursion). The scan is a ref-struct stack local; it
    /// reads the bound variables for its constraints, self-truncates the BindingTable to its start count on each
    /// MoveNext, and binds the match — so there are no closures and no per-step allocation. A complete solution
    /// (all patterns matched) is materialized.
    /// </summary>
    private void JoinAt(TriplePattern[] patterns, string[] graphs, int index, ref BindingTable bindings,
        List<MaterializedRow> results)
    {
        if (index == patterns.Length)
        {
            results.Add(new MaterializedRow(bindings));
            return;
        }

        var scan = new TriplePatternScan(_store, _source.AsSpan(), patterns[index], bindings, graphs[index].AsSpan());
        try
        {
            while (scan.MoveNext(ref bindings))
                JoinAt(patterns, graphs, index + 1, ref bindings, results);
        }
        finally
        {
            scan.Dispose();
        }
    }

    private static TriplePattern TripleFromSlot(PatternSlot slot) => new()
    {
        Subject = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength },
        Predicate = new Term { Type = slot.PredicateType, Start = slot.PredicateStart, Length = slot.PredicateLength },
        Object = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength },
    };
}
