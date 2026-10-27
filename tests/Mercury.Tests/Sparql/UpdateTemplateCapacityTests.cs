using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// [52] QuadPattern / [50] Quads / [51] TriplesTemplate: an Update template holds any number of triples and GRAPH
/// blocks, and a triple may use predicate (;) and object (,) lists. Mercury parsed templates into fixed-capacity
/// structs that silently dropped what did not fit: 32 default-graph triples, 4 GRAPH blocks, 8 triples per GRAPH block
/// (found when a Mercury MCP memory write stored 8 of 25 triples). Object lists were a syntax error. Each test runs the
/// update and counts what the store holds afterwards.
/// </summary>
[Collection("QuadStore")]
public class UpdateTemplateCapacityTests : PooledStoreTestBase
{
    public UpdateTemplateCapacityTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void InsertTemplate_GraphBlockWith40Triples_StoresAll()
    {
        var triples = string.Join(" . ", Enumerable.Range(0, 40).Select(i => $"<http://ex/s> <http://ex/p{i}> {i}"));

        var update = SparqlEngine.Update(Store, $"INSERT {{ GRAPH <http://ex/g> {{ {triples} }} }} WHERE {{ }}", TestContext.Current.CancellationToken);
        var stored = SparqlEngine.Query(Store, "SELECT * WHERE { GRAPH <http://ex/g> { ?s ?p ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(40, update.AffectedCount);
        Assert.Equal(40, stored.Rows!.Count);
    }

    [Fact]
    public void InsertTemplate_DefaultGraphWith40Triples_StoresAll()
    {
        var triples = string.Join(" . ", Enumerable.Range(0, 40).Select(i => $"<http://ex/s> <http://ex/p{i}> {i}"));

        var update = SparqlEngine.Update(Store, $"INSERT {{ {triples} }} WHERE {{ }}", TestContext.Current.CancellationToken);
        var stored = SparqlEngine.Query(Store, "SELECT * WHERE { <http://ex/s> ?p ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(40, stored.Rows!.Count);
    }

    [Fact]
    public void InsertTemplate_SixGraphBlocks_StoresEachGraph()
    {
        var blocks = string.Join(" ", Enumerable.Range(0, 6).Select(g => $"GRAPH <http://ex/g{g}> {{ <http://ex/s> <http://ex/p> {g} }}"));

        var update = SparqlEngine.Update(Store, $"INSERT {{ {blocks} }} WHERE {{ }}", TestContext.Current.CancellationToken);
        var stored = SparqlEngine.Query(Store, "SELECT ?g WHERE { GRAPH ?g { <http://ex/s> ?p ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(6, stored.Rows!.Count);
    }

    [Fact]
    public void DeleteTemplate_GraphBlockWith12Triples_DeletesAll()
    {
        var triples = string.Join(" . ", Enumerable.Range(0, 12).Select(i => $"<http://ex/s> <http://ex/p{i}> {i}"));
        SparqlEngine.Update(Store, $"INSERT DATA {{ GRAPH <http://ex/g> {{ {triples} }} }}", TestContext.Current.CancellationToken);

        var update = SparqlEngine.Update(Store, $"DELETE {{ GRAPH <http://ex/g> {{ {triples} }} }} WHERE {{ }}", TestContext.Current.CancellationToken);
        var left = SparqlEngine.Query(Store, "SELECT * WHERE { GRAPH <http://ex/g> { ?s ?p ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(12, update.AffectedCount);
        Assert.Empty(left.Rows!);
    }

    [Fact]
    public void DeleteWhere_GraphBlockWith12Triples_DeletesAll()
    {
        // IRI objects: a bare number is stored in a different form by INSERT DATA than a WHERE matches (ADR-053).
        var triples = string.Join(" . ", Enumerable.Range(0, 12).Select(i => $"<http://ex/s> <http://ex/p{i}> <http://ex/o{i}>"));
        SparqlEngine.Update(Store, $"INSERT DATA {{ GRAPH <http://ex/g> {{ {triples} }} }}", TestContext.Current.CancellationToken);

        var update = SparqlEngine.Update(Store, $"DELETE WHERE {{ GRAPH <http://ex/g> {{ {triples} }} }}", TestContext.Current.CancellationToken);
        var left = SparqlEngine.Query(Store, "SELECT * WHERE { GRAPH <http://ex/g> { ?s ?p ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Empty(left.Rows!);
    }

    [Theory]
    [InlineData("INSERT { <http://ex/s> <http://ex/p> 1, 2 ; <http://ex/q> 3 } WHERE { }", "SELECT * WHERE { <http://ex/s> ?p ?o }")]
    [InlineData("INSERT { GRAPH <http://ex/g> { <http://ex/s> <http://ex/p> 1, 2 ; <http://ex/q> 3 } } WHERE { }", "SELECT * WHERE { GRAPH <http://ex/g> { <http://ex/s> ?p ?o } }")]
    public void InsertTemplate_PredicateAndObjectLists_StoreEveryTriple(string update, string query)
    {
        var result = SparqlEngine.Update(Store, update, TestContext.Current.CancellationToken);
        var stored = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, stored.Rows!.Count);
    }

    [Fact]
    public void ModifyTemplate_VariableGraphAndObjectList_InstantiatesPerSolution()
    {
        SparqlEngine.Update(Store, "INSERT DATA { GRAPH <http://ex/g1> { <http://ex/a> <http://ex/k> 1 } GRAPH <http://ex/g2> { <http://ex/b> <http://ex/k> 2 } }", TestContext.Current.CancellationToken);

        var update = SparqlEngine.Update(Store,
            "INSERT { GRAPH ?g { ?s <http://ex/seen> 1, 2 } } WHERE { GRAPH ?g { ?s <http://ex/k> ?v } }", TestContext.Current.CancellationToken);
        var stored = SparqlEngine.Query(Store, "SELECT ?g ?s ?o WHERE { GRAPH ?g { ?s <http://ex/seen> ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(4, stored.Rows!.Count);
    }

    [Fact]
    public void Template_PropertyPath_IsASyntaxError()
    {
        // [51] TriplesTemplate takes a Verb (VarOrIri | 'a'), not a path.
        var update = SparqlEngine.Update(Store, "INSERT { <http://ex/s> <http://ex/a>/<http://ex/b> <http://ex/o> } WHERE { }", TestContext.Current.CancellationToken);

        Assert.False(update.Success);
    }
}
