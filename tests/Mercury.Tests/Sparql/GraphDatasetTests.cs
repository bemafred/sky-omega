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
/// (default-graph patterns scan the union of the FROM graphs); FROM NAMED restricts which named graphs GRAPH may
/// access; FROM without FROM NAMED makes ALL named graphs invisible. Locks the behaviour established during the
/// _defaultGraphs investigation — the default-union is handled by the default-pattern path, and GRAPH visibility
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
    public void DatasetClauses_AreRespectedByTheUnifiedGraphPath(string name, string query, string variable, string expectedPipe)
    {
        var result = SparqlEngine.Query(_store, query);
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
