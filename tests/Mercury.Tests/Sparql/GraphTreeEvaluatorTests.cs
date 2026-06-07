using System;
using System.Collections.Generic;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-045 Step 4 (first evaluator increment) — the uniform tree-walking evaluator, ALONGSIDE.
///
/// This is the executor analog of the Step 2 parser: a single recursive evaluator that walks the
/// <see cref="PatternArray"/> tree the recursive parser produces, threading the ACTIVE GRAPH as one parameter
/// ("a default graph is also a graph"). A GRAPH header rebinds the active graph for its subtree; every other
/// group-like node threads it through unchanged. It reuses the real store scan (<see cref="QuadStore.QueryCurrent"/>),
/// so the proof is grounded in the substrate, not a toy.
///
/// The proof: take the mirror gate's RED cases (which the shipping divergent GRAPH path gets wrong), parse the
/// GRAPH-wrapped query with the recursive parser, walk it with this evaluator, and show the result equals the
/// shipping engine's DEFAULT-graph baseline for the unwrapped query. The divergence is dissolved by construction —
/// there is no GRAPH-only evaluation path to be wrong.
///
/// THIS INCREMENT covers the non-expression spine: BGP scan / GRAPH / nested group / UNION / OPTIONAL (left join) /
/// single-variable VALUES (inline-data join). It proves union, optional and values (3 of the 4 RED cases). BIND
/// and FILTER (which reuse <c>BindExpressionEvaluator</c> / <c>FilterEvaluator</c> over the engine's binding form)
/// are the next evaluator increment. The walker is a correctness MODEL (heap solution bags, not zero-GC); the
/// production cutover reimplements it zero-GC over BindingTable and wires it into <see cref="SparqlEngine"/>.
/// </summary>
public class GraphTreeEvaluatorTests : IDisposable
{
    private const string MirrorGraph = "<urn:test:mirror>";

    private readonly ITestOutputHelper _output;
    private readonly string _testPath;
    private readonly QuadStore _store;

    public GraphTreeEvaluatorTests(ITestOutputHelper output)
    {
        _output = output;
        var tempPath = TempPath.Test("tree-eval");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        (string s, string p, string o)[] data =
        {
            ("<urn:a>", "<urn:p>", "<urn:v1>"),
            ("<urn:a>", "<urn:q>", "\"qa\""),
            ("<urn:b>", "<urn:p>", "<urn:v2>"),
            ("<urn:b>", "<urn:q>", "\"qb\""),
        };
        _store.BeginBatch();
        foreach (var (s, p, o) in data)
        {
            _store.AddCurrentBatched(s, p, o);                  // default graph
            _store.AddCurrentBatched(s, p, o, MirrorGraph);     // mirror graph
        }
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("bgp", "SELECT ?s ?o WHERE { ?s <urn:p> ?o }")]
    [InlineData("union", "SELECT ?s WHERE { { ?s <urn:p> ?o } UNION { ?s <urn:q> ?o } }")]
    [InlineData("optional", "SELECT ?s ?x WHERE { ?s <urn:p> ?o OPTIONAL { ?s <urn:q> ?x } }")]
    [InlineData("values", "SELECT ?o WHERE { VALUES ?s { <urn:a> } ?s <urn:p> ?o }")]
    public void GraphWrapped_ThroughTheUniformWalker_EqualsTheDefaultGraphBaseline(string name, string query)
    {
        // The known-correct baseline: the unwrapped query over the default graph, via the shipping engine.
        var baseline = SparqlEngine.Query(_store, query);
        Assert.True(baseline.Success, $"baseline failed: {baseline.ErrorMessage}");
        var projection = baseline.Variables!;
        var expected = Canonicalize(baseline.Rows, projection);

        // The new path on the GRAPH-WRAPPED query: recursive parser → uniform walker (active graph = mirror).
        // The projection comes from the ORIGINAL query — the mirrored form fails to parse on the shipping engine.
        string mirrored = MirrorWhereIntoGraph(query, MirrorGraph);
        var mirroredResult = EvaluateThroughTreeWalker(mirrored, projection);

        // And the new path on the UNWRAPPED query (active graph = default) — same evaluator, one path.
        var defaultResult = EvaluateThroughTreeWalker(query, projection);

        _output.WriteLine($"[{name}] baseline={Show(expected)}");
        _output.WriteLine($"  mirrored(<m>) via walker = {Show(mirroredResult)}");
        _output.WriteLine($"  default      via walker = {Show(defaultResult)}");

        // The whole point: GRAPH-wrapped (the shipping divergent path's RED case) now equals the default baseline,
        // and the default graph runs through the very same evaluator.
        Assert.Equal(expected, mirroredResult);
        Assert.Equal(expected, defaultResult);
    }

