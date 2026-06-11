using System;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// RDF-star quoted-triple queries through the PUBLIC facade (<see cref="SparqlEngine.Query"/> — the path the MCP
/// server and CLI use), which constructs a <c>QueryPlanner</c>. The planner once threw ArgumentOutOfRange on the
/// synthetic negative offsets in the expanded reification patterns (rdf:type / subject / predicate / object), so
/// RDF-star worked through the core <c>QueryExecutor</c> but crashed through the facade
/// (ck:obs-facade-rdfstar-planner-crash). It was fixed by the planner's synthetic-offset guard (the same one the
/// RDF-collection work added). The existing SparqlStarTests run the core executor (no planner), so the facade path
/// was untested — this locks it across the quoted-triple shapes.
/// </summary>
public class SparqlStarFacadeTests : IDisposable
{
    private const string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    private const string RdfStatement = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement>";
    private const string RdfSubject = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#subject>";
    private const string RdfPredicate = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate>";
    private const string RdfObject = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#object>";

    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public SparqlStarFacadeTests()
    {
        var tempPath = TempPath.Test("star-facade");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        // The reification of << <urn:a> <urn:p> <urn:v1> >> (certainty 0.9), itself stated by <urn:doc>.
        _store.BeginBatch();
        _store.AddCurrentBatched("<urn:r1>", RdfType, RdfStatement);
        _store.AddCurrentBatched("<urn:r1>", RdfSubject, "<urn:a>");
        _store.AddCurrentBatched("<urn:r1>", RdfPredicate, "<urn:p>");
        _store.AddCurrentBatched("<urn:r1>", RdfObject, "<urn:v1>");
        _store.AddCurrentBatched("<urn:r1>", "<urn:certainty>", "\"0.9\"");
        _store.AddCurrentBatched("<urn:doc>", "<urn:states>", "<urn:r1>");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("quoted triple in subject", "SELECT ?c WHERE { << <urn:a> <urn:p> <urn:v1> >> <urn:certainty> ?c }", "c", "\"0.9\"")]
    [InlineData("variable in quoted triple", "SELECT ?s WHERE { << ?s <urn:p> <urn:v1> >> <urn:certainty> ?c }", "s", "<urn:a>")]
    [InlineData("variable predicate slot", "SELECT ?p WHERE { << <urn:a> ?p <urn:v1> >> <urn:certainty> ?c }", "p", "<urn:p>")]
    [InlineData("quoted triple in object", "SELECT ?d WHERE { ?d <urn:states> << <urn:a> <urn:p> <urn:v1> >> }", "d", "<urn:doc>")]
    public void RdfStar_ThroughTheFacade_ResolvesWithoutCrashing(string name, string query, string variable, string expected)
    {
        var result = SparqlEngine.Query(_store, query);
        Assert.True(result.Success, $"[{name}] {result.ErrorMessage}");
        Assert.Equal(expected, Assert.Single(result.Rows!)[variable]);
    }
}
