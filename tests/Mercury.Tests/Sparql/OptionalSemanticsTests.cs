using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// OPTIONAL is a left join of the group with what precedes it (SPARQL 1.1 §18.2.2.6), and a subject that is a node of its
/// own (<c>[]</c>, <c>[ … ]</c>, a collection, a quoted triple) is one node for its whole property list. Through 1.8.28 a
/// variable an OPTIONAL group binds only in a nested OPTIONAL was seeded from outside, so the nested OPTIONAL could not
/// bind it (W3C nested-opt-1), and <c>[] :p ?a ; :q ?b</c> gave each triple a node of its own (dawg-optional-complex-2/3).
/// </summary>
[Collection("QuadStore")]
public class OptionalSemanticsTests : PooledStoreTestBase
{
    public OptionalSemanticsTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void NestedOptional_BindingAnOuterVariable_DoesNotJoinThatVariablesValue()
    {
        // The inner group's solutions are (w=3, v=2) and (w=4, v=2); neither is compatible with v=1, so the outer row stays
        Store.AddCurrent("<http://ex/x1>", "<http://ex/p>", "<http://ex/v1>");
        Store.AddCurrent("<http://ex/x2>", "<http://ex/p>", "<http://ex/v2>");
        Store.AddCurrent("<http://ex/x3>", "<http://ex/q>", "<http://ex/w3>");
        Store.AddCurrent("<http://ex/x3>", "<http://ex/q>", "<http://ex/w4>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { <http://ex/x1> <http://ex/p> ?v OPTIONAL { <http://ex/x3> <http://ex/q> ?w OPTIONAL { <http://ex/x2> <http://ex/p> ?v } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/v1>", row["v"]);
        Assert.False(row.ContainsKey("w"));
    }

    [Theory]
    [InlineData("<http://ex/v1>", true)]
    [InlineData("<http://ex/v9>", false)]
    public void OptionalFilter_OnAVariableBoundOnlyByANestedOptional_SeesTheOuterValue(string compared, bool extends)
    {
        // LeftJoin's condition (the group's FILTER) is evaluated on the joined row, which has the outer ?v; the nested
        // OPTIONAL does not match, so the group's own solution has no ?v
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/v1>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "<http://ex/w>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?v ?w WHERE { ?s <http://ex/p> ?v OPTIONAL { ?s <http://ex/q> ?w OPTIONAL { ?s <http://ex/r> ?v } " +
            $"FILTER(?v = {compared}) }} }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/v1>", row["v"]);
        Assert.Equal(extends, row.ContainsKey("w"));
    }

    [Fact]
    public void NestedOptional_WellDesigned_ExtendsEachRow()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/name>", "\"A\"");
        Store.AddCurrent("<http://ex/a>", "<http://ex/plan>", "<http://ex/p1>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/dept>", "<http://ex/d1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/name>", "\"B\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/plan>", "<http://ex/p2>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/name>", "\"C\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s ?plan ?dept WHERE { ?s <http://ex/name> ?n OPTIONAL { ?s <http://ex/plan> ?plan OPTIONAL { ?s <http://ex/dept> ?dept } } } ORDER BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.Rows!.Count);
        Assert.Equal("<http://ex/d1>", result.Rows[0]["dept"]);
        Assert.Equal("<http://ex/p2>", result.Rows[1]["plan"]);
        Assert.False(result.Rows[1].ContainsKey("dept"));
        Assert.Single(result.Rows[2]);
    }

    [Theory]
    [InlineData("[] <http://ex/name> ?name ; <http://ex/nick> ?nick")]
    [InlineData("[ <http://ex/name> ?name ] <http://ex/nick> ?nick")]
    [InlineData("[ <http://ex/name> ?name ] <http://ex/nick> ?nick ; <http://ex/name> ?name")]
    public void ANodeSubject_IsOneNodeForItsPropertyList(string pattern)
    {
        Store.AddCurrent("<http://ex/alice>", "<http://ex/name>", "\"Alice\"");
        Store.AddCurrent("<http://ex/alice>", "<http://ex/nick>", "\"A\"");
        Store.AddCurrent("<http://ex/bert>", "<http://ex/name>", "\"Bert\"");
        Store.AddCurrent("<http://ex/bert>", "<http://ex/nick>", "\"B\"");

        var result = SparqlEngine.Query(Store, $"SELECT ?name ?nick WHERE {{ {pattern} }} ORDER BY ?name",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal([("\"Alice\"", "\"A\""), ("\"Bert\"", "\"B\"")], result.Rows!.Select(r => (r["name"], r["nick"])));
    }

    [Fact]
    public void ACollectionSubject_IsOneListForItsPropertyList()
    {
        // ( "x" ) <p> ?a ; <q> ?b : one list node with both triples; per triple it was two lists
        var first = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
        var rest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
        Store.AddCurrent("_:l", first, "\"x\"");
        Store.AddCurrent("_:l", rest, "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>");
        Store.AddCurrent("_:l", "<http://ex/p>", "\"a\"");
        Store.AddCurrent("_:l", "<http://ex/q>", "\"b\"");
        Store.AddCurrent("_:m", first, "\"x\"");
        Store.AddCurrent("_:m", rest, "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>");
        Store.AddCurrent("_:m", "<http://ex/p>", "\"c\"");

        var result = SparqlEngine.Query(Store, "SELECT ?a ?b WHERE { (\"x\") <http://ex/p> ?a ; <http://ex/q> ?b }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\"a\"", row["a"]);
    }
}