    /// <summary>Parse a query's WHERE group with the recursive parser and walk it with the uniform evaluator.</summary>
    private List<string> EvaluateThroughTreeWalker(string query, string[] projection)
    {
        string whereGroup = ExtractWhereGroup(query);
        var buffer = new byte[PatternSlot.Size * 256];
        var pa = new PatternArray(buffer);
        var parser = new SparqlParser(whereGroup.AsSpan());
        int root = parser.ParsePatternTree(ref pa);

        var evaluator = new GraphTreeEvaluator(_store, whereGroup);
        var solutions = evaluator.Evaluate(ref pa, root);
        return Canonicalize(solutions, projection);
    }

    // ── Helpers shared with the gate (kept local so each test file reads self-contained) ───────────────

    private static string MirrorWhereIntoGraph(string query, string graphIri)
    {
        int open = OpenBraceAfterWhere(query);
        int close = MatchingBrace(query, open);
        string body = query.Substring(open + 1, close - open - 1);
        return query[..(open + 1)] + " GRAPH " + graphIri + " {" + body + "} " + query[close..];
    }

    private static string ExtractWhereGroup(string query)
    {
        int open = OpenBraceAfterWhere(query);
        int close = MatchingBrace(query, open);
        return query.Substring(open, close - open + 1); // the balanced "{ ... }"
    }

    private static int OpenBraceAfterWhere(string query)
    {
        int w = query.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        int from = w < 0 ? 0 : w;
        int open = query.IndexOf('{', from);
        if (open < 0) throw new ArgumentException("No WHERE '{' found");
        return open;
    }

    private static int MatchingBrace(string query, int open)
    {
        int depth = 0;
        for (int i = open; i < query.Length; i++)
        {
            if (query[i] == '{') depth++;
            else if (query[i] == '}' && --depth == 0) return i;
        }
        throw new ArgumentException("Unbalanced braces");
    }

    private static List<string> Canonicalize(List<Dictionary<string, string>>? rows, string[] projection)
    {
        var canon = new List<string>();
        if (rows is null) return canon;
        foreach (var row in rows)
            canon.Add(string.Join("|", projection
                .Where(row.ContainsKey)
                .OrderBy(v => v, StringComparer.Ordinal)
                .Select(v => v + "=" + row[v])));
        canon.Sort(StringComparer.Ordinal);
        return canon;
    }

    private static string Show(List<string> bag) => $"{bag.Count} row(s): [{string.Join("; ", bag)}]";
}

/// <summary>
/// The uniform tree-walking evaluator (ADR-045 Step 4 model). One recursive walk over the <see cref="PatternArray"/>
/// tree, with the active graph as a parameter; a GRAPH header rebinds it for its subtree. Solution bags are heap
/// dictionaries (correctness model, not the zero-GC production form). Non-expression spine only this increment.
/// </summary>
internal sealed class GraphTreeEvaluator
{
    private readonly QuadStore _store;
    private readonly string _source;

    public GraphTreeEvaluator(QuadStore store, string source)
    {
        _store = store;
        _source = source;
    }

    public List<Dictionary<string, string>> Evaluate(ref PatternArray pa, int root)
    {
        var seed = new List<Dictionary<string, string>> { new() };
        return EvalGroup(ref pa, root, activeGraph: "", seed);
    }

    /// <summary>Evaluate a group-like header's body: fold its direct children left-to-right as a join sequence.</summary>
    private List<Dictionary<string, string>> EvalGroup(ref PatternArray pa, int headerIndex, string activeGraph,
        List<Dictionary<string, string>> input)
    {
        var children = new List<int>();
        var e = pa.EnumerateDirectChildren(headerIndex);
        while (e.MoveNext()) children.Add(e.CurrentIndex);

        var current = input;
        foreach (int ci in children)
            current = JoinStep(ref pa, ci, activeGraph, current);
        return current;
    }

