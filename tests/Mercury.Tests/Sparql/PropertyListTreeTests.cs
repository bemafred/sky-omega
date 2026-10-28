using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// One property-list reader ([83] PropertyListPathNotEmpty) for a subject and for a blank-node property list, and a
/// sequence path expanded once, when the query is parsed. Through 1.8.30 a <c>[ … ]</c> had a reader of its own: it
/// kept a path's base IRI only, so every path but a sequence matched nothing inside it; it found its ']' by counting
/// brackets, so a ']' in a literal ended it; and it accepted <c>[,]</c>, <c>[;]</c> and a '.' inside. A sequence was
/// expanded when the query ran, its middle nodes numbered from 0 again, so two sequences in one pattern, or a sequence
/// beside a <c>[ … ]</c>, shared a node and matched nothing.
/// </summary>
[Collection("QuadStore")]
public class PropertyListTreeTests : PooledStoreTestBase
{
    public PropertyListTreeTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("<http://ex/p>|<http://ex/q>", new[] { "\"viaP\"", "\"viaQ\"" })]
    [InlineData("<http://ex/x>/<http://ex/y>", new[] { "\"viaXY\"" })]
    [InlineData("^<http://ex/r>", new[] { "<http://ex/s>" })]
    public void PathInABlankNodePropertyList_IsEvaluatedAsOutsideIt(string path, string[] expected)
    {
        // s -r-> b ; b -p-> "viaP" ; b -q-> "viaQ" ; b -x-> c -y-> "viaXY"
        Store.AddCurrent("<http://ex/s>", "<http://ex/r>", "<http://ex/b>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"viaP\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/q>", "\"viaQ\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/x>", "<http://ex/c>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/y>", "\"viaXY\"");

        var inside = SparqlEngine.Query(Store, $"SELECT ?o WHERE {{ <http://ex/s> <http://ex/r> [ {path} ?o ] }}",
            TestContext.Current.CancellationToken);
        var outside = SparqlEngine.Query(Store,
            $"SELECT ?o WHERE {{ <http://ex/s> <http://ex/r> ?b . ?b {path} ?o }}", TestContext.Current.CancellationToken);

        Assert.True(inside.Success, inside.ErrorMessage);
        Assert.True(outside.Success, outside.ErrorMessage);
        Assert.Equal(expected.OrderBy(v => v), outside.Rows!.Select(r => r["o"]).OrderBy(v => v));
        Assert.Equal(expected.OrderBy(v => v), inside.Rows!.Select(r => r["o"]).OrderBy(v => v));
    }

    [Fact]
    public void TwoSequencePaths_InOnePattern_HaveTheirOwnMiddleNodes()
    {
        // a1 -p-> m1 -q-> b1 and c1 -r-> m2 -s-> d1: the two sequences go through different nodes
        Store.AddCurrent("<http://ex/a1>", "<http://ex/p>", "<http://ex/m1>");
        Store.AddCurrent("<http://ex/m1>", "<http://ex/q>", "<http://ex/b1>");
        Store.AddCurrent("<http://ex/c1>", "<http://ex/r>", "<http://ex/m2>");
        Store.AddCurrent("<http://ex/m2>", "<http://ex/s>", "<http://ex/d1>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { ?a <http://ex/p>/<http://ex/q> ?b . ?c <http://ex/r>/<http://ex/s> ?d }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/b1>", row["b"]);
        Assert.Equal("<http://ex/d1>", row["d"]);
    }

    [Fact]
    public void SequencePath_BesideABlankNodePropertyList_HasItsOwnMiddleNode()
    {
        Store.AddCurrent("<http://ex/a1>", "<http://ex/p>", "<http://ex/m1>");
        Store.AddCurrent("<http://ex/m1>", "<http://ex/q>", "<http://ex/b1>");
        Store.AddCurrent("<http://ex/n>", "<http://ex/t>", "\"x\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?x ?b WHERE { [ <http://ex/t> ?x ] . ?a <http://ex/p>/<http://ex/q> ?b }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\"x\"", row["x"]);
        Assert.Equal("<http://ex/b1>", row["b"]);
    }

    [Fact]
    public void BlankNodePropertyList_WithABracketInALiteral_EndsAtItsOwnBracket()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/r>", "<http://ex/b>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/q>", "\"a]b\"");

        var result = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/r> [ <http://ex/q> \"a]b\" ] }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/s>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void Collection_OfBlankNodePropertyLists_MatchesItsList()
    {
        // ( [ :v "1" ] [ :v "2" ] ): each item is a node of its own, read by the same property-list reader
        SparqlEngine.Update(Store,
            "INSERT DATA { <http://ex/s> <http://ex/list> ( [ <http://ex/v> \"1\" ] [ <http://ex/v> \"2\" ] ) }",
            TestContext.Current.CancellationToken);

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/list> ( [ <http://ex/v> \"1\" ] [ <http://ex/v> \"2\" ] ) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/s>", Assert.Single(result.Rows!)["s"]);
    }

    [Theory]
    [InlineData("SELECT * WHERE { [] . }")]
    [InlineData("SELECT * WHERE { [] }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ , ] }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ ; ] }")]
    [InlineData("SELECT * WHERE { [,] <http://ex/p> [;] . }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ <http://ex/q> ?o . ] }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ <http://ex/q> ] }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ <http://ex/q> ?o ?x ] }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> ( ?a ?b }")]
    public void PropertyList_TheGrammarDoesNotRead_IsASyntaxError(string query)
    {
        // [77] BlankNodePropertyList ::= '[' PropertyListNotEmpty ']'; ANON alone has no property list (W3C
        // syn-bad-bnode-dot, syn-bad-bnodes-missing-pvalues-01)
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("SELECT * WHERE { [ <http://ex/p> ?o ] . }")]
    [InlineData("SELECT * WHERE { [ <http://ex/p> ?o ] }")]
    [InlineData("SELECT * WHERE { [ <http://ex/p> ?o ] FILTER(true) }")]
    [InlineData("SELECT * WHERE { ( ?a ?b ) . }")]
    [InlineData("SELECT * WHERE { [] <http://ex/p> ?o }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ <http://ex/q> ?o ; ; <http://ex/r> ?x ; ] }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ <http://ex/q> ?o , ?x ] , [ <http://ex/r> ?y ] }")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> [ <http://ex/q> [ <http://ex/r> ( 1 [ <http://ex/t> ?z ] ) ] ] }")]
    public void PropertyList_TheGrammarReads_Parses(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }
}
