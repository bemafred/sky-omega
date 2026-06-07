using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;
using Xunit;
using Xunit.Abstractions;
using ExprValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

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
/// THE WALKER covers the full spine: BGP scan / GRAPH / nested group / UNION / OPTIONAL (left join) /
/// single-variable VALUES (inline-data join) / BIND / FILTER. BIND and FILTER reuse the REAL
/// <c>BindExpressionEvaluator</c> / <c>FilterEvaluator</c> — matched terms are bound RAW as String exactly as the
/// engine does (`STR`'s bracket-stripping and PNAME expansion are the evaluators' job, given the prologue prefixes),
/// so the proof carries real expression semantics. All 4 RED gate cases (bind, values, optional, union) plus the
/// FILTER cases now go GREEN through this one path. The walker is a correctness MODEL (heap solution bags, not
/// zero-GC); the production cutover reimplements it zero-GC over BindingTable and wires it into
/// <see cref="SparqlEngine"/>, then deletes the divergent path. Still to add before cutover: MINUS / EXISTS /
/// sub-SELECT / SERVICE / property paths (each throws <c>NotSupportedException</c> today rather than mis-evaluating).
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
            ("<urn:c>", "<urn:p>", "<urn:v3>"), // p but no q — makes MINUS / (NOT) EXISTS non-trivial
            ("<urn:a>", "<urn:next>", "<urn:b>"), // a -> b -> c chain for property-path closures
            ("<urn:b>", "<urn:next>", "<urn:c>"),
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
    // Non-expression spine (first evaluator increment):
    [InlineData("bgp", "SELECT ?s ?o WHERE { ?s <urn:p> ?o }")]
    [InlineData("union", "SELECT ?s WHERE { { ?s <urn:p> ?o } UNION { ?s <urn:q> ?o } }")]
    [InlineData("optional", "SELECT ?s ?x WHERE { ?s <urn:p> ?o OPTIONAL { ?s <urn:q> ?x } }")]
    [InlineData("values", "SELECT ?o WHERE { VALUES ?s { <urn:a> } ?s <urn:p> ?o }")]
    // Expression spine (this increment): BIND is the 4th RED gate case (BIND-in-GRAPH parse-fails on shipping);
    // FILTER cases reuse the real FilterEvaluator (the PNAME case also exercises prologue prefix expansion).
    [InlineData("bind", "SELECT ?l WHERE { ?s <urn:p> ?o BIND(STR(?o) AS ?l) }")]
    [InlineData("filter-object-iri", "SELECT ?s WHERE { ?s <urn:p> ?o FILTER(?o = <urn:v1>) }")]
    [InlineData("filter-subject-full-iri", "SELECT ?o WHERE { ?s <urn:p> ?o FILTER(?s = <urn:a>) }")]
    [InlineData("filter-subject-pname", "PREFIX ex: <urn:> SELECT ?o WHERE { ?s <urn:p> ?o FILTER(?s = ex:a) }")]
    // MINUS (positional anti-join) and FILTER [NOT] EXISTS (group-scoped sub-pattern): ?c has p but not q.
    [InlineData("minus", "SELECT ?s WHERE { ?s <urn:p> ?o MINUS { ?s <urn:q> ?x } }")]
    [InlineData("exists", "SELECT ?s WHERE { ?s <urn:p> ?o FILTER EXISTS { ?s <urn:q> ?x } }")]
    [InlineData("not-exists", "SELECT ?s WHERE { ?s <urn:p> ?o FILTER NOT EXISTS { ?s <urn:q> ?x } }")]
    // Property paths — ALL forms (inverse / + / * / ? / sequence / alternative / grouped / negated / PNAME),
    // each threaded through the active graph. Nothing deferred — a deferred path form is exactly the divergence.
    [InlineData("path-inverse", "SELECT ?o WHERE { <urn:b> ^<urn:next> ?o }")]
    [InlineData("path-plus-fwd", "SELECT ?o WHERE { <urn:a> <urn:next>+ ?o }")]
    [InlineData("path-plus-bwd", "SELECT ?s WHERE { ?s <urn:next>+ <urn:c> }")]
    [InlineData("path-plus-both", "SELECT ?s ?o WHERE { ?s <urn:next>+ ?o }")]
    [InlineData("path-star-fwd", "SELECT ?o WHERE { <urn:a> <urn:next>* ?o }")]
    [InlineData("path-zero-or-one", "SELECT ?o WHERE { <urn:a> <urn:next>? ?o }")]
    [InlineData("path-sequence", "SELECT ?o WHERE { <urn:a> <urn:next>/<urn:next> ?o }")]
    [InlineData("path-alternative", "SELECT ?o WHERE { <urn:a> <urn:next>|<urn:p> ?o }")]
    [InlineData("path-grouped", "SELECT ?o WHERE { <urn:a> (<urn:next>)+ ?o }")]
    [InlineData("path-negated", "SELECT ?o WHERE { <urn:a> !<urn:next> ?o }")]
    [InlineData("path-pname", "PREFIX ex: <urn:> SELECT ?o WHERE { <urn:a> ex:next+ ?o }")]
    public void GraphWrapped_ThroughTheUniformWalker_EqualsTheDefaultGraphBaseline(string name, string query)
    {
        // The known-correct baseline: the unwrapped query over the default graph, via the shipping engine.
        var baseline = SparqlEngine.Query(_store, query);
        Assert.True(baseline.Success, $"baseline failed: {baseline.ErrorMessage}");
        var projection = baseline.Variables!;
        var expected = Canonicalize(baseline.Rows, projection);

        // Prologue prefixes for FILTER PNAME expansion come from the ORIGINAL query (offsets into it).
        var prefixes = ExtractPrefixes(query);

        // The new path on the GRAPH-WRAPPED query: recursive parser → uniform walker (active graph = mirror).
        // The projection/prefixes come from the ORIGINAL query — the mirrored form may fail to parse on shipping.
        string mirrored = MirrorWhereIntoGraph(query, MirrorGraph);
        var mirroredResult = EvaluateThroughTreeWalker(mirrored, projection, prefixes, query);

        // And the new path on the UNWRAPPED query (active graph = default) — same evaluator, one path.
        var defaultResult = EvaluateThroughTreeWalker(query, projection, prefixes, query);

        _output.WriteLine($"[{name}] baseline={Show(expected)}");
        _output.WriteLine($"  mirrored(<m>) via walker = {Show(mirroredResult)}");
        _output.WriteLine($"  default      via walker = {Show(defaultResult)}");

        // The whole point: GRAPH-wrapped (the shipping divergent path's RED case) now equals the default baseline,
        // and the default graph runs through the very same evaluator.
        Assert.Equal(expected, mirroredResult);
        Assert.Equal(expected, defaultResult);
    }

    /// <summary>Parse a query's WHERE group with the recursive parser and walk it with the uniform evaluator.</summary>
    private List<string> EvaluateThroughTreeWalker(string query, string[] projection,
        PrefixMapping[]? prefixes, string prefixSource)
    {
        string whereGroup = ExtractWhereGroup(query);
        var buffer = new byte[PatternSlot.Size * 256];
        var pa = new PatternArray(buffer);
        var parser = new SparqlParser(whereGroup.AsSpan());
        int root = parser.ParsePatternTree(ref pa);

        var evaluator = new GraphTreeEvaluator(_store, whereGroup, prefixes, prefixSource);
        var solutions = evaluator.Evaluate(ref pa, root);
        return Canonicalize(solutions, projection);
    }

    /// <summary>Recover the prologue prefixes (offsets into <paramref name="query"/>) via the shipping parser.</summary>
    private static PrefixMapping[]? ExtractPrefixes(string query)
    {
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();
        int count = parsed.Prologue.PrefixCount;
        if (count == 0) return null;
        var prefixes = new PrefixMapping[count];
        for (int i = 0; i < count; i++)
        {
            var (prefixStart, prefixLength, iriStart, iriLength) = parsed.Prologue.GetPrefix(i);
            prefixes[i] = new PrefixMapping
            {
                PrefixStart = prefixStart,
                PrefixLength = prefixLength,
                IriStart = iriStart,
                IriLength = iriLength
            };
        }
        return prefixes;
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
    private readonly PrefixMapping[]? _prefixes;
    private readonly string _prefixSource;

    public GraphTreeEvaluator(QuadStore store, string source, PrefixMapping[]? prefixes = null, string? prefixSource = null)
    {
        _store = store;
        _source = source;
        _prefixes = prefixes;
        _prefixSource = prefixSource ?? source;
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

        // Patterns / BIND / MINUS are positional joins; FILTER and (NOT) EXISTS are scoped to the whole group,
        // so defer them and apply to all of the group's solutions.
        var current = input;
        foreach (int ci in children)
            if (!IsGroupScoped(pa[ci].Kind))
                current = JoinStep(ref pa, ci, activeGraph, current);
        foreach (int ci in children)
        {
            var kind = pa[ci].Kind;
            if (kind == PatternKind.Filter)
                current = ApplyFilter(ref pa, ci, current);
            else if (kind == PatternKind.ExistsHeader || kind == PatternKind.NotExistsHeader)
                current = ApplyExists(ref pa, ci, activeGraph, current, negated: kind == PatternKind.NotExistsHeader);
        }
        return current;
    }

    private static bool IsGroupScoped(PatternKind kind) =>
        kind == PatternKind.Filter || kind == PatternKind.ExistsHeader || kind == PatternKind.NotExistsHeader;

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
            case PatternKind.Bind:
                return BindStep(ref pa, childIndex, input);
            case PatternKind.MinusHeader:
                return MinusStep(ref pa, childIndex, activeGraph, input);
            default:
                throw new NotSupportedException(
                    $"{kind} is a later evaluator increment (sub-SELECT / SERVICE / property paths follow).");
        }
    }

    private void ScanTriple(ref PatternArray pa, int tripleIndex, string activeGraph,
        Dictionary<string, string> sol, List<Dictionary<string, string>> output)
    {
        var slot = pa[tripleIndex];
        string sText = _source.Substring(slot.SubjectStart, slot.SubjectLength);
        string oText = _source.Substring(slot.ObjectStart, slot.ObjectLength);
        var sType = slot.SubjectType;
        var oType = slot.ObjectType;

        // A property path of any form: the slot carries the full path-expression span; evaluate the path algebra.
        if (slot.PathKind != PathType.None)
        {
            string pathText = _source.Substring(slot.PathIriStart, slot.PathIriLength);
            ScanPathTriple(pathText, sType, sText, oType, oText, sol, activeGraph, output);
            return;
        }

        string pText = _source.Substring(slot.PredicateStart, slot.PredicateLength);
        var pType = slot.PredicateType;

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

    // ── Property paths (ALL forms) ────────────────────────────────────────────────────────────────────
    // The slot carries the full path-expression source span; the path algebra is evaluated against the active
    // graph as a relation (set of subject→object pairs) and filtered by the bound endpoints. Arbitrary-length
    // (* +) and zero-length (* ?) paths yield DISTINCT endpoint pairs (SPARQL §9) — the pair-set gives that.
    // No path form is deferred; deferring on the new path is the very divergence ADR-045 deletes.

    private void ScanPathTriple(string pathText, TermType sType, string sText, TermType oType, string oText,
        Dictionary<string, string> sol, string activeGraph, List<Dictionary<string, string>> output)
    {
        string? sBound = Bound(sType, sText, sol);
        string? oBound = Bound(oType, oText, sol);

        int pos = 0;
        var pairs = PathAlternative(pathText, ref pos, activeGraph);

        foreach (var (s, o) in pairs)
        {
            if (sBound != null && s != sBound) continue;
            if (oBound != null && o != oBound) continue;
            var ext = new Dictionary<string, string>(sol);
            bool ok = true;
            if (sType == TermType.Variable)
            {
                string name = VarName(sText);
                if (ext.TryGetValue(name, out var ev)) ok = ev == s; else ext[name] = s;
            }
            if (ok && oType == TermType.Variable)
            {
                string name = VarName(oText);
                if (ext.TryGetValue(name, out var ev)) ok = ev == o; else ext[name] = o;
            }
            if (ok) output.Add(ext);
        }
    }

    /// <summary>A bound endpoint's value (a constant term or a bound variable), or null for an unbound variable.</summary>
    private static string? Bound(TermType type, string text, Dictionary<string, string> sol)
        => type == TermType.Variable ? (sol.TryGetValue(VarName(text), out var v) ? v : null) : text;

    // [98] PathAlternative ::= PathSequence ( '|' PathSequence )*
    private HashSet<(string, string)> PathAlternative(string t, ref int pos, string ag)
    {
        var result = PathSequence(t, ref pos, ag);
        SkipPathWs(t, ref pos);
        while (pos < t.Length && t[pos] == '|')
        {
            pos++;
            result.UnionWith(PathSequence(t, ref pos, ag));
            SkipPathWs(t, ref pos);
        }
        return result;
    }

    // [99] PathSequence ::= PathEltOrInverse ( '/' PathEltOrInverse )*
    private HashSet<(string, string)> PathSequence(string t, ref int pos, string ag)
    {
        var result = PathEltOrInverse(t, ref pos, ag);
        SkipPathWs(t, ref pos);
        while (pos < t.Length && t[pos] == '/')
        {
            pos++;
            result = Compose(result, PathEltOrInverse(t, ref pos, ag));
            SkipPathWs(t, ref pos);
        }
        return result;
    }

    // [100] PathEltOrInverse ::= '^'? PathElt
    private HashSet<(string, string)> PathEltOrInverse(string t, ref int pos, string ag)
    {
        SkipPathWs(t, ref pos);
        bool inverse = pos < t.Length && t[pos] == '^';
        if (inverse) pos++;
        var rel = PathElt(t, ref pos, ag);
        return inverse ? Swap(rel) : rel;
    }

    // [101] PathElt ::= PathPrimary ( '*' | '+' | '?' )?
    private HashSet<(string, string)> PathElt(string t, ref int pos, string ag)
    {
        var rel = PathPrimary(t, ref pos, ag);
        SkipPathWs(t, ref pos);
        if (pos < t.Length && (t[pos] == '*' || t[pos] == '+' || t[pos] == '?'))
        {
            char mod = t[pos++];
            return mod switch
            {
                '+' => TransitiveClosure(rel),
                '*' => ReflexiveTransitiveClosure(rel, ag),
                _ => ZeroOrOne(rel, ag),
            };
        }
        return rel;
    }

    // [102] PathPrimary ::= iri | 'a' | '(' Path ')' | '!' PathNegatedPropertySet
    private HashSet<(string, string)> PathPrimary(string t, ref int pos, string ag)
    {
        SkipPathWs(t, ref pos);
        if (pos < t.Length && t[pos] == '(')
        {
            pos++;
            var inner = PathAlternative(t, ref pos, ag);
            SkipPathWs(t, ref pos);
            if (pos < t.Length && t[pos] == ')') pos++;
            return inner;
        }
        if (pos < t.Length && t[pos] == '!')
        {
            pos++;
            return PathNegatedPropertySet(t, ref pos, ag);
        }
        return BasePairs(ReadPathIri(t, ref pos), ag);
    }

    // [104]/[105] PathNegatedPropertySet — a single step whose predicate is NOT in the set (forward, plus ^inverse).
    private HashSet<(string, string)> PathNegatedPropertySet(string t, ref int pos, string ag)
    {
        var forward = new HashSet<string>();
        var inverse = new HashSet<string>();
        SkipPathWs(t, ref pos);
        if (pos < t.Length && t[pos] == '(')
        {
            pos++;
            SkipPathWs(t, ref pos);
            while (pos < t.Length && t[pos] != ')')
            {
                ReadOneInPropertySet(t, ref pos, forward, inverse);
                SkipPathWs(t, ref pos);
                if (pos < t.Length && t[pos] == '|') { pos++; SkipPathWs(t, ref pos); }
            }
            if (pos < t.Length && t[pos] == ')') pos++;
        }
        else
        {
            ReadOneInPropertySet(t, ref pos, forward, inverse);
        }

        var result = new HashSet<(string, string)>();
        var scan = _store.QueryCurrent("".AsSpan(), "".AsSpan(), "".AsSpan(), ag.AsSpan());
        while (scan.MoveNext())
        {
            var q = scan.Current;
            string s = q.Subject.ToString(), p = q.Predicate.ToString(), o = q.Object.ToString();
            if (!forward.Contains(p)) result.Add((s, o));
            if (inverse.Count > 0 && !inverse.Contains(p)) result.Add((o, s));
        }
        return result;
    }

    private void ReadOneInPropertySet(string t, ref int pos, HashSet<string> forward, HashSet<string> inverse)
    {
        SkipPathWs(t, ref pos);
        bool inv = pos < t.Length && t[pos] == '^';
        if (inv) pos++;
        (inv ? inverse : forward).Add(ReadPathIri(t, ref pos));
    }

    private static void SkipPathWs(string t, ref int pos)
    {
        while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
    }

    /// <summary>Read an IRI / prefixed name / 'a' path primary and resolve it to canonical IRI form ("&lt;…&gt;").</summary>
    private string ReadPathIri(string t, ref int pos)
    {
        SkipPathWs(t, ref pos);
        if (pos < t.Length && t[pos] == '<')
        {
            int start = pos++;
            while (pos < t.Length && t[pos] != '>') pos++;
            if (pos < t.Length) pos++; // consume '>'
            return t.Substring(start, pos - start);
        }
        int s = pos;
        while (pos < t.Length && (char.IsLetterOrDigit(t[pos]) || t[pos] == ':' || t[pos] == '_' || t[pos] == '-' || t[pos] == '.'))
            pos++;
        string token = t.Substring(s, pos - s);
        if (token == "a")
            return "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
        return ExpandPathPname(token);
    }

    private string ExpandPathPname(string token)
    {
        int colon = token.IndexOf(':');
        if (colon < 0 || _prefixes is null) return token;
        // The prologue stores the prefix WITH its trailing ':' ("ex:") and the namespace IRI WITH its < > .
        var prefixWithColon = token.AsSpan(0, colon + 1);
        string local = token.Substring(colon + 1);
        foreach (var pm in _prefixes)
        {
            if (!_prefixSource.AsSpan(pm.PrefixStart, pm.PrefixLength).SequenceEqual(prefixWithColon)) continue;
            var ns = _prefixSource.AsSpan(pm.IriStart, pm.IriLength);
            if (ns.Length >= 2 && ns[0] == '<' && ns[^1] == '>') ns = ns[1..^1];
            return "<" + ns.ToString() + local + ">";
        }
        return token;
    }

    private HashSet<(string, string)> BasePairs(string iri, string ag)
    {
        var result = new HashSet<(string, string)>();
        var scan = _store.QueryCurrent("".AsSpan(), iri.AsSpan(), "".AsSpan(), ag.AsSpan());
        while (scan.MoveNext())
        {
            var q = scan.Current;
            result.Add((q.Subject.ToString(), q.Object.ToString()));
        }
        return result;
    }

    private static HashSet<(string, string)> Swap(HashSet<(string, string)> rel)
    {
        var result = new HashSet<(string, string)>();
        foreach (var (s, o) in rel) result.Add((o, s));
        return result;
    }

    private static HashSet<(string, string)> Compose(HashSet<(string, string)> a, HashSet<(string, string)> b)
    {
        var byMid = new Dictionary<string, List<string>>();
        foreach (var (m, o) in b)
        {
            if (!byMid.TryGetValue(m, out var list)) byMid[m] = list = new List<string>();
            list.Add(o);
        }
        var result = new HashSet<(string, string)>();
        foreach (var (s, m) in a)
            if (byMid.TryGetValue(m, out var os))
                foreach (var o in os) result.Add((s, o));
        return result;
    }

    private static HashSet<(string, string)> TransitiveClosure(HashSet<(string, string)> rel)
    {
        var result = new HashSet<(string, string)>(rel);
        var frontier = new HashSet<(string, string)>(rel);
        while (frontier.Count > 0)
        {
            var next = Compose(frontier, rel);
            next.ExceptWith(result);
            if (next.Count == 0) break;
            result.UnionWith(next);
            frontier = next;
        }
        return result;
    }

    private HashSet<(string, string)> ReflexiveTransitiveClosure(HashSet<(string, string)> rel, string ag)
    {
        var result = TransitiveClosure(rel);
        foreach (var node in GraphNodes(ag)) result.Add((node, node));
        return result;
    }

    private HashSet<(string, string)> ZeroOrOne(HashSet<(string, string)> rel, string ag)
    {
        var result = new HashSet<(string, string)>(rel);
        foreach (var node in GraphNodes(ag)) result.Add((node, node));
        return result;
    }

    private HashSet<string> GraphNodes(string ag)
    {
        var nodes = new HashSet<string>();
        var scan = _store.QueryCurrent("".AsSpan(), "".AsSpan(), "".AsSpan(), ag.AsSpan());
        while (scan.MoveNext())
        {
            var q = scan.Current;
            nodes.Add(q.Subject.ToString());
            nodes.Add(q.Object.ToString());
        }
        return nodes;
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

    /// <summary>BIND: extend each solution with the evaluated expression, via the real <see cref="BindExpressionEvaluator"/>.</summary>
    private List<Dictionary<string, string>> BindStep(ref PatternArray pa, int bindIndex,
        List<Dictionary<string, string>> input)
    {
        var slot = pa[bindIndex];
        string exprText = _source.Substring(slot.BindExprStart, slot.BindExprLength);
        string key = VarName(_source.Substring(slot.BindVarStart, slot.BindVarLength));

        var output = new List<Dictionary<string, string>>();
        foreach (var sol in input)
        {
            Span<Binding> store = new Binding[64];
            Span<char> stringBuffer = new char[8192];
            var table = new BindingTable(store, stringBuffer);
            Populate(ref table, sol);

            var evaluator = new BindExpressionEvaluator(exprText.AsSpan(),
                table.GetBindings(), table.Count, table.GetStringBuffer());
            string rendered = Render(evaluator.Evaluate());

            output.Add(new Dictionary<string, string>(sol) { [key] = rendered });
        }
        return output;
    }

    /// <summary>FILTER: keep solutions for which the constraint holds, via the real <see cref="FilterEvaluator"/>.</summary>
    private List<Dictionary<string, string>> ApplyFilter(ref PatternArray pa, int filterIndex,
        List<Dictionary<string, string>> input)
    {
        var slot = pa[filterIndex];
        string exprText = _source.Substring(slot.FilterStart, slot.FilterLength);

        var output = new List<Dictionary<string, string>>();
        foreach (var sol in input)
        {
            Span<Binding> store = new Binding[64];
            Span<char> stringBuffer = new char[8192];
            var table = new BindingTable(store, stringBuffer);
            Populate(ref table, sol);

            var evaluator = new FilterEvaluator(exprText.AsSpan());
            bool pass = _prefixes is not null
                ? evaluator.Evaluate(table.GetBindings(), table.Count, table.GetStringBuffer(), _prefixes, _prefixSource.AsSpan())
                : evaluator.Evaluate(table.GetBindings(), table.Count, table.GetStringBuffer());
            if (pass) output.Add(sol);
        }
        return output;
    }

    /// <summary>
    /// MINUS (positional): drop a left solution iff the right side has a solution that is compatible with it AND
    /// shares at least one variable (SPARQL §8.3 — disjoint domains do not remove). The right side is evaluated
    /// INDEPENDENTLY of the left (seeded with the empty solution), unlike NOT EXISTS.
    /// </summary>
    private List<Dictionary<string, string>> MinusStep(ref PatternArray pa, int minusIndex, string activeGraph,
        List<Dictionary<string, string>> input)
    {
        var seed = new List<Dictionary<string, string>> { new() };
        var rhs = EvalGroup(ref pa, minusIndex, activeGraph, seed);

        var output = new List<Dictionary<string, string>>();
        foreach (var mu in input)
        {
            bool removed = false;
            foreach (var other in rhs)
            {
                bool sharesVar = false, compatible = true;
                foreach (var kv in mu)
                    if (other.TryGetValue(kv.Key, out var otherValue))
                    {
                        sharesVar = true;
                        if (otherValue != kv.Value) { compatible = false; break; }
                    }
                if (sharesVar && compatible) { removed = true; break; }
            }
            if (!removed) output.Add(mu);
        }
        return output;
    }

    /// <summary>
    /// FILTER [NOT] EXISTS (group-scoped): keep a solution iff its body — evaluated with that solution's bindings
    /// in scope (seeded with the solution, so its bound variables constrain the body's scans) — is non-empty
    /// (EXISTS) or empty (NOT EXISTS). EXISTS adds no bindings; it only filters.
    /// </summary>
    private List<Dictionary<string, string>> ApplyExists(ref PatternArray pa, int existsIndex, string activeGraph,
        List<Dictionary<string, string>> input, bool negated)
    {
        var output = new List<Dictionary<string, string>>();
        foreach (var mu in input)
        {
            var seed = new List<Dictionary<string, string>> { new Dictionary<string, string>(mu) };
            var body = EvalGroup(ref pa, existsIndex, activeGraph, seed);
            bool exists = body.Count > 0;
            if (negated ? !exists : exists) output.Add(mu);
        }
        return output;
    }

    /// <summary>
    /// Populate a transient binding table from a solution, mirroring the engine exactly: matched terms are bound
    /// RAW as String (no bracket/quote stripping — that is the expression evaluators' job) and the variable name
    /// carries its leading '?'.
    /// </summary>
    private static void Populate(ref BindingTable table, Dictionary<string, string> sol)
    {
        foreach (var kv in sol)
            table.Bind(("?" + kv.Key).AsSpan(), kv.Value.AsSpan());
    }

    /// <summary>Render a BIND result to the string form the result set would carry.</summary>
    private static string Render(Value value) => value.Type switch
    {
        ExprValueType.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
        ExprValueType.Double => value.DoubleValue.ToString("G", CultureInfo.InvariantCulture),
        ExprValueType.Boolean => value.BooleanValue ? "true" : "false",
        _ => value.StringValue.ToString(),
    };

    private static string VarName(string termText) => termText.TrimStart('?');
}
