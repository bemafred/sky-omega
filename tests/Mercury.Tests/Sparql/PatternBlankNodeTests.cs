using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §4.1.4: a blank node in a graph pattern acts as a variable, and a labeled one (<c>_:b</c>) joins the
/// patterns that use its label. It is not a query variable: no result names it. On 1.8.26 a label matched only a
/// stored blank node of that same label, so <c>?s ?p _:x</c> matched nothing.
/// </summary>
[Collection("QuadStore")]
public class PatternBlankNodeTests : PooledStoreTestBase
{
    public PatternBlankNodeTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void LabeledBlankNode_MatchesAnyTerm()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"literal\"");

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s <http://ex/p> _:x } ORDER BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s"], result.Variables!);
        Assert.Equal(["<http://ex/a>", "<http://ex/b>"], result.Rows!.Select(r => r["s"]));
        Assert.All(result.Rows!, r => Assert.Single(r));
    }

    [Fact]
    public void LabeledBlankNode_JoinsThePatternsThatUseIt()
    {
        // Only <a>'s object has a <q>; the label joins on that term, whether it is an IRI or a blank node
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/m>");
        Store.AddCurrent("<http://ex/m>", "<http://ex/q>", "<http://ex/c>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "_:n");
        Store.AddCurrent("_:n", "<http://ex/q>", "<http://ex/d>");
        Store.AddCurrent("<http://ex/e>", "<http://ex/p>", "<http://ex/unlinked>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s ?o WHERE { ?s <http://ex/p> _:b . _:b <http://ex/q> ?o } ORDER BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Equal("<http://ex/a>", result.Rows[0]["s"]);
        Assert.Equal("<http://ex/c>", result.Rows[0]["o"]);
        Assert.Equal("<http://ex/b>", result.Rows[1]["s"]);
        Assert.Equal("<http://ex/d>", result.Rows[1]["o"]);
    }

    [Fact]
    public void LabeledBlankNode_IsNotTheVariableOfTheSameName()
    {
        // As one variable, _:b and ?b would have to be equal: no row. They are two, so ?b is <n>
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/m>");
        Store.AddCurrent("<http://ex/n>", "<http://ex/q>", "<http://ex/c>");

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s <http://ex/p> _:b . ?b <http://ex/q> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "b", "o"], result.Variables!);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/n>", row["b"]);
    }

    [Fact]
    public void LabeledBlankNode_InsideAPropertyList_Joins()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:x");
        Store.AddCurrent("_:x", "<http://ex/q>", "<http://ex/m>");
        Store.AddCurrent("<http://ex/m>", "<http://ex/r>", "<http://ex/c>");
        Store.AddCurrent("<http://ex/n>", "<http://ex/r>", "<http://ex/d>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { ?s <http://ex/p> [ <http://ex/q> _:b ] . _:b <http://ex/r> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "o"], result.Variables!);
        Assert.Equal("<http://ex/c>", Assert.Single(result.Rows!)["o"]);
    }

    [Fact]
    public void SelectDistinct_OverALabeledBlankNode_IsOneRowPerProjection()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o1>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o2>");

        var result = SparqlEngine.Query(Store, "SELECT DISTINCT * WHERE { ?s <http://ex/p> _:x }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void ConstructTemplate_LabeledBlankNode_IsFreshPerSolution()
    {
        // §16.2: a template's blank node is a new one for each solution; within one solution, one node
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/o2>");

        var result = SparqlEngine.Query(Store,
            "CONSTRUCT { ?s <http://ex/r> _:n . _:n <http://ex/v> ?o } WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4, result.Triples!.Count);
        var nodeOfA = Assert.Single(result.Triples!, t => t.Subject == "<http://ex/a>").Object;
        var nodeOfB = Assert.Single(result.Triples!, t => t.Subject == "<http://ex/b>").Object;
        Assert.StartsWith("_:", nodeOfA);
        Assert.NotEqual(nodeOfA, nodeOfB);
        Assert.Equal("<http://ex/o1>", Assert.Single(result.Triples!, t => t.Subject == nodeOfA).Object);
        Assert.Equal("<http://ex/o2>", Assert.Single(result.Triples!, t => t.Subject == nodeOfB).Object);
    }

    [Fact]
    public void ConstructWhere_LabeledBlankNode_MatchesAsAVariable_AndIsFreshInTheTemplate()
    {
        // The pattern's _:n matches <m1> and <m2>; the template's _:n is a new blank node, one per solution
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/m1>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/m2>");

        var result = SparqlEngine.Query(Store, "CONSTRUCT WHERE { ?s <http://ex/p> _:n }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Triples!.Count);
        Assert.All(result.Triples!, t => Assert.StartsWith("_:", t.Object));
        Assert.NotEqual(result.Triples[0].Object, result.Triples[1].Object);
    }

    [Fact]
    public void UpdateWhere_LabeledBlankNode_Joins()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/m>");
        Store.AddCurrent("<http://ex/m>", "<http://ex/q>", "<http://ex/c>");

        var update = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/linked> ?o } WHERE { ?s <http://ex/p> _:b . _:b <http://ex/q> ?o }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store, "SELECT ?o WHERE { <http://ex/a> <http://ex/linked> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal("<http://ex/c>", Assert.Single(result.Rows!)["o"]);
    }
}
