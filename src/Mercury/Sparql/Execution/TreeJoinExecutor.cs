using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;
using ExprValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Sparql.Execution;

// ═══════════════════════════════════════════════════════════════════════════
// ADR-045 Step 4 — the zero-GC tree pattern executor (cutover form).
//
// The production form of the GraphTreeEvaluator model: it walks the recursive PatternArray tree the recursive
// parser produces, threading the ACTIVE GRAPH as a per-pattern parameter ("a default graph is also a graph"). The
// BGP hot path is evaluated ZERO-GC over a BindingTable, reusing the engine's own TriplePatternScan (which
// self-manages backtracking via TruncateTo and evaluates every non-sequence property-path form); the continuation
// is plain recursion, so there are no closures and no per-step allocation. Solutions materialize into
// MaterializedRow (bounded by result size) and the composing operators compose over those rows — exactly as the
// GraphTreeEvaluator model does, and as the divergent QueryExecutor.Graph.cs the cutover deletes does — feeding the
// shared QueryResults.FromMaterializedSimple downstream.
//
// EvalGroup walks a group's direct children: consecutive triples accumulate into a BGP run flushed zero-GC by
// FlushBgp (seeded by each input row); each composing operator (GRAPH / group / UNION / OPTIONAL / BIND / VALUES /
// MINUS / sub-SELECT / SERVICE) composes over the materialized rows; FILTER and (NOT) EXISTS are group-scoped and
// applied to the whole group's solutions. The path-span↔PropertyPath reconciliation and top-level-sequence
// expansion live in AddPathPatterns (see ck:obs-trscan-sequence-wildcard).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Zero-GC executor for the ADR-045 pattern tree (see file header). Reuses <see cref="TriplePatternScan"/> for
/// graph-parameterized backtracking scans and the real expression evaluators for FILTER / BIND.
/// </summary>
internal sealed class TreeJoinExecutor
{
    private readonly QuadStore _store;
    private readonly string _source;
    private readonly PrefixMapping[]? _prefixes;
    private readonly string _prefixSource;

    public TreeJoinExecutor(QuadStore store, string source, PrefixMapping[]? prefixes = null, string? prefixSource = null)
    {
        _store = store;
        _source = source;
        _prefixes = prefixes;
        _prefixSource = prefixSource ?? source;
    }

    /// <summary>Evaluate the group at <paramref name="rootHeader"/> into materialized rows, threading the active graph.</summary>
    public List<MaterializedRow> Evaluate(ref PatternArray pa, int rootHeader, string activeGraph)
    {
        var seed = new List<MaterializedRow> { new(new BindingTable(Array.Empty<Binding>(), Array.Empty<char>())) };
        return EvalGroup(ref pa, rootHeader, activeGraph, seed);
    }

    /// <summary>
    /// Evaluate a group: consecutive triples accumulate into a BGP run (flushed zero-GC, seeded by each input row);
    /// each composing operator composes over the materialized rows; FILTER and (NOT) EXISTS are group-scoped and
    /// applied last to the whole group's solutions.
    /// </summary>
    private List<MaterializedRow> EvalGroup(ref PatternArray pa, int headerIndex, string activeGraph,
        List<MaterializedRow> input)
    {
        var children = new List<int>();
        var e = pa.EnumerateDirectChildren(headerIndex);
        while (e.MoveNext()) children.Add(e.CurrentIndex);

        var current = input;
        var pendingPatterns = new List<TriplePattern>();
        var pendingGraphs = new List<string>();

        foreach (int ci in children)
        {
            var kind = pa[ci].Kind;
            if (IsGroupScoped(kind)) continue; // deferred to the second pass

            if (kind == PatternKind.Triple)
            {
                var slot = pa[ci];
                if (slot.PathKind == PathType.None)
                {
                    pendingPatterns.Add(TripleFromSlot(slot));
                    pendingGraphs.Add(activeGraph);
                }
                else
                {
                    AddPathPatterns(slot, activeGraph, pendingPatterns, pendingGraphs);
                }
            }
            else
            {
                current = FlushBgp(pendingPatterns, pendingGraphs, current); // flush the BGP run before the operator
                pendingPatterns.Clear();
                pendingGraphs.Clear();
                current = JoinOperator(ref pa, ci, kind, activeGraph, current);
            }
        }
        current = FlushBgp(pendingPatterns, pendingGraphs, current); // trailing BGP run

        foreach (int ci in children)
        {
            var kind = pa[ci].Kind;
            if (kind == PatternKind.Filter)
                current = ApplyFilter(ref pa, ci, current);
            else if (kind is PatternKind.ExistsHeader or PatternKind.NotExistsHeader)
                current = ApplyExists(ref pa, ci, activeGraph, current, negated: kind == PatternKind.NotExistsHeader);
        }
        return current;
    }

