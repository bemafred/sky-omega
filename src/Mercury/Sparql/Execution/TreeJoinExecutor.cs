using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Execution.Federated;
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
    private readonly ISparqlServiceExecutor? _serviceExecutor;
    private readonly TemporalQueryMode _temporalMode;
    private readonly DateTimeOffset _asOfTime;
    private readonly DateTimeOffset _rangeStart;
    private readonly DateTimeOffset _rangeEnd;
    // Dataset (FROM NAMED / USING NAMED): which named graphs GRAPH may access. null = all; empty = none; [g…] = those.
    private readonly string[]? _namedGraphs;
    // Dataset (FROM / USING): the default-graph set. null/empty = the real unnamed default graph; [g…] = the FROM
    // graphs, whose RDF merge IS the default graph (SPARQL §13.2) — a default-context pattern (activeGraph == "")
    // scans their UNION, and because every pattern re-scans the whole set, a BGP join may span them (the merge, not
    // per-graph silos). Inert inside a GRAPH clause, where activeGraph is the specific named-graph IRI.
    private readonly string[]? _defaultGraphs;
    // ADR-047 spike: reorder each BGP run by selectivity (the QueryPlanner model) before the nested-loop join. A
    // BGP join is commutative, so this is correctness-neutral; it only changes the join order's cost.
    private readonly bool _reorderBgp;
    private readonly long _guardCap; // StorageOptions.MaxResultRows — the unbounded-result guard (0 = unbounded)
    private QueryPlanner? _planner;
    // ADR-024 trigram pre-filter: object-variable name → candidate object atom IDs from text:match. A pattern whose
    // object is one of these variables scans candidate-filtered (the old MultiPatternScan integration, now in the tree).
    private readonly Dictionary<string, HashSet<long>>? _trigramCandidatesByVar;

    public TreeJoinExecutor(QuadStore store, string source, PrefixMapping[]? prefixes = null,
        string? prefixSource = null, ISparqlServiceExecutor? serviceExecutor = null,
        TemporalQueryMode temporalMode = TemporalQueryMode.Current,
        DateTimeOffset asOfTime = default, DateTimeOffset rangeStart = default, DateTimeOffset rangeEnd = default,
        string[]? namedGraphs = null, string[]? defaultGraphs = null, bool reorderBgp = false,
        Dictionary<string, HashSet<long>>? trigramCandidatesByVar = null)
    {
        _store = store;
        _source = source;
        _prefixes = prefixes;
        _prefixSource = prefixSource ?? source;
        _serviceExecutor = serviceExecutor;
        _temporalMode = temporalMode;
        _asOfTime = asOfTime;
        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;
        _namedGraphs = namedGraphs;
        _defaultGraphs = defaultGraphs;
        _reorderBgp = reorderBgp;
        _guardCap = store.MaxResultRows;
        _trigramCandidatesByVar = trigramCandidatesByVar;
    }

    /// <summary>The trigram candidate object atom IDs for a pattern whose object is a text:match variable (simple
    /// forward pattern only — the candidate filter is an object-position pre-filter), or null.</summary>
    private HashSet<long>? ObjectCandidatesFor(in TriplePattern p)
    {
        if (_trigramCandidatesByVar == null || p.Path.Type != PathType.None || !p.Object.IsVariable)
            return null;
        var varName = _source.AsSpan(p.Object.Start + 1, p.Object.Length - 1); // skip '?'
        return _trigramCandidatesByVar.TryGetValue(varName.ToString(), out var c) ? c : null;
    }

    // LIMIT-pushdown row cap (ADR-045): when the query is a pure BGP with LIMIT and no order/group/distinct/having
    // (see IsPureBgp + the QueryExecutor guard), the BGP scan stops after this many rows instead of materializing
    // the whole match set then truncating. int.MaxValue = no cap (every other shape).
    private int _maxRows = int.MaxValue;

    /// <summary>
    /// Evaluate the group at <paramref name="rootHeader"/> into materialized rows, threading the active graph.
    /// <paramref name="maxRows"/> caps the BGP scan for a pushed-down LIMIT (default: no cap).
    /// </summary>
    public List<MaterializedRow> Evaluate(ref PatternArray pa, int rootHeader, string activeGraph, int maxRows = int.MaxValue)
    {
        _maxRows = maxRows;
        var seed = new List<MaterializedRow> { EmptyRow() };
        // A WHERE that IS a bare sub-SELECT (single-braced `WHERE { SELECT … }`) or SERVICE parses to THAT header as the
        // root, not a GroupHeader wrapping it — so dispatch it directly; EvalGroup would enumerate its (zero) children
        // and return the seed unchanged (the sub-SELECT never runs — W3C basic-update insert-05a etc.).
        var rootKind = pa[rootHeader].Kind;
        if (rootKind is PatternKind.SubSelectHeader or PatternKind.ServiceHeader)
            return JoinOperator(ref pa, rootHeader, rootKind, activeGraph, seed);
        return EvalGroup(ref pa, rootHeader, activeGraph, seed);
    }

    /// <summary>
    /// True if the subtree at <paramref name="header"/> is a pure basic graph pattern — only triples, GRAPH headers,
    /// and nested groups, no composing operators (FILTER / BIND / UNION / OPTIONAL / MINUS / VALUES / EXISTS /
    /// sub-SELECT / SERVICE). Only then may a LIMIT push into the scan: nothing between the BGP and the LIMIT
    /// drops or adds rows, so the first N matched rows are exactly the first N result rows.
    /// </summary>
    public static bool IsPureBgp(ref PatternArray pa, int header)
    {
        var e = pa.EnumerateDirectChildren(header);
        while (e.MoveNext())
        {
            var kind = e.Current.Kind;
            if (kind == PatternKind.Triple)
                continue;
            if (kind is PatternKind.GraphHeader or PatternKind.GroupHeader)
            {
                if (!IsPureBgp(ref pa, e.CurrentIndex)) return false;
                continue;
            }
            return false; // any composing operator
        }
        return true;
    }

    /// <summary>
    /// ADR-047 materialization fix — stricter than <see cref="IsPureBgp"/>: every direct child of
    /// <paramref name="header"/> is a PLAIN triple (no property paths, no nested groups, no GRAPH headers). Only this
    /// flat shape can be streamed by <see cref="FoldFlatBgpAggregate"/>, which collects the children as one BGP run
    /// and folds each join row into the aggregate accumulator. Nested groups / paths reintroduce the List-materializing
    /// operator composition the fold exists to avoid, so they fall back to the materializing path. Requires at least
    /// one triple: an empty group ({}) folds nothing and is left to the materializing path's empty-row seed.
    /// </summary>
    public static bool IsFlatBgp(ref PatternArray pa, int header)
    {
        var e = pa.EnumerateDirectChildren(header);
        bool any = false;
        while (e.MoveNext())
        {
            any = true;
            if (e.Current.Kind != PatternKind.Triple) return false;
            if (e.Current.PathKind != PathType.None) return false;
        }
        return any;
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
                current = ApplyFilter(ref pa, ci, activeGraph, current);
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

        if (_reorderBgp && patternArr.Length > 1)
            ReorderBySelectivity(ref patternArr, ref graphArr);

        var bindingArray = ArrayPool<Binding>.Shared.Rent(256);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 16);
        try
        {
            var bindings = new BindingTable(bindingArray.AsSpan(0, 256), charArray.AsSpan(0, 1 << 16));
            foreach (var row in input)
            {
                if (output.Count >= _maxRows) break; // LIMIT pushed into the scan
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

    /// <summary>
    /// ADR-047 spike: reorder a BGP run by the QueryPlanner's selectivity model (greedy: most selective first,
    /// given the variables earlier patterns bind), so the nested-loop join runs the smallest outer set. Correctness-
    /// neutral — a BGP join is commutative. Reuses the production planner via <see cref="QueryPlanner.OptimizePatternOrder"/>.
    /// </summary>
    private void ReorderBySelectivity(ref TriplePattern[] patternArr, ref string[] graphArr)
    {
        _planner ??= new QueryPlanner(_store.Statistics, _store.Atoms);

        var gp = new GraphPattern();
        foreach (var tp in patternArr)
            gp.AddPattern(tp);

        int[] order = _planner.OptimizePatternOrder(gp, _source.AsSpan());
        if (order.Length != patternArr.Length)
            return; // shape mismatch (e.g. an optional crept in) — leave source order untouched

        var reorderedPatterns = new TriplePattern[patternArr.Length];
        var reorderedGraphs = new string[graphArr.Length];
        for (int i = 0; i < order.Length; i++)
        {
            reorderedPatterns[i] = patternArr[order[i]];
            reorderedGraphs[i] = graphArr[order[i]];
        }
        patternArr = reorderedPatterns;
        graphArr = reorderedGraphs;
    }

    private List<MaterializedRow> JoinOperator(ref PatternArray pa, int ci, PatternKind kind, string activeGraph,
        List<MaterializedRow> input)
    {
        switch (kind)
        {
            case PatternKind.GraphHeader:
            {
                var slot = pa[ci];
                string graphTerm = _source.Substring(slot.GraphTermStart, slot.GraphTermLength);
                if (slot.GraphTermType == TermType.Variable)
                    return VariableGraphStep(ref pa, ci, graphTerm, input);
                // Expand a prefixed graph name (GRAPH :g1) to its full IRI so it matches the stored graph.
                string graphIri = ExpandPname(graphTerm);
                // A dataset restriction (USING / FROM NAMED) makes a graph outside it inaccessible — GRAPH <g> over
                // such a graph matches nothing (e.g. USING without USING NAMED makes ALL named graphs inaccessible).
                if (!GraphAccessible(graphIri))
                    return new List<MaterializedRow>();
                return EvalGroup(ref pa, ci, graphIri, input);
            }
            case PatternKind.GroupHeader:
                // A nested group { P } is evaluated INDEPENDENTLY and then joined — Join(input, eval(P)) per SPARQL
                // §18.2 — NOT seeded by the outer bindings. Seeding leaks an outer-only variable into the group's own
                // FILTER scope: in BIND(4 AS ?z) { ?s :p ?v FILTER(?v=?z) }, ?z is NOT in scope for the inner FILTER
                // (W3C bind10), so the FILTER must see ?z unbound. (Seeding remains a valid pushdown only for the BGP
                // runs inside a group, where it cannot change FILTER scope.)
                return Join(input, EvalGroup(ref pa, ci, activeGraph, new List<MaterializedRow> { EmptyRow() }));
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
            case PatternKind.MinusHeader:
                return MinusStep(ref pa, ci, activeGraph, input);
            case PatternKind.ValuesHeader:
                return ValuesStep(ref pa, ci, input);
            case PatternKind.SubSelectHeader:
                return SubSelectStep(ref pa, ci, activeGraph, input);
            case PatternKind.ServiceHeader:
                return ServiceStep(ref pa, ci, input);
            default:
                throw new NotSupportedException($"{kind} has no evaluator case.");
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
                // Bind via the typed overloads so the result carries its RDF datatype tag (Integer/Double/Boolean), which
                // MaterializedRow now preserves. A numeric BIND result then reads back as its lexical form yet still
                // matches a stored typed literal when it feeds a later pattern (W3C bind03).
                var v = evaluator.Evaluate(_prefixes, _prefixSource.AsSpan());
                switch (v.Type)
                {
                    case ExprValueType.Integer: table.Bind(varName.AsSpan(), v.IntegerValue); break;
                    case ExprValueType.Double: table.Bind(varName.AsSpan(), v.DoubleValue); break;
                    case ExprValueType.Boolean: table.Bind(varName.AsSpan(), v.BooleanValue); break;
                    default: table.Bind(varName.AsSpan(), v.StringValue); break;
                }
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
    private List<MaterializedRow> ApplyFilter(ref PatternArray pa, int filterIndex, string activeGraph, List<MaterializedRow> input)
    {
        var slot = pa[filterIndex];
        int exprStart = slot.FilterStart;
        string exprText = _source.Substring(exprStart, slot.FilterLength);

        // A FILTER whose expression EMBEDS [NOT] EXISTS { … } as a sub-expression (e.g. ?a = ?b || NOT EXISTS { … })
        // — distinct from a standalone FILTER [NOT] EXISTS, which the parser lifts to an ExistsHeader (ApplyExists).
        // FilterEvaluator has no EXISTS support, so resolve each embedded EXISTS per row against the store and
        // substitute its truth value into the expression text; any surrounding NOT / ! / || / && is then handled by
        // FilterEvaluator. (W3C subset-02 — NOT EXISTS inside a MINUS's FILTER.)
        var existsSpans = FindEmbeddedExists(exprText);
        if (existsSpans.Count > 0)
            return ApplyFilterWithEmbeddedExists(exprText, exprStart, existsSpans, activeGraph, input);

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
    /// FILTER with an EXISTS embedded in a compound expression: per row, substitute each <c>EXISTS { … }</c> with its
    /// truth value (evaluated against the store, seeded with the row so its bound variables constrain the body), then
    /// evaluate the rewritten expression. The body is re-parsed over THIS executor's source (offsets index it) and run
    /// through the same <see cref="EvalGroup"/> as everything else, so a nested GRAPH / FILTER / EXISTS inside it works.
    /// </summary>
    private List<MaterializedRow> ApplyFilterWithEmbeddedExists(string exprText, int exprStart,
        List<(int start, int end, int braceRel)> existsSpans, string activeGraph, List<MaterializedRow> input)
    {
        var output = new List<MaterializedRow>();
        var bindingArray = ArrayPool<Binding>.Shared.Rent(64);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 13);
        try
        {
            var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
            var sb = new System.Text.StringBuilder(exprText.Length);
            foreach (var row in input)
            {
                sb.Clear();
                int cursor = 0;
                foreach (var (start, end, braceRel) in existsSpans)
                {
                    sb.Append(exprText, cursor, start - cursor);
                    bool exists = ExistsBodyHasMatch(exprStart + braceRel, activeGraph, row);
                    sb.Append(exists ? "true" : "false");
                    cursor = end;
                }
                sb.Append(exprText, cursor, exprText.Length - cursor);
                string rewritten = sb.ToString();

                table.TruncateTo(0);
                row.RestoreBindings(ref table);
                var evaluator = new FilterEvaluator(rewritten.AsSpan());
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

    /// <summary>True iff the EXISTS body at <paramref name="braceOffset"/> (the body's '{' in this executor's source)
    /// has at least one solution when seeded with <paramref name="row"/>'s bindings (which constrain it).</summary>
    private bool ExistsBodyHasMatch(int braceOffset, string activeGraph, MaterializedRow row)
    {
        const int treeBytes = PatternSlot.Size * 256;
        var buf = ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var subPa = new PatternArray(buf.AsSpan(0, treeBytes));
            int subRoot = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(braceOffset, ref subPa);
            return EvalGroup(ref subPa, subRoot, activeGraph, new List<MaterializedRow> { row }).Count > 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Locate each <c>EXISTS { … }</c> embedded in a FILTER expression (keyword + braced body), skipping string
    /// literals so an "EXISTS" inside a quoted string is not matched. Returns (start, endExclusive, braceRel) per
    /// occurrence, braceRel being the body '{' offset within <paramref name="expr"/>. A leading NOT is intentionally
    /// left in place — the caller substitutes the bare EXISTS truth value and lets FilterEvaluator apply NOT / '!'.
    /// </summary>
    private static List<(int start, int end, int braceRel)> FindEmbeddedExists(string expr)
    {
        var spans = new List<(int, int, int)>();
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if (c == '"' || c == '\'')
            {
                char q = c; i++;
                while (i < expr.Length && expr[i] != q) { if (expr[i] == '\\') i++; i++; }
                i++; // closing quote
                continue;
            }
            if ((c == 'E' || c == 'e') && IsKeywordAt(expr, i, "EXISTS"))
            {
                int braceRel = expr.IndexOf('{', i + 6);
                if (braceRel < 0) break;
                int braceEnd = MatchBrace(expr, braceRel);
                spans.Add((i, braceEnd + 1, braceRel));
                i = braceEnd + 1;
                continue;
            }
            i++;
        }
        return spans;
    }

    /// <summary>Whether the case-insensitive keyword sits at <paramref name="pos"/> with non-identifier boundaries.</summary>
    private static bool IsKeywordAt(string s, int pos, string kw)
    {
        if (pos + kw.Length > s.Length) return false;
        for (int k = 0; k < kw.Length; k++)
            if (char.ToUpperInvariant(s[pos + k]) != kw[k]) return false;
        if (pos > 0 && (char.IsLetterOrDigit(s[pos - 1]) || s[pos - 1] == '_')) return false;
        int after = pos + kw.Length;
        if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_')) return false;
        return true;
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
    /// GRAPH ?g { P }: for each named graph g, evaluate P against g and bind ?g = g, unioned over the graphs. (The
    /// default graph is not a named graph, so it is not enumerated — matching SPARQL's GRAPH semantics.)
    /// </summary>
    private List<MaterializedRow> VariableGraphStep(ref PatternArray pa, int graphHeaderIndex, string graphVar, List<MaterializedRow> input)
    {
        var graphNames = new List<string>();
        if (_namedGraphs != null)
        {
            graphNames.AddRange(_namedGraphs); // dataset restriction: only these named graphs are visible
        }
        else
        {
            var en = _store.GetNamedGraphs();
            while (en.MoveNext()) graphNames.Add(en.Current.ToString());
        }

        var output = new List<MaterializedRow>();
        var bindingArray = ArrayPool<Binding>.Shared.Rent(8);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 10);
        try
        {
            foreach (var g in graphNames)
            {
                string graphIri = g.Length > 0 && g[0] == '<' ? g : "<" + g + ">"; // the scan's active graph, in IRI form
                var bodyRows = EvalGroup(ref pa, graphHeaderIndex, graphIri, input);
                if (bodyRows.Count == 0) continue;

                var table = new BindingTable(bindingArray.AsSpan(0, 8), charArray.AsSpan(0, 1 << 10));
                table.TruncateTo(0);
                table.Bind(graphVar.AsSpan(), graphIri.AsSpan());
                output.AddRange(Join(bodyRows, new List<MaterializedRow> { new(table) }));
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
    /// MINUS (positional): drop a left row iff the right side has a row compatible with it AND sharing at least one
    /// variable (SPARQL §8.3 — disjoint domains do not remove). The right side is evaluated INDEPENDENTLY of the
    /// left (seeded with the empty row), unlike NOT EXISTS.
    /// </summary>
    private List<MaterializedRow> MinusStep(ref PatternArray pa, int minusIndex, string activeGraph, List<MaterializedRow> input)
    {
        var rhs = EvalGroup(ref pa, minusIndex, activeGraph, new List<MaterializedRow> { EmptyRow() });
        var output = new List<MaterializedRow>();
        foreach (var row in input)
        {
            bool removed = false;
            foreach (var other in rhs)
                if (SharesVariable(row, other) && Compatible(row, other)) { removed = true; break; }
            if (!removed) output.Add(row);
        }
        return output;
    }

    /// <summary>VALUES (inline data): build a row per data row (UNDEF leaves a variable unbound) and join with the input.</summary>
    private List<MaterializedRow> ValuesStep(ref PatternArray pa, int valuesIndex, List<MaterializedRow> input)
    {
        var slot = pa[valuesIndex];
        string valuesSpan = _source.Substring(slot.ValuesVarStart, slot.ValuesVarLength);
        var temp = new GraphPattern();
        new SparqlParser(valuesSpan.AsSpan()).ParseValues(ref temp);
        return JoinValuesClause(input, temp.Values, valuesSpan);
    }

    /// <summary>
    /// The trailing (post-query) VALUES is a JOIN with the inline data, not a filter — it can MULTIPLY a solution (one
    /// that is compatible with several data rows) and BIND a variable left unbound by an OPTIONAL (W3C values5/values7).
    /// The buffer's ValuesClause carries offsets into this executor's source, so it reuses the same join as inline VALUES.
    /// </summary>
    public List<MaterializedRow> JoinPostQueryValues(List<MaterializedRow> input, ValuesClause postValues)
        => JoinValuesClause(input, postValues, _source);

    /// <summary>Build a row per VALUES data row (UNDEF leaves the variable unbound) and join it with the input — the
    /// join multiplies and merges, so a solution compatible with several data rows yields several solutions.</summary>
    private List<MaterializedRow> JoinValuesClause(List<MaterializedRow> input, ValuesClause vc, string vcSource)
    {
        int varCount = vc.VariableCount;
        if (varCount == 0) return input;

        var varNames = new string[varCount];
        for (int i = 0; i < varCount; i++) { var (vs, vl) = vc.GetVariable(i); varNames[i] = vcSource.Substring(vs, vl); }

        var valueRows = new List<MaterializedRow>();
        var bindingArray = ArrayPool<Binding>.Shared.Rent(64);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 13);
        try
        {
            var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
            for (int r = 0; r < vc.RowCount; r++)
            {
                table.TruncateTo(0);
                for (int c = 0; c < varCount; c++)
                {
                    var (vs, vl) = vc.GetValueAt(r, c);
                    if (vl < 0) continue; // UNDEF
                    table.Bind(varNames[c].AsSpan(), ExpandValueTerm(vcSource.Substring(vs, vl)).AsSpan());
                }
                valueRows.Add(new MaterializedRow(table));
            }
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }
        return Join(input, valueRows);
    }

    // ── SERVICE (federation) ──────────────────────────────────────────────────────────────────────────
    // SERVICE [SILENT] ep { P } sends P to a REMOTE endpoint (not the local active graph — so the active graph does
    // NOT thread into it), and joins the returned rows with the input. Delegated to an injected ISparqlServiceExecutor
    // (the cutover injects HttpSparqlServiceExecutor), exactly as sub-SELECT delegates the modifier layer.

    private List<MaterializedRow> ServiceStep(ref PatternArray pa, int serviceIndex, List<MaterializedRow> input)
    {
        if (_serviceExecutor is null)
            throw new InvalidOperationException("SERVICE requires an ISparqlServiceExecutor (the cutover injects HttpSparqlServiceExecutor).");

        var slot = pa[serviceIndex];
        string serviceSrc = _source.Substring(slot.GraphTermStart, slot.GraphTermLength);
        var (endpointIsVariable, endpointText, silent, innerWhere) = ParseServiceParts(serviceSrc);
        string query = "SELECT * WHERE " + innerWhere;

        if (!endpointIsVariable)
            return Join(input, ServiceCall(StripIri(endpointText), query, silent));

        // SERVICE ?ep — the endpoint is taken from each outer row's binding.
        var output = new List<MaterializedRow>();
        foreach (var row in input)
        {
            var epValue = row.GetValueByName(endpointText.AsSpan());
            if (epValue.IsEmpty) continue; // endpoint unbound: no call
            output.AddRange(Join(new List<MaterializedRow> { row }, ServiceCall(StripIri(epValue.ToString()), query, silent)));
        }
        return output;
    }

    private List<MaterializedRow> ServiceCall(string endpoint, string query, bool silent)
    {
        try
        {
            var rows = _serviceExecutor!.ExecuteSelectAsync(endpoint, query).GetAwaiter().GetResult();
            var result = new List<MaterializedRow>(rows.Count);
            var bindingArray = ArrayPool<Binding>.Shared.Rent(64);
            var charArray = ArrayPool<char>.Shared.Rent(1 << 13);
            try
            {
                var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
                foreach (var row in rows)
                {
                    table.TruncateTo(0);
                    foreach (var v in row.Variables)
                        if (row.TryGetBinding(v, out var b)) table.Bind(("?" + v.TrimStart('?')).AsSpan(), b.ToRdfTerm().AsSpan());
                    result.Add(new MaterializedRow(table));
                }
            }
            finally
            {
                ArrayPool<Binding>.Shared.Return(bindingArray);
                ArrayPool<char>.Shared.Return(charArray);
            }
            return result;
        }
        catch when (silent)
        {
            // SILENT: a failed endpoint contributes the EMPTY multiset (no solutions), so a non-OPTIONAL SERVICE
            // eliminates the join (Join(input, ∅) = ∅) — the certified behaviour. Returning a single empty row instead
            // would silently make the SERVICE optional (Join(input, {μ0}) = input). OPTIONAL { SERVICE SILENT } still
            // preserves its input rows, but that is the OptionalHeader's left-join, not ServiceCall's concern.
            return new List<MaterializedRow>();
        }
    }

    // ── sub-SELECT ────────────────────────────────────────────────────────────────────────────────────
    // A nested query: its WHERE is walked through the SAME path with the active graph threaded in, then the
    // SOLUTION-MODIFIER layer (project / distinct / group + aggregates / having / order / limit) is applied by the
    // shipping QueryResults.FromMaterializedSimple — graph-agnostic and shared, reused not reimplemented. The
    // projected rows then join with the outer rows on their shared variables.

    private List<MaterializedRow> SubSelectStep(ref PatternArray pa, int headerIndex, string activeGraph, List<MaterializedRow> input)
    {
        var slot = pa[headerIndex];
        int subStart = slot.GraphTermStart; // absolute offset of the sub-SELECT in _source

        // ADR-047 — evaluate the sub-SELECT in the ONE source/offset regime. Parse its clauses AND its body at absolute
        // offsets over _source, the same string the prologue prefixes index, so a prefixed name in the body expands
        // against the same source. (The old code sliced subSrc/innerWhere substrings and handed them to a second
        // executor as its source, while the prefix mappings still indexed the outer prologue — so prefix expansion
        // sliced the substring at a prologue offset and threw ArgumentOutOfRange on any prefixed inner term.)
        var sub = new SparqlParser(_source.AsSpan()).ParseSubSelectCoreAt(subStart);

        int whereBrace = _source.IndexOf('{', subStart); // the body's '{' (first brace after the SELECT clause)
        int whereClose = MatchBrace(_source, whereBrace);
        var innerBuffer = new byte[PatternSlot.Size * 256];
        var innerPa = new PatternArray(innerBuffer);
        int innerRoot = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(whereBrace, ref innerPa);
        var innerBag = new TreeJoinExecutor(_store, _source, _prefixes, _prefixSource, _serviceExecutor,
                _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _namedGraphs, _defaultGraphs)
            .Evaluate(ref innerPa, innerRoot, activeGraph);

        // A trailing VALUES inside the sub-SELECT joins its WHERE solutions BEFORE the modifier layer (W3C inline02).
        // sub.Values offsets index _source (parsed at absolute offset above), so the shared join reuses _source.
        if (sub.Values.HasValues)
            innerBag = JoinPostQueryValues(innerBag, sub.Values);

        var selectClause = BuildSelectClause(sub);
        var qrBindings = new Binding[64];
        var qrStringBuffer = new char[16384];
        // Lend the prefixes (there is no outer QueryBuffer here) so the modifier layer can evaluate a computed sub-SELECT
        // projection like ( CONCAT(?f," ",?l) AS ?full ) — without them EvaluateSelectExpressions never ran and the alias
        // came out unbound (W3C sq12, surfaced by the C2 CONSTRUCT-subquery cutover).
        var qr = QueryResults.FromMaterializedSimple(innerBag, _source.AsSpan(), _store, qrBindings, qrStringBuffer,
            sub.Limit, sub.Offset, sub.Distinct, sub.OrderBy, sub.GroupBy, selectClause, sub.Having, buffer: null, prefixes: _prefixes);

        var outNames = OutputVarNames(sub, _source, whereBrace, whereClose);
        var projected = new List<MaterializedRow>();
        var bindingArray = ArrayPool<Binding>.Shared.Rent(64);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 13);
        try
        {
            var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
            while (qr.MoveNext())
            {
                var bt = qr.Current;
                table.TruncateTo(0);
                foreach (var name in outNames)
                {
                    int idx = bt.FindBinding(("?" + name).AsSpan());
                    if (idx >= 0) table.Bind(("?" + name).AsSpan(), bt.GetString(idx));
                }
                projected.Add(new MaterializedRow(table));
            }
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }
        return Join(input, projected);
    }

    private static SelectClause BuildSelectClause(SubSelect sub)
    {
        var sc = new SelectClause { Distinct = sub.Distinct, SelectAll = sub.SelectAll };
        for (int i = 0; i < sub.ProjectedVarCount; i++)
        {
            var (start, length) = sub.GetProjectedVariable(i);
            sc.AddProjectedVariable(start, length);
        }
        for (int i = 0; i < sub.AggregateCount; i++)
            sc.AddAggregate(sub.GetAggregate(i));
        return sc;
    }

    private static List<string> OutputVarNames(SubSelect sub, string source, int whereBrace, int whereClose)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        if (sub.SelectAll)
        {
            // SELECT * — the output variables are the body's variables, in first-seen order, scanned over the body's
            // span (whereBrace..whereClose) in the one source.
            for (int i = whereBrace; i <= whereClose && i < source.Length; i++)
            {
                if (source[i] != '?') continue;
                int j = i + 1;
                while (j < source.Length && (char.IsLetterOrDigit(source[j]) || source[j] == '_')) j++;
                if (j > i + 1 && seen.Add(source.Substring(i + 1, j - i - 1))) names.Add(source.Substring(i + 1, j - i - 1));
                i = j - 1;
            }
            return names;
        }
        for (int i = 0; i < sub.ProjectedVarCount; i++)
        {
            var (start, length) = sub.GetProjectedVariable(i);
            string name = source.Substring(start, length).TrimStart('?');
            if (seen.Add(name)) names.Add(name);
        }
        // Aggregate aliases: SELECT (COUNT(?x) AS ?c) projects ?c, which is not a plain projected variable —
        // FromMaterializedSimple binds the computed aggregate to that alias, so read it back too.
        for (int i = 0; i < sub.AggregateCount; i++)
        {
            var agg = sub.GetAggregate(i);
            if (agg.AliasLength == 0) continue;
            string name = source.Substring(agg.AliasStart, agg.AliasLength).TrimStart('?');
            if (seen.Add(name)) names.Add(name);
        }
        return names;
    }

    // ── shared composition helpers (over MaterializedRow) ───────────────────────────────────────────────

    private static MaterializedRow EmptyRow() => new(new BindingTable(Array.Empty<Binding>(), Array.Empty<char>()));

    /// <summary>Natural join: every compatible (left, right) pair merges (left's bindings plus right's new ones).</summary>
    private static List<MaterializedRow> Join(List<MaterializedRow> left, List<MaterializedRow> right)
    {
        var output = new List<MaterializedRow>();
        var bindingArray = ArrayPool<Binding>.Shared.Rent(64);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 13);
        try
        {
            var table = new BindingTable(bindingArray.AsSpan(0, 64), charArray.AsSpan(0, 1 << 13));
            foreach (var l in left)
                foreach (var r in right)
                {
                    if (!Compatible(l, r)) continue;
                    table.TruncateTo(0);
                    l.RestoreBindings(ref table);
                    for (int j = 0; j < r.BindingCount; j++)
                        if (!HasHash(l, r.GetHash(j))) table.BindWithHash(r.GetHash(j), r.GetValue(j));
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

    /// <summary>Two rows are compatible iff every shared variable (same name-hash) has the same value.</summary>
    private static bool Compatible(MaterializedRow a, MaterializedRow b)
    {
        for (int i = 0; i < a.BindingCount; i++)
        {
            int h = a.GetHash(i);
            for (int j = 0; j < b.BindingCount; j++)
                if (b.GetHash(j) == h && !a.GetValue(i).SequenceEqual(b.GetValue(j))) return false;
        }
        return true;
    }

    private static bool SharesVariable(MaterializedRow a, MaterializedRow b)
    {
        for (int i = 0; i < a.BindingCount; i++)
            for (int j = 0; j < b.BindingCount; j++)
                if (a.GetHash(i) == b.GetHash(j)) return true;
        return false;
    }

    private static bool HasHash(MaterializedRow row, int hash)
    {
        for (int i = 0; i < row.BindingCount; i++)
            if (row.GetHash(i) == hash) return true;
        return false;
    }

    private static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        throw new ArgumentException("Unbalanced braces");
    }

    private static (bool endpointIsVariable, string endpointText, bool silent, string innerWhere) ParseServiceParts(string s)
    {
        int i = SkipWs(s, "SERVICE".Length);
        bool silent = false;
        if (Matches(s, i, "SILENT")) { i = SkipWs(s, i + "SILENT".Length); silent = true; }

        int epStart = i;
        bool isVar;
        if (i < s.Length && s[i] == '<') { while (i < s.Length && s[i] != '>') i++; if (i < s.Length) i++; isVar = false; }
        else { while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{') i++; isVar = true; }
        string endpointText = s.Substring(epStart, i - epStart);

        int open = s.IndexOf('{', i);
        int close = MatchBrace(s, open);
        return (isVar, endpointText, silent, s.Substring(open, close - open + 1));
    }

    private static int SkipWs(string s, int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; return i; }
    private static bool Matches(string s, int i, string w) =>
        i + w.Length <= s.Length && string.Compare(s, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0;
    private static string StripIri(string t) => t.Length >= 2 && t[0] == '<' && t[^1] == '>' ? t[1..^1] : t;

    /// <summary>
    /// A VALUES value as a canonical RDF term: a prefixed name is expanded to its full IRI; an IRI, literal (string,
    /// langtagged, or typed), number, boolean, or blank node is verbatim. (UNDEF is handled by the caller.)
    /// </summary>
    private string ExpandValueTerm(string text)
    {
        if (text.Length == 0) return text;
        char c = text[0];
        if (c is '<' or '"' or '\'' or '_') return text; // IRI / literal / blank node — already a canonical term

        // Numeric and boolean VALUES tokens are typed literals in SPARQL (25 ≡ "25"^^xsd:integer) — canonicalize via
        // the shared LiteralForm so VALUES and the constant-object scan (TriplePatternScan) stay identical; the old
        // path matched these value-aware. A non-numeric/non-boolean token falls through to prefix expansion below.
        _ = LiteralForm.CanonicalizeNumericOrBoolean(text.AsSpan(), out var canonical);
        if (canonical != null) return canonical;

        return text.Contains(':') ? ExpandPname(text) : text; // a prefixed name; everything else has no ':' to expand
    }

    /// <summary>
    /// Expand a prefixed name (<c>:g1</c>, <c>ex:g1</c>) to its full <c>&lt;…&gt;</c> IRI via the prologue prefixes
    /// (stored WITH the trailing colon and WITH brackets) — used for GRAPH terms and VALUES values. A term already
    /// in IRI form, or with an unknown prefix, is returned unchanged.
    /// </summary>
    private string ExpandPname(string term)
    {
        if (term.Length == 0 || term[0] == '<' || _prefixes == null) return term;
        int colon = term.IndexOf(':');
        if (colon < 0) return term;

        var prefix = term.AsSpan(0, colon + 1); // includes the ':' — e.g. "ex:" or ":"
        foreach (var pm in _prefixes)
        {
            if (_prefixSource.AsSpan(pm.PrefixStart, pm.PrefixLength).SequenceEqual(prefix))
            {
                var iri = _prefixSource.AsSpan(pm.IriStart, pm.IriLength);
                var inner = iri.Length >= 2 && iri[0] == '<' ? iri[1..^1] : iri;
                return "<" + inner.ToString() + term.AsSpan(colon + 1).ToString() + ">";
            }
        }
        return term;
    }

    /// <summary>Whether a concrete graph IRI is visible under the dataset restriction (null = all visible).</summary>
    private bool GraphAccessible(string graphTerm)
    {
        if (_namedGraphs == null) return true;
        foreach (var g in _namedGraphs)
            if (StripIri(g) == StripIri(graphTerm)) return true;
        return false;
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
            if (results.Count < _maxRows) // LIMIT pushed into the scan
            {
                results.Add(new MaterializedRow(bindings));
                ResultLimitExceededException.ThrowIfExceeded(_guardCap, results.Count); // unbounded-result guard
            }
            return;
        }

        // FROM dataset: a default-context pattern scans the UNION of the FROM graphs. A scan does NOT truncate the last
        // match's bindings on exhaustion (the parent scan's next MoveNext normally does), and the union loop is that
        // parent here — so reset to the seeded count between graphs, or graph N+1's scan would inherit graph N's bound
        // terms and over-constrain. (The same reset CrossGraphMultiPatternScan does between graphs.)
        var union = DefaultGraphUnion(graphs[index]);
        if (union != null)
        {
            int seed = bindings.Count;
            for (int gi = 0; gi < union.Length && results.Count < _maxRows; gi++)
            {
                bindings.TruncateTo(seed);
                ScanAndJoinAt(patterns, graphs, index, union[gi], ref bindings, results);
            }
        }
        else
            ScanAndJoinAt(patterns, graphs, index, graphs[index], ref bindings, results);
    }

    private void ScanAndJoinAt(TriplePattern[] patterns, string[] graphs, int index, string graph,
        ref BindingTable bindings, List<MaterializedRow> results)
    {
        var scan = new TriplePatternScan(_store, _source.AsSpan(), patterns[index], bindings, graph.AsSpan(),
            _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _prefixes, ObjectCandidatesFor(patterns[index]));
        try
        {
            // Stop the nested-loop scan as soon as the cap is reached — the work-saving heart of LIMIT-pushdown.
            while (results.Count < _maxRows && scan.MoveNext(ref bindings))
                JoinAt(patterns, graphs, index + 1, ref bindings, results);
        }
        finally
        {
            scan.Dispose();
        }
    }

    /// <summary>
    /// The graphs a pattern with default-context graph <paramref name="graph"/> ("") scans when a FROM dataset
    /// redefined the default graph: the FROM graphs (their RDF merge IS the default graph, SPARQL §13.2). Returns null
    /// for the single-graph cases — a GRAPH &lt;g&gt; override, or the real unnamed default with no FROM clause — so the
    /// caller takes the unchanged single-scan path.
    /// </summary>
    private string[]? DefaultGraphUnion(string graph)
        => graph.Length == 0 && _defaultGraphs is { Length: > 0 } ? _defaultGraphs : null;

    /// <summary>
    /// ADR-047 materialization fix — fold a FLAT pure-BGP into the aggregate accumulator as the nested-loop join
    /// produces each row, instead of materializing the whole intermediate as a <c>List&lt;MaterializedRow&gt;</c> just
    /// to reduce it to one row. This restores the streaming old path's O(1) intermediate memory: a COUNT over a
    /// 1,000,000-row join no longer allocates the 1,000,000 rows. The leaf drives the SAME
    /// <see cref="GroupedRow.UpdateAggregates"/> the materializing path uses, so the result is identical — only the
    /// rows are never collected. The caller (QueryExecutor.TryFoldStreamingAggregate) gates on <see cref="IsFlatBgp"/>
    /// (every child a plain triple) and a single global group (no GROUP BY); the accumulator handles the rest.
    /// </summary>
    public void FoldFlatBgpAggregate(ref PatternArray pa, int rootHeader, string activeGraph, GroupedRow group)
    {
        var patterns = new List<TriplePattern>();
        var graphs = new List<string>();
        var e = pa.EnumerateDirectChildren(rootHeader);
        while (e.MoveNext())
        {
            patterns.Add(TripleFromSlot(e.Current)); // IsFlatBgp guarantees: every child is a plain triple
            graphs.Add(activeGraph);
        }
        if (patterns.Count == 0)
            return; // nothing to fold ⇒ the group finalizes to default aggregate values (COUNT 0, etc.)

        var patternArr = patterns.ToArray();
        var graphArr = graphs.ToArray();
        if (_reorderBgp && patternArr.Length > 1)
            ReorderBySelectivity(ref patternArr, ref graphArr);

        var bindingArray = ArrayPool<Binding>.Shared.Rent(256);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 16);
        try
        {
            var bindings = new BindingTable(bindingArray.AsSpan(0, 256), charArray.AsSpan(0, 1 << 16));
            JoinAtFold(patternArr, graphArr, 0, ref bindings, group);
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }
    }

    /// <summary>
    /// Streaming sibling of <see cref="JoinAt"/>: the same zero-GC nested-loop BGP join, but at the leaf it FOLDS the
    /// solution into the aggregate accumulator (<see cref="GroupedRow.UpdateAggregates"/>) rather than materializing a
    /// <see cref="MaterializedRow"/>. No List, no per-row allocation — the materialization fix's hot path. No
    /// <c>_maxRows</c> cap: an aggregate consumes the whole match set.
    /// </summary>
    private void JoinAtFold(TriplePattern[] patterns, string[] graphs, int index, ref BindingTable bindings, GroupedRow group)
    {
        if (index == patterns.Length)
        {
            group.UpdateAggregates(bindings, _source);
            return;
        }

        var union = DefaultGraphUnion(graphs[index]); // FROM dataset: fold over the union of the FROM graphs
        if (union != null)
        {
            int seed = bindings.Count; // reset between graphs — see the JoinAt union loop for why
            foreach (var g in union)
            {
                bindings.TruncateTo(seed);
                ScanAndJoinAtFold(patterns, graphs, index, g, ref bindings, group);
            }
        }
        else
            ScanAndJoinAtFold(patterns, graphs, index, graphs[index], ref bindings, group);
    }

    private void ScanAndJoinAtFold(TriplePattern[] patterns, string[] graphs, int index, string graph,
        ref BindingTable bindings, GroupedRow group)
    {
        var scan = new TriplePatternScan(_store, _source.AsSpan(), patterns[index], bindings, graph.AsSpan(),
            _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _prefixes, ObjectCandidatesFor(patterns[index]));
        try
        {
            while (scan.MoveNext(ref bindings))
                JoinAtFold(patterns, graphs, index + 1, ref bindings, group);
        }
        finally
        {
            scan.Dispose();
        }
    }

    /// <summary>
    /// ADR-047 — stream a FLAT pure-BGP into a bounded top-K heap for ORDER BY + LIMIT, instead of materializing the
    /// whole match set just to sort it and take the first <paramref name="capacity"/> rows. Retains only
    /// <paramref name="capacity"/> (= OFFSET + LIMIT) rows — O(K) memory — where the old path materializes the full
    /// set. <see cref="MaterializedRowComparer.CompareBindings"/> rejects a row that sorts after the worst kept
    /// WITHOUT allocating it, so only rows that enter the heap are materialized (near-zero-GC). Returns the kept rows
    /// UNSORTED (≤ capacity); the caller re-sorts that small set and applies OFFSET/LIMIT. The caller gates on
    /// <see cref="IsFlatBgp"/> and a bounded capacity. The "stream when reducible" half applied to ORDER BY: top-N
    /// is bounded, so we keep only the N we need.
    /// </summary>
    public List<MaterializedRow> StreamFlatBgpTopK(ref PatternArray pa, int rootHeader, string activeGraph,
        MaterializedRowComparer comparer, int capacity)
    {
        // A min-PQ whose MIN is the WORST kept (the row that sorts LAST): feed it the REVERSED comparer, so Peek is the
        // eviction candidate and a better row displaces it.
        var heap = new PriorityQueue<MaterializedRow, MaterializedRow>(
            Comparer<MaterializedRow>.Create((x, y) => comparer.Compare(y, x)));

        var patterns = new List<TriplePattern>();
        var graphs = new List<string>();
        var e = pa.EnumerateDirectChildren(rootHeader);
        while (e.MoveNext())
        {
            patterns.Add(TripleFromSlot(e.Current)); // IsFlatBgp guarantees: every child is a plain triple
            graphs.Add(activeGraph);
        }
        if (patterns.Count == 0)
            return new List<MaterializedRow>();

        var patternArr = patterns.ToArray();
        var graphArr = graphs.ToArray();
        if (_reorderBgp && patternArr.Length > 1)
            ReorderBySelectivity(ref patternArr, ref graphArr);

        var bindingArray = ArrayPool<Binding>.Shared.Rent(256);
        var charArray = ArrayPool<char>.Shared.Rent(1 << 16);
        try
        {
            var bindings = new BindingTable(bindingArray.AsSpan(0, 256), charArray.AsSpan(0, 1 << 16));
            JoinAtTopK(patternArr, graphArr, 0, ref bindings, heap, capacity, comparer);
        }
        finally
        {
            ArrayPool<Binding>.Shared.Return(bindingArray);
            ArrayPool<char>.Shared.Return(charArray);
        }

        var result = new List<MaterializedRow>(heap.Count);
        while (heap.TryDequeue(out var row, out _))
            result.Add(row);
        return result;
    }

    /// <summary>
    /// Streaming sibling of <see cref="JoinAt"/> for top-K: at the leaf, offer the solution to the bounded heap. Below
    /// capacity it is materialized and inserted; at capacity it is materialized ONLY if it sorts before the worst kept
    /// (checked against the binding table, no allocation otherwise), then displaces that worst. No <c>_maxRows</c> cap:
    /// every solution is offered, but at most <paramref name="capacity"/> survive.
    /// </summary>
    private void JoinAtTopK(TriplePattern[] patterns, string[] graphs, int index, ref BindingTable bindings,
        PriorityQueue<MaterializedRow, MaterializedRow> heap, int capacity, MaterializedRowComparer comparer)
    {
        if (index == patterns.Length)
        {
            if (heap.Count < capacity)
            {
                var row = new MaterializedRow(bindings);
                heap.Enqueue(row, row);
            }
            else if (comparer.CompareBindings(ref bindings, heap.Peek()) < 0) // sorts before the worst kept
            {
                var row = new MaterializedRow(bindings);
                heap.EnqueueDequeue(row, row); // add it, evict the worst
            }
            // else: sorts at/after every kept row — drop it without allocating (the zero-GC win)
            return;
        }

        var union = DefaultGraphUnion(graphs[index]); // FROM dataset: offer rows from the union of the FROM graphs
        if (union != null)
        {
            int seed = bindings.Count; // reset between graphs — see the JoinAt union loop for why
            foreach (var g in union)
            {
                bindings.TruncateTo(seed);
                ScanAndJoinAtTopK(patterns, graphs, index, g, ref bindings, heap, capacity, comparer);
            }
        }
        else
            ScanAndJoinAtTopK(patterns, graphs, index, graphs[index], ref bindings, heap, capacity, comparer);
    }

    private void ScanAndJoinAtTopK(TriplePattern[] patterns, string[] graphs, int index, string graph,
        ref BindingTable bindings, PriorityQueue<MaterializedRow, MaterializedRow> heap, int capacity,
        MaterializedRowComparer comparer)
    {
        var scan = new TriplePatternScan(_store, _source.AsSpan(), patterns[index], bindings, graph.AsSpan(),
            _temporalMode, _asOfTime, _rangeStart, _rangeEnd, _prefixes, ObjectCandidatesFor(patterns[index]));
        try
        {
            while (scan.MoveNext(ref bindings))
                JoinAtTopK(patterns, graphs, index + 1, ref bindings, heap, capacity, comparer);
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
}