    private List<Dictionary<string, string>> JoinStep(ref PatternArray pa, int childIndex, string activeGraph,
        List<Dictionary<string, string>> input)
    {
        var kind = pa[childIndex].Kind;
        switch (kind)
        {
            case PatternKind.Triple:
            {
                var output = new List<Dictionary<string, string>>();
                foreach (var sol in input)
                    ScanTriple(ref pa, childIndex, activeGraph, sol, output);
                return output;
            }
            case PatternKind.GraphHeader:
            {
                var slot = pa[childIndex];
                string subGraph = _source.Substring(slot.GraphTermStart, slot.GraphTermLength);
                return EvalGroup(ref pa, childIndex, subGraph, input); // GRAPH rebinds the active graph
            }
            case PatternKind.GroupHeader:
                return EvalGroup(ref pa, childIndex, activeGraph, input);
            case PatternKind.UnionHeader:
            {
                var output = new List<Dictionary<string, string>>();
                var branches = new List<int>();
                var be = pa.EnumerateDirectChildren(childIndex);
                while (be.MoveNext()) branches.Add(be.CurrentIndex);
                foreach (int branch in branches)
                    output.AddRange(EvalGroup(ref pa, branch, activeGraph, input)); // (A ∪ B) joined with input
                return output;
            }
            case PatternKind.OptionalHeader:
            {
                var output = new List<Dictionary<string, string>>();
                foreach (var sol in input)
                {
                    var single = new List<Dictionary<string, string>> { sol };
                    var ext = EvalGroup(ref pa, childIndex, activeGraph, single);
                    if (ext.Count > 0) output.AddRange(ext);
                    else output.Add(sol); // left join: preserve the row when the optional has no match
                }
                return output;
            }
            case PatternKind.ValuesHeader:
                return ValuesJoin(ref pa, childIndex, input);
            default:
                throw new NotSupportedException(
                    $"{kind} is a later evaluator increment (BIND/FILTER reuse the expression evaluators; MINUS/EXISTS/subquery follow).");
        }
    }

    private void ScanTriple(ref PatternArray pa, int tripleIndex, string activeGraph,
        Dictionary<string, string> sol, List<Dictionary<string, string>> output)
    {
        var slot = pa[tripleIndex];
        string sText = _source.Substring(slot.SubjectStart, slot.SubjectLength);
        string pText = _source.Substring(slot.PredicateStart, slot.PredicateLength);
        string oText = _source.Substring(slot.ObjectStart, slot.ObjectLength);
        var sType = slot.SubjectType;
        var pType = slot.PredicateType;
        var oType = slot.ObjectType;

        string sCon = Constraint(sType, sText, sol);
        string pCon = Constraint(pType, pText, sol);
        string oCon = Constraint(oType, oText, sol);

        var scan = _store.QueryCurrent(sCon.AsSpan(), pCon.AsSpan(), oCon.AsSpan(), activeGraph.AsSpan());
        while (scan.MoveNext())
        {
            var q = scan.Current;
            var ext = new Dictionary<string, string>(sol);
            if (TryBind(ext, sType, sText, q.Subject) &&
                TryBind(ext, pType, pText, q.Predicate) &&
                TryBind(ext, oType, oText, q.Object))
            {
                output.Add(ext);
            }
        }
    }

    /// <summary>The scan constraint for a position: a bound variable's value, a wildcard for an unbound one, or the constant.</summary>
    private static string Constraint(TermType type, string text, Dictionary<string, string> sol)
    {
        if (type == TermType.Variable)
            return sol.TryGetValue(VarName(text), out var v) ? v : ""; // "" = wildcard
        return text;
    }

    /// <summary>Bind a matched value to a variable position, rejecting the match on an inconsistent repeated variable.</summary>
    private static bool TryBind(Dictionary<string, string> ext, TermType type, string text, ReadOnlySpan<char> matched)
    {
        if (type != TermType.Variable) return true; // constant already matched by the scan
        string name = VarName(text);
        string value = matched.ToString();
        if (ext.TryGetValue(name, out var existing)) return existing == value;
        ext[name] = value;
        return true;
    }

    private List<Dictionary<string, string>> ValuesJoin(ref PatternArray pa, int valuesHeaderIndex,
        List<Dictionary<string, string>> input)
    {
        var header = pa[valuesHeaderIndex];
        string valuesVar = VarName(_source.Substring(header.ValuesVarStart, header.ValuesVarLength));
        int entryCount = header.ValuesEntryCount;

        var entries = new List<string?>();
        for (int k = 1; k <= entryCount; k++)
        {
            var entry = pa[valuesHeaderIndex + k];
            entries.Add(entry.ValuesEntryLength < 0
                ? null // UNDEF
                : _source.Substring(entry.ValuesEntryStart, entry.ValuesEntryLength));
        }

        var output = new List<Dictionary<string, string>>();
        foreach (var sol in input)
            foreach (var value in entries)
            {
                if (value is null) { output.Add(new Dictionary<string, string>(sol)); continue; } // UNDEF binds nothing
                if (sol.TryGetValue(valuesVar, out var existing) && existing != value) continue;   // inconsistent
                var ext = new Dictionary<string, string>(sol) { [valuesVar] = value };
                output.Add(ext);
            }
        return output;
    }

    private static string VarName(string termText) => termText.TrimStart('?');
}