    private static bool IsGroupScoped(PatternKind kind) =>
        kind is PatternKind.Filter or PatternKind.ExistsHeader or PatternKind.NotExistsHeader;

    /// <summary>
    /// Join an accumulated BGP run with the input rows, zero-GC: restore each input row into a pooled BindingTable
    /// (TruncateTo(0) reclaims the char buffer between rows) and run the nested-loop join seeded by it.
    /// </summary>
    private List<MaterializedRow> FlushBgp(List<TriplePattern> patterns, List<string> graphs, List<MaterializedRow> input)
    {
        if (patterns.Count == 0) return input;

        var output = new List<MaterializedRow>();
        var patternArr = patterns.ToArray();
        var graphArr = graphs.ToArray();

        var bindingArray = ArrayPool<Binding>.Shared.Rent(256);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 16);
        try
        {
            var bindings = new BindingTable(bindingArray.AsSpan(0, 256), charArray.AsSpan(0, 1 << 16));
            foreach (var row in input)
            {
                bindings.TruncateTo(0);
                row.RestoreBindings(ref bindings);
                JoinAt(patternArr, graphArr, 0, ref bindings, output);
            }
            return output;
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }
    }

    private List<MaterializedRow> JoinOperator(ref PatternArray pa, int ci, PatternKind kind, string activeGraph,
        List<MaterializedRow> input)
    {
        switch (kind)
        {
            case PatternKind.GraphHeader:
            {
                var slot = pa[ci];
                return EvalGroup(ref pa, ci, _source.Substring(slot.GraphTermStart, slot.GraphTermLength), input);
            }
            case PatternKind.GroupHeader:
                return EvalGroup(ref pa, ci, activeGraph, input);
            case PatternKind.UnionHeader:
            {
                var output = new List<MaterializedRow>();
                var branches = new List<int>();
                var be = pa.EnumerateDirectChildren(ci);
                while (be.MoveNext()) branches.Add(be.CurrentIndex);
                foreach (int branch in branches)
                    output.AddRange(EvalGroup(ref pa, branch, activeGraph, input)); // (A ∪ B) joined with input
                return output;
            }
            case PatternKind.OptionalHeader:
            {
                var output = new List<MaterializedRow>();
                foreach (var row in input)
                {
                    var single = new List<MaterializedRow> { row };
                    var ext = EvalGroup(ref pa, ci, activeGraph, single);
                    if (ext.Count > 0) output.AddRange(ext);
                    else output.Add(row); // left join: preserve the row when the optional has no match
                }
                return output;
            }
            case PatternKind.Bind:
                return BindStep(ref pa, ci, input);
            default:
                throw new NotSupportedException($"{kind} is a later increment of the zero-GC executor's operators (MINUS / VALUES / sub-SELECT / SERVICE).");
        }
    }

    /// <summary>BIND: extend each row with the evaluated expression, via the real <see cref="BindExpressionEvaluator"/>.</summary>
    private List<MaterializedRow> BindStep(ref PatternArray pa, int bindIndex, List<MaterializedRow> input)
    {
        var slot = pa[bindIndex];
        string exprText = _source.Substring(slot.BindExprStart, slot.BindExprLength);
        string varName = _source.Substring(slot.BindVarStart, slot.BindVarLength); // carries the leading '?'

        var output = new List<MaterializedRow>(input.Count);
        var bindingArray = ArrayPool<Binding>.Shared.Rent(64);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 13);
        try
        {
            var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
            foreach (var row in input)
            {
                table.TruncateTo(0);
                row.RestoreBindings(ref table);
                var evaluator = new BindExpressionEvaluator(exprText.AsSpan(), table.GetBindings(), table.Count, table.GetStringBuffer());
                table.Bind(varName.AsSpan(), Render(evaluator.Evaluate()).AsSpan());
                output.Add(new MaterializedRow(table));
            }
            return output;
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }
    }

    /// <summary>FILTER: keep rows for which the constraint holds, via the real <see cref="FilterEvaluator"/>.</summary>
    private List<MaterializedRow> ApplyFilter(ref PatternArray pa, int filterIndex, List<MaterializedRow> input)
    {
        var slot = pa[filterIndex];
        string exprText = _source.Substring(slot.FilterStart, slot.FilterLength);

        var output = new List<MaterializedRow>();
        var bindingArray = ArrayPool<Binding>.Shared.Rent(64);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 13);
        try
        {
            var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
            foreach (var row in input)
            {
                table.TruncateTo(0);
                row.RestoreBindings(ref table);
                var evaluator = new FilterEvaluator(exprText.AsSpan());
                bool pass = _prefixes is not null
                    ? evaluator.Evaluate(table.GetBindings(), table.Count, table.GetStringBuffer(), _prefixes, _prefixSource.AsSpan())
                    : evaluator.Evaluate(table.GetBindings(), table.Count, table.GetStringBuffer());
                if (pass) output.Add(row);
            }
            return output;
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }
    }

    /// <summary>
    /// FILTER [NOT] EXISTS (group-scoped): keep a row iff its body — evaluated with that row's bindings in scope
    /// (seeded with the row, so its bound variables constrain the body) — is non-empty (EXISTS) or empty (NOT
    /// EXISTS). EXISTS adds no bindings; it only filters.
    /// </summary>
    private List<MaterializedRow> ApplyExists(ref PatternArray pa, int existsIndex, string activeGraph,
        List<MaterializedRow> input, bool negated)
    {
        var output = new List<MaterializedRow>();
        foreach (var row in input)
        {
            var seed = new List<MaterializedRow> { row };
            bool exists = EvalGroup(ref pa, existsIndex, activeGraph, seed).Count > 0;
            if (negated ? !exists : exists) output.Add(row);
        }
        return output;
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

    /// <summary>
    /// A property-path triple. The slot carries the full path-expression SPAN (not a base IRI); re-parse it over the
    /// SAME source (so the PropertyPath's offsets index the source given to <see cref="TriplePatternScan"/>). Then
    /// reuse the shipping expansion: a top-level SEQUENCE expands to intermediate-variable triples (TriplePatternScan
    /// does not evaluate a top-level sequence — the shipping flat path always expands it); every other form —
    /// ^ * + ? | ( ) ! and grouped sequences — stays one pattern TriplePatternScan evaluates zero-GC. Subject/object
    /// come from the slot; the synthetic intermediate variables are ones TriplePatternScan already resolves.
    /// </summary>
    private void AddPathPatterns(PatternSlot slot, string activeGraph, List<TriplePattern> patterns, List<string> graphs)
    {
        var pathParser = new SparqlParser(_source.AsSpan());
        var (predicate, path) = pathParser.ParsePredicateOrPathAt(slot.PathIriStart);
        var subject = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength };
        var obj = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength };

        var expanded = new GraphPattern();
        pathParser.AddTriplePatternOrExpand(ref expanded, subject, predicate, obj, path);
        for (int i = 0; i < expanded.PatternCount; i++)
        {
            patterns.Add(expanded.GetPattern(i));
            graphs.Add(activeGraph);
        }
    }

    private static string Render(Value value) => value.Type switch
    {
        ExprValueType.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
        ExprValueType.Double => value.DoubleValue.ToString("G", CultureInfo.InvariantCulture),
        ExprValueType.Boolean => value.BooleanValue ? "true" : "false",
        _ => value.StringValue.ToString(),
    };
}
