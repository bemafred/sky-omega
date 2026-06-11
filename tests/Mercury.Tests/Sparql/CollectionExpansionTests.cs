using System;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// RDF Collection syntax <c>( a b c )</c> in a WHERE clause expands to the rdf:first / rdf:rest / rdf:nil list
/// structure, joined through synthetic variables — in BOTH the default-graph path and the unified GRAPH path
/// (ADR-045). An empty <c>()</c> is rdf:nil. Collections were a placeholder blank node (incomplete-but-equal) in
/// both paths before; completing the expansion improves both, so it is not a divergence.
/// </summary>
public class CollectionExpansionTests : IDisposable
{
    private const string RdfFirst = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
    private const string RdfRest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
    private const string RdfNil = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>";

    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public CollectionExpansionTests()
    {
        var tempPath = TempPath.Test("collection");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        // <urn:s> <urn:p> ( "1" "2" "3" )  and  <urn:e> <urn:p> ()  — mirrored into the default graph AND <urn:g>.
        _store.BeginBatch();
        foreach (var graph in new string?[] { null, "<urn:g>" })
        {
            void Add(string s, string p, string o)
            {
                if (graph is null) _store.AddCurrentBatched(s, p, o);
                else _store.AddCurrentBatched(s, p, o, graph);
            }
            Add("<urn:s>", "<urn:p>", "_:l1");
            Add("_:l1", RdfFirst, "\"1\""); Add("_:l1", RdfRest, "_:l2");
            Add("_:l2", RdfFirst, "\"2\""); Add("_:l2", RdfRest, "_:l3");
            Add("_:l3", RdfFirst, "\"3\""); Add("_:l3", RdfRest, RdfNil);
            Add("<urn:e>", "<urn:p>", RdfNil);
        }
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("default path", "SELECT ?a ?b ?c WHERE { <urn:s> <urn:p> ( ?a ?b ?c ) }")]
    [InlineData("GRAPH path", "SELECT ?a ?b ?c WHERE { GRAPH <urn:g> { <urn:s> <urn:p> ( ?a ?b ?c ) } }")]
    public void Collection_MatchesTheListStructure(string name, string query)
    {
        var result = SparqlEngine.Query(_store, query);
        Assert.True(result.Success, $"[{name}] {result.ErrorMessage}");
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\"1\"", row["a"]);
        Assert.Equal("\"2\"", row["b"]);
        Assert.Equal("\"3\"", row["c"]);
    }

    [Theory]
    [InlineData("default path", "SELECT ?x WHERE { ?x <urn:p> () }")]
    [InlineData("GRAPH path", "SELECT ?x WHERE { GRAPH <urn:g> { ?x <urn:p> () } }")]
    public void EmptyCollection_IsRdfNil(string name, string query)
    {
        var result = SparqlEngine.Query(_store, query);
        Assert.True(result.Success, $"[{name}] {result.ErrorMessage}");
        Assert.Equal("<urn:e>", Assert.Single(result.Rows!)["x"]);
    }
}
