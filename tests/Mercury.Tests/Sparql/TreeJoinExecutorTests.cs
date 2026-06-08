using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-045 Step 4 — the zero-GC tree executor foundation (<see cref="TreeJoinExecutor"/>). Proves the BGP core:
/// the default graph and a GRAPH-wrapped mirror of the same pattern, evaluated through the ONE active-graph-
/// parameterized path, both equal the shipping default-graph baseline — and the hot scan/join loop is zero-GC.
/// </summary>
public class TreeJoinExecutorTests : IDisposable
{
    private const string MirrorGraph = "<urn:test:mirror>";
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public TreeJoinExecutorTests()
    {
        var tempPath = TempPath.Test("tree-join-exec");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        (string s, string p, string o)[] data =
        {
            ("<urn:a>", "<urn:p>", "<urn:v1>"),
            ("<urn:b>", "<urn:p>", "<urn:v2>"),
            ("<urn:c>", "<urn:p>", "<urn:v3>"),
            ("<urn:v1>", "<urn:next>", "<urn:z>"), // only v1 has a next — makes the 2-pattern join selective
            ("<urn:a>", "<urn:link>", "<urn:b>"),  // a -> b -> c chain for property-path closures
            ("<urn:b>", "<urn:link>", "<urn:c>"),
            ("<urn:a>", "<urn:q>", "\"qa\""),      // a, b have q; c does not — makes OPTIONAL / (NOT) EXISTS non-trivial
            ("<urn:b>", "<urn:q>", "\"qb\""),
        };
        _store.BeginBatch();
        foreach (var (s, p, o) in data)
        {
            _store.AddCurrentBatched(s, p, o);                  // default graph
            _store.AddCurrentBatched(s, p, o, MirrorGraph);     // mirror graph (identical triples)
        }
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("SELECT ?s ?o WHERE { ?s <urn:p> ?o }", new[] { "s", "o" })]
    [InlineData("SELECT ?s ?x WHERE { ?s <urn:p> ?o . ?o <urn:next> ?x }", new[] { "s", "x" })]
    // Property paths — reused from TriplePatternScan via the re-parsed PropertyPath: + (transitive), * (reflexive
    // transitive), ^ (inverse), / (sequence), | (alternative).
    [InlineData("SELECT ?s ?o WHERE { ?s <urn:link>+ ?o }", new[] { "s", "o" })]
    [InlineData("SELECT ?o WHERE { <urn:a> <urn:link>* ?o }", new[] { "o" })]
    [InlineData("SELECT ?s WHERE { ?s ^<urn:link> <urn:c> }", new[] { "s" })]
    [InlineData("SELECT ?o WHERE { <urn:a> <urn:link>/<urn:link> ?o }", new[] { "o" })]
    [InlineData("SELECT ?o WHERE { <urn:a> <urn:link>|<urn:p> ?o }", new[] { "o" })]
    [InlineData("SELECT ?o WHERE { <urn:a> <urn:link>? ?o }", new[] { "o" })]          // zero-or-one (reflexive + one hop)
    [InlineData("SELECT ?o WHERE { <urn:a> !<urn:p> ?o }", new[] { "o" })]             // negated property set
    [InlineData("SELECT ?o WHERE { <urn:a> (<urn:link>/<urn:link>) ?o }", new[] { "o" })] // grouped sequence
    // Composing operators (batch A) — UNION, OPTIONAL, BIND, FILTER, (NOT) EXISTS, nested group.
    [InlineData("SELECT ?s WHERE { { ?s <urn:p> ?o } UNION { ?s <urn:q> ?o } }", new[] { "s" })]
    [InlineData("SELECT ?s ?x WHERE { ?s <urn:p> ?o OPTIONAL { ?s <urn:q> ?x } }", new[] { "s", "x" })]
    [InlineData("SELECT ?l WHERE { ?s <urn:p> ?o BIND(STR(?o) AS ?l) }", new[] { "l" })]
    [InlineData("SELECT ?s WHERE { ?s <urn:p> ?o FILTER(?o = <urn:v1>) }", new[] { "s" })]
    [InlineData("SELECT ?o WHERE { ?s <urn:p> ?o FILTER(?s = <urn:a>) }", new[] { "o" })]
    [InlineData("SELECT ?s WHERE { ?s <urn:p> ?o FILTER EXISTS { ?s <urn:q> ?x } }", new[] { "s" })]
    [InlineData("SELECT ?s WHERE { ?s <urn:p> ?o FILTER NOT EXISTS { ?s <urn:q> ?x } }", new[] { "s" })]
    [InlineData("SELECT ?s WHERE { { ?s <urn:p> ?o } }", new[] { "s" })]
    // Composing operators (batch B) — MINUS, VALUES, sub-SELECT (with the shared modifier layer).
    [InlineData("SELECT ?s WHERE { ?s <urn:p> ?o MINUS { ?s <urn:q> ?x } }", new[] { "s" })]
    [InlineData("SELECT ?o WHERE { VALUES ?s { <urn:a> <urn:b> } ?s <urn:p> ?o }", new[] { "o" })]
    [InlineData("SELECT ?o WHERE { { SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY ?o LIMIT 2 } }", new[] { "o" })]
    public void DefaultAndGraphWrapped_ThroughTheZeroGcExecutor_EqualTheBaseline(string query, string[] projection)
    {
        var baseline = BaselineCanonical(query, projection);

        // The default graph through the zero-GC tree executor (active graph = the default, "").
        var defaultRows = Canonicalize(ExecuteTree(ExtractWhereGroup(query), ""), projection);
        Assert.Equal(baseline, defaultRows);

        // The SAME pattern wrapped in GRAPH <mirror>: the active graph is threaded per pattern, the scan runs in the
        // mirror graph, and the result equals the default-graph baseline — the divergence dissolved by construction.
        string wrappedWhere = "{ GRAPH " + MirrorGraph + " " + ExtractWhereGroup(query) + " }";
        var wrappedRows = Canonicalize(ExecuteTree(wrappedWhere, ""), projection);
        Assert.Equal(baseline, wrappedRows);
    }

