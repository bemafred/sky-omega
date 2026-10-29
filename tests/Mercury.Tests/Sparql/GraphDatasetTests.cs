using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-045 cutover: the unified GRAPH path respects SPARQL dataset clauses. FROM redefines the default graph
/// (default-graph patterns read the RDF merge of the FROM graphs, as one graph, §13.2); FROM NAMED restricts which named graphs GRAPH may
/// access; FROM without FROM NAMED makes ALL named graphs invisible. Locks the behaviour established during the
/// _defaultGraphs investigation — the merge is read by the default-pattern scan, and GRAPH visibility
/// under a dataset clause by the wire's _namedGraphs restriction (so the executor never needs _defaultGraphs: a
/// GRAPH-only WHERE has no default-graph patterns).
/// </summary>
public class GraphDatasetTests : IDisposable
{
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public GraphDatasetTests()
    {
        var tempPath = TempPath.Test("graph-dataset");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        _store.BeginBatch();
        _store.AddCurrentBatched("<urn:d>", "<urn:p>", "<urn:vd>");             // the real default graph
        _store.AddCurrentBatched("<urn:a>", "<urn:p>", "<urn:v1>", "<urn:g1>"); // named graph g1
        _store.AddCurrentBatched("<urn:b>", "<urn:p>", "<urn:v2>", "<urn:g2>"); // named graph g2
        _store.AddCurrentBatched("<urn:c>", "<urn:q>", "<urn:v3>", "<urn:g1>"); // one triple in both g1 and g2
        _store.AddCurrentBatched("<urn:c>", "<urn:q>", "<urn:v3>", "<urn:g2>");
        _store.AddCurrentBatched("<urn:a>", "<urn:r>", "<urn:b>", "<urn:g1>");  // a -r-> b in g1, b -r-> c in g2
        _store.AddCurrentBatched("<urn:b>", "<urn:r>", "<urn:c>", "<urn:g2>");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("no FROM — the real default graph", "SELECT ?s WHERE { ?s <urn:p> ?o }", "s", "<urn:d>")]
    [InlineData("FROM g1 — g1 becomes the default", "SELECT ?s FROM <urn:g1> WHERE { ?s <urn:p> ?o }", "s", "<urn:a>")]
    [InlineData("FROM g1 g2 — default graph is the UNION", "SELECT ?s FROM <urn:g1> FROM <urn:g2> WHERE { ?s <urn:p> ?o }", "s", "<urn:a>|<urn:b>")]
    [InlineData("FROM g1 + GRAPH g1 — invisible (no FROM NAMED)", "SELECT ?s FROM <urn:g1> WHERE { GRAPH <urn:g1> { ?s <urn:p> ?o } }", "s", "")]
    [InlineData("FROM NAMED g1 + GRAPH g1 — visible", "SELECT ?s FROM NAMED <urn:g1> WHERE { GRAPH <urn:g1> { ?s <urn:p> ?o } }", "s", "<urn:a>")]
    [InlineData("FROM g1 + FROM NAMED g2 + GRAPH g2", "SELECT ?s FROM <urn:g1> FROM NAMED <urn:g2> WHERE { GRAPH <urn:g2> { ?s <urn:p> ?o } }", "s", "<urn:b>")]
    [InlineData("FROM NAMED g1 g2 + GRAPH ?g — enumerates only the named set", "SELECT ?g FROM NAMED <urn:g1> FROM NAMED <urn:g2> WHERE { GRAPH ?g { ?s <urn:p> ?o } }", "g", "<urn:g1>|<urn:g2>")]
    [InlineData("FROM NAMED g1 + GRAPH g2 — not in the set, empty", "SELECT ?s FROM NAMED <urn:g1> WHERE { GRAPH <urn:g2> { ?s <urn:p> ?o } }", "s", "")]
    [InlineData("FROM NAMED g1, no FROM — the default graph is empty (§13.2)", "SELECT ?s FROM NAMED <urn:g1> WHERE { ?s <urn:p> ?o }", "s", "")]
    [InlineData("FROM g1 twice — g1 once", "SELECT ?s FROM <urn:g1> FROM <urn:g1> WHERE { ?s <urn:p> ?o }", "s", "<urn:a>")]
    [InlineData("FROM g1 g2 — a triple in both is in their merge once", "SELECT ?s FROM <urn:g1> FROM <urn:g2> WHERE { ?s <urn:q> ?o }", "s", "<urn:c>")]
    [InlineData("FROM g1 g2 — once, joined", "SELECT ?s FROM <urn:g1> FROM <urn:g2> WHERE { ?s <urn:q> ?o . ?x <urn:r> ?s }", "s", "<urn:c>")]
    [InlineData("FROM g1 g2 — once, with ORDER BY and LIMIT", "SELECT ?s FROM <urn:g1> FROM <urn:g2> WHERE { ?s <urn:q> ?o } ORDER BY ?s LIMIT 5", "s", "<urn:c>")]
    [InlineData("FROM g1 g2 — counted once", "SELECT (COUNT(*) AS ?n) FROM <urn:g1> FROM <urn:g2> WHERE { ?s <urn:q> ?o }", "n", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("FROM g1 g2 — a path walks the merge, from g1 into g2", "SELECT ?o FROM <urn:g1> FROM <urn:g2> WHERE { <urn:a> <urn:r>+ ?o }", "o", "<urn:b>|<urn:c>")]
    [InlineData("FROM g1 g2 — zero or more steps in the merge", "SELECT ?o FROM <urn:g1> FROM <urn:g2> WHERE { <urn:a> <urn:r>* ?o }", "o", "<urn:a>|<urn:b>|<urn:c>")]
    [InlineData("FROM g1 g2 — an inverse path walks the merge", "SELECT ?s FROM <urn:g1> FROM <urn:g2> WHERE { <urn:c> ^<urn:r>+ ?s }", "s", "<urn:a>|<urn:b>")]
    [InlineData("FROM g1 — a path stays in g1", "SELECT ?o FROM <urn:g1> WHERE { <urn:a> <urn:r>+ ?o }", "o", "<urn:b>")]
    [InlineData("FROM NAMED g1 twice + GRAPH ?g — g1 once", "SELECT ?g FROM NAMED <urn:g1> FROM NAMED <urn:g1> WHERE { GRAPH ?g { ?s <urn:p> ?o } }", "g", "<urn:g1>")]
    public void DatasetClauses_AreRespectedByTheUnifiedGraphPath(string name, string query, string variable, string expectedPipe)
    {
        var result = SparqlEngine.Query(_store, query, TestContext.Current.CancellationToken);
        Assert.True(result.Success, $"[{name}] {result.ErrorMessage}");

        var actual = result.Rows!
            .Select(row => row.GetValueOrDefault(variable) ?? "")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var expected = expectedPipe.Length == 0
            ? new List<string>()
            : expectedPipe.Split('|').OrderBy(value => value, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }
}
