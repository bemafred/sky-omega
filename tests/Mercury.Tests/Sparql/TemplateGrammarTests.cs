using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// One template grammar ([52] TriplesTemplate) reads INSERT DATA, DELETE DATA, Update templates and CONSTRUCT's template,
/// with collections and blank-node property lists as template nodes, a new blank node each time the template is
/// instantiated. Through 1.8.28 INSERT DATA had its own triple parser that read neither, a template's blank node was one
/// node for every solution, and an INSERT DATA blank node in a later session took the same label as one stored earlier.
/// </summary>
[Collection("QuadStore")]
public class TemplateGrammarTests : PooledStoreTestBase
{
    public TemplateGrammarTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void InsertData_ACollection_IsStoredAsAList()
    {
        var update = SparqlEngine.Update(Store, "INSERT DATA { <http://ex/a> <http://ex/p> (\"x\" \"y\") }",
            TestContext.Current.CancellationToken);
        var match = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> (\"x\" \"y\") }",
            TestContext.Current.CancellationToken);
        var count = SparqlEngine.Query(Store, "SELECT * WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(match.Rows!)["s"]);
        Assert.Equal(5, count.Rows!.Count); // the link, two rdf:first, two rdf:rest
    }

    [Fact]
    public void InsertData_APropertyList_IsStoredOnANewNode()
    {
        var update = SparqlEngine.Update(Store,
            "INSERT DATA { <http://ex/a> <http://ex/p> [ <http://ex/q> \"v\" , \"w\" ; <http://ex/r> [] ] }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store,
            "SELECT ?v ?m WHERE { <http://ex/a> <http://ex/p> ?n . ?n <http://ex/q> ?v . ?n <http://ex/r> ?m } ORDER BY ?v",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(["\"v\"", "\"w\""], result.Rows!.Select(r => r["v"]));
        Assert.All(result.Rows!, r => Assert.StartsWith("_:", r["m"])); // [] is a new blank node
    }

    [Fact]
    public void InsertTemplate_ABlankNode_IsANewNodeForEachSolution()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/o>");

        var update = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/r> _:n . _:n <http://ex/of> ?s } WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store, "SELECT ?s ?n WHERE { ?s <http://ex/r> ?n . ?n <http://ex/of> ?t }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.NotEqual(result.Rows[0]["n"], result.Rows[1]["n"]);
    }

    [Fact]
    public void InsertTemplate_APropertyList_IsANewNodeForEachSolution()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"1\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"2\"");

        var update = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/r> [ <http://ex/v> ?o ] } WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store, "SELECT DISTINCT ?n WHERE { ?s <http://ex/r> ?n . ?n <http://ex/v> ?v }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
    }

    [Fact]
    public void InsertData_TheSameLabelInTwoRequests_IsTwoNodes()
    {
        // Each request mints new nodes; the old counter restarted per process, so a later session's _:x met an earlier one
        var first = SparqlEngine.Update(Store, "INSERT DATA { <http://ex/a> <http://ex/p> _:x }", TestContext.Current.CancellationToken);
        var second = SparqlEngine.Update(Store, "INSERT DATA { <http://ex/b> <http://ex/p> _:x }", TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store, "SELECT DISTINCT ?o WHERE { ?s <http://ex/p> ?o }", TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.ErrorMessage);
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
    }

    [Theory]
    [InlineData("DELETE DATA { <http://ex/a> <http://ex/p> ( \"x\" ) }")]
    [InlineData("DELETE DATA { <http://ex/a> <http://ex/p> [ <http://ex/q> \"v\" ] }")]
    [InlineData("DELETE { ?s <http://ex/p> [] } WHERE { ?s ?p ?o }")]
    [InlineData("INSERT DATA { ?s <http://ex/p> <http://ex/o> }")]
    [InlineData("INSERT DATA { <http://ex/a> <http://ex/p> _:b } ; INSERT DATA { <http://ex/c> <http://ex/p> _:b }")]
    public void Update_ThatTheGrammarForbids_IsASyntaxError(string update)
    {
        var result = SparqlEngine.Update(Store, update, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public void ConstructTemplate_ObjectListsAndPropertyLists_AreInstantiated()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"1\"");

        var result = SparqlEngine.Query(Store,
            "CONSTRUCT { ?s <http://ex/r> \"x\", \"y\" ; <http://ex/n> [ <http://ex/v> ?o ] } WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4, result.Triples!.Count);
        var node = Assert.Single(result.Triples, t => t.Predicate == "<http://ex/n>").Object;
        Assert.StartsWith("_:", node);
        Assert.Contains((node, "<http://ex/v>", "\"1\""), result.Triples);
    }
}