    [Fact]
    public void HotJoinPath_IsZeroGc_BeyondTheBoundedResultRows()
    {
        // Parse ONCE (the parse buffer is test-harness setup), then measure only the executor. The pooled binding
        // buffers and the bounded materialized rows are the only allocations Evaluate makes; the inner scan/join
        // loop (TriplePatternScan.MoveNext + the recursion) adds nothing per scan step — a per-step-allocating join
        // over this 2-pattern, multi-match join would blow far past the ceiling.
        const string where = "{ ?s <urn:p> ?o . ?o <urn:next> ?x }";
        var buffer = new byte[PatternSlot.Size * 256];
        var pa = new PatternArray(buffer);
        int root = new SparqlParser(where.AsSpan()).ParsePatternTree(ref pa);
        var executor = new TreeJoinExecutor(_store, where);

        _store.AcquireReadLock();
        try
        {
            for (int i = 0; i < 50; i++) executor.Evaluate(ref pa, root, ""); // warm up JIT + the array pool

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int iterations = 200;
            for (int i = 0; i < iterations; i++) executor.Evaluate(ref pa, root, "");
            long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

            // Per call: the flatten lists/arrays (O(patterns)) and the materialized result rows (O(results)) — not
            // O(scan steps). The cutover caches the flatten and streams the rows; the inner loop is already zero-GC.
            Assert.True(perCall < 4_000, $"per-call allocation {perCall} bytes — the hot scan loop is allocating per step");
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    private List<MaterializedRow> ExecuteTree(string whereGroup, string activeGraph, ISparqlServiceExecutor? serviceExecutor = null)
    {
        var buffer = new byte[PatternSlot.Size * 256];
        var pa = new PatternArray(buffer);
        int root = new SparqlParser(whereGroup.AsSpan()).ParsePatternTree(ref pa);

        _store.AcquireReadLock();
        try
        {
            return new TreeJoinExecutor(_store, whereGroup, null, null, serviceExecutor).Evaluate(ref pa, root, activeGraph);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Service_ThroughTheZeroGcExecutor_DelegatesAndIgnoresTheActiveGraph()
    {
        // SERVICE is federation: the inner pattern goes to a REMOTE endpoint, not the active graph. A mock executor
        // returns canned rows; wrapping the SERVICE in GRAPH <m> must not change anything. (Shipping would attempt a
        // live request, so there is no facade baseline; the model validates the same shape.)
        const string where = "{ SERVICE <http://remote/sparql> { ?s <urn:r> ?o } }";
        var executor = new MockServiceExecutor();

        var unwrapped = Canonicalize(ExecuteTree(where, "", executor), new[] { "o" });
        var wrapped = Canonicalize(ExecuteTree("{ GRAPH " + MirrorGraph + " " + where + " }", "", executor), new[] { "o" });

        var expected = new List<string> { "o=<urn:remote1>", "o=<urn:remote2>" };
        Assert.Equal(expected, unwrapped);
        Assert.Equal(expected, wrapped);
    }

    private sealed class MockServiceExecutor : ISparqlServiceExecutor
    {
        public ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(string endpointUri, string query, CancellationToken ct = default)
        {
            var rows = new List<ServiceResultRow>();
            foreach (var iri in new[] { "urn:remote1", "urn:remote2" })
            {
                var row = new ServiceResultRow();
                row.AddBinding("o", new ServiceBinding(ServiceBindingType.Uri, iri));
                rows.Add(row);
            }
            return new ValueTask<List<ServiceResultRow>>(rows);
        }

        public ValueTask<bool> ExecuteAskAsync(string endpointUri, string query, CancellationToken ct = default)
            => new ValueTask<bool>(false);
    }

    private static List<string> Canonicalize(List<MaterializedRow> rows, string[] projection)
    {
        var canon = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var parts = new List<string>(projection.Length);
            foreach (var v in projection)
            {
                var value = row.GetValueByName(("?" + v).AsSpan());
                if (!value.IsEmpty) parts.Add(v + "=" + value.ToString());
            }
            canon.Add(string.Join("|", parts));
        }
        canon.Sort(StringComparer.Ordinal);
        return canon;
    }

    private List<string> BaselineCanonical(string query, string[] projection)
    {
        var result = SparqlEngine.Query(_store, query);
        Assert.True(result.Success, result.ErrorMessage);
        var rows = result.Rows!;
        var canon = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var parts = new List<string>(projection.Length);
            foreach (var v in projection)
                if (row.TryGetValue(v, out var value)) parts.Add(v + "=" + value);
            canon.Add(string.Join("|", parts));
        }
        canon.Sort(StringComparer.Ordinal);
        return canon;
    }

    private static string ExtractWhereGroup(string query)
    {
        int w = query.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        int open = query.IndexOf('{', w);
        int close = MatchBrace(query, open);
        return query.Substring(open, close - open + 1);
    }

    private static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        throw new InvalidOperationException("unbalanced braces");
    }
}
