using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A collection, blank-node property list, quoted triple or sequence path in a pattern expands into several triple
/// patterns, joined by variables the expansion adds. Through 1.8.28:
/// <list type="bullet">
/// <item>the expansion kept at most 32 triples and dropped the rest without an error, so a long collection or path
/// matched on its first part only;</item>
/// <item>each kind of join variable had 32 names, after which the joins silently broke;</item>
/// <item>the names were <c>?_bn0</c>, <c>?_seq0</c>, <c>?_qt0</c>, so a query's own <c>?_bn0</c> joined a hidden one;</item>
/// <item>a CONSTRUCT template held at most 16 triples and 32 collection nodes.</item>
/// </list>
/// </summary>
[Collection("QuadStore")]
public class LongExpansionTests : PooledStoreTestBase
{
    public LongExpansionTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void Collection_OfTwentyItems_MatchesOnEveryItem()
    {
        // 20 items expand to 41 triple patterns; <b>'s list differs only in its last item
        const string first = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
        const string rest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
        const string nil = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>";
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:a1");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "_:b1");
        for (int i = 1; i <= 20; i++)
        {
            Store.AddCurrent($"_:a{i}", first, $"\"x{i}\"");
            Store.AddCurrent($"_:a{i}", rest, i == 20 ? nil : $"_:a{i + 1}");
            Store.AddCurrent($"_:b{i}", first, i == 20 ? "\"other\"" : $"\"x{i}\"");
            Store.AddCurrent($"_:b{i}", rest, i == 20 ? nil : $"_:b{i + 1}");
        }

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> (\"x1\" \"x2\" \"x3\" \"x4\" \"x5\" \"x6\" \"x7\" \"x8\" \"x9\" \"x10\" " +
            "\"x11\" \"x12\" \"x13\" \"x14\" \"x15\" \"x16\" \"x17\" \"x18\" \"x19\" \"x20\") }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void BlankNodePropertyList_OfFortyPairs_MatchesOnEveryPair()
    {
        // 1 + 40 triple patterns; <b>'s node lacks only the last pair
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:na");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "_:nb");
        for (int i = 1; i <= 40; i++)
        {
            Store.AddCurrent("_:na", $"<http://ex/q{i}>", $"\"v{i}\"");
            if (i < 40) Store.AddCurrent("_:nb", $"<http://ex/q{i}>", $"\"v{i}\"");
        }
        var pairs = string.Join(" ; ", Enumerable.Range(1, 40).Select(i => $"<http://ex/q{i}> \"v{i}\""));

        var result = SparqlEngine.Query(Store, $"SELECT ?s WHERE {{ ?s <http://ex/p> [ {pairs} ] }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void SequencePath_OfFortySteps_FollowsEveryStep()
    {
        // <n0> <p> <n1> <p> … <n40>; a path of 40 steps from <n0> reaches <n40> only
        for (int i = 0; i < 40; i++)
            Store.AddCurrent($"<http://ex/n{i}>", "<http://ex/p>", $"<http://ex/n{i + 1}>");
        var path = string.Join("/", Enumerable.Repeat("<http://ex/p>", 40));

        var result = SparqlEngine.Query(Store, $"SELECT ?o WHERE {{ <http://ex/n0> {path} ?o }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/n40>", Assert.Single(result.Rows!)["o"]);
    }

    [Fact]
    public void FortyBlankNodePropertyLists_InOneQuery_EachJoins()
    {
        for (int i = 1; i <= 40; i++)
        {
            Store.AddCurrent("<http://ex/a>", $"<http://ex/p{i}>", $"_:n{i}");
            Store.AddCurrent($"_:n{i}", "<http://ex/q>", $"\"v{i}\"");
        }
        var patterns = string.Join(" . ", Enumerable.Range(1, 40).Select(i => $"<http://ex/a> <http://ex/p{i}> [ <http://ex/q> ?o{i} ]"));

        var result = SparqlEngine.Query(Store, $"SELECT ?o1 ?o40 WHERE {{ {patterns} }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\"v1\"", row["o1"]);
        Assert.Equal("\"v40\"", row["o40"]);
    }

    [Fact]
    public void AVariableNamedLikeAnOldJoinVariable_IsTheQuerysOwn()
    {
        // ?_bn0 was the name of the first property list's hidden node, so it joined it
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:n");
        Store.AddCurrent("_:n", "<http://ex/q>", "\"x\"");
        Store.AddCurrent("<http://ex/c>", "<http://ex/r>", "\"y\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?_bn0 WHERE { ?s <http://ex/p> [ <http://ex/q> ?o ] . ?_bn0 <http://ex/r> ?y }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/c>", Assert.Single(result.Rows!)["_bn0"]);
    }

    [Fact]
    public void TextMatch_WithAPropertyListObject_Runs()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:n");
        Store.AddCurrent("_:n", "<http://ex/q>", "\"x\"");
        Store.AddCurrent("<http://ex/a>", "<http://ex/name>", "\"Alice\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> [ <http://ex/q> ?o ] . ?s <http://ex/name> ?n FILTER(text:match(?n, \"Alice\")) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void ConstructTemplate_WithAFortyItemCollection_BuildsEveryTriple()
    {
        // 40 rdf:first and 40 rdf:rest triples, plus the triple that points at the list
        var items = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"\"i{i}\""));

        var result = SparqlEngine.Query(Store, $"CONSTRUCT {{ <http://ex/a> <http://ex/list> ({items}) }} WHERE {{}}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(81, result.Triples!.Count);
        Assert.Equal(40, result.Triples.Count(t => t.Predicate == "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>"));
        Assert.Single(result.Triples, t => t.Object == "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>");
    }
}
