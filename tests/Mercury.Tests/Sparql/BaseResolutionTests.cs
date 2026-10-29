using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §4.1.1.1: a relative IRI is resolved against the base IRI (RFC 3986 §5.2), when the query is parsed.
/// Through 1.8.32 BASE was parsed and stored, but no IRI the query writes was resolved against it: &lt;x&gt; looked up
/// the atom "&lt;x&gt;", and PREFIX : &lt;&gt; made :x the IRI "&lt;x&gt;" (W3C base-prefix-1, -2, -4, -5, -6).
/// SparqlParser.ResolveAgainstBase now writes every relative IRIREF as its absolute IRI before the query is parsed,
/// each against the BASE in effect where it is written; SparqlEngine applies it to every query and update.
/// </summary>
[Collection("QuadStore")]
public class BaseResolutionTests : PooledStoreTestBase
{
    public BaseResolutionTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("BASE <http://ex/x/> SELECT * { <a> <p> ?v }",
        "BASE <http://ex/x/> SELECT * { <http://ex/x/a> <http://ex/x/p> ?v }")]
    [InlineData("BASE <http://ex/x/> PREFIX : <> PREFIX h: <#> SELECT * { :a h:b ?v }",
        "BASE <http://ex/x/> PREFIX : <http://ex/x/> PREFIX h: <http://ex/x/#> SELECT * { :a h:b ?v }")]
    [InlineData("BASE <http://ex/xxx/abc> SELECT * { <> <#g> <../m> . </r> <./j> ?v }",           // RFC 3986 forms
        "BASE <http://ex/xxx/abc> SELECT * { <http://ex/xxx/abc> <http://ex/xxx/abc#g> <http://ex/m> . <http://ex/r> <http://ex/xxx/j> ?v }")]
    [InlineData("BASE <http://ex/a/> PREFIX p: <q/> BASE <b/> SELECT * { <c> p:d ?v }",            // each BASE in turn
        "BASE <http://ex/a/> PREFIX p: <http://ex/a/q/> BASE <http://ex/a/b/> SELECT * { <http://ex/a/b/c> p:d ?v }")]
    [InlineData("BASE <http://ex/x/> SELECT * { ?s ?p \"<a>\" FILTER(?s != <a> && ?v < 3) } # <a>",  // literal, operator, comment
        "BASE <http://ex/x/> SELECT * { ?s ?p \"<a>\" FILTER(?s != <http://ex/x/a> && ?v < 3) } # <a>")]
    [InlineData("BASE <http://ex/x/> SELECT * { ?s ?p \"1\"^^<int> VALUES ?s { <a> } }",          // a datatype IRI, VALUES
        "BASE <http://ex/x/> SELECT * { ?s ?p \"1\"^^<http://ex/x/int> VALUES ?s { <http://ex/x/a> } }")]
    [InlineData("BASE <http://ex/x/> SELECT * { <http://other/a> ?p ?v }",                          // absolute: as written
        "BASE <http://ex/x/> SELECT * { <http://other/a> ?p ?v }")]
    [InlineData("SELECT * { <a> ?p ?v }", "SELECT * { <a> ?p ?v }")]                                  // no BASE
    [InlineData("PREFIX base: <http://ex/> SELECT * { base:a ?p ?v }", "PREFIX base: <http://ex/> SELECT * { base:a ?p ?v }")]
    public void ResolveAgainstBase_WritesEachRelativeIriAbsolute(string query, string expected)
    {
        Assert.Equal(expected, SparqlParser.ResolveAgainstBase(query));
    }

    [Fact]
    public void Query_ReadsRelativeIrisAgainstItsBase()
    {
        Store.AddCurrent("<http://ex/x/a>", "<http://ex/x/p>", "<http://ex/x/b>");
        Store.AddCurrent("<http://ex/x/b>", "<http://ex/x/p>", "\"end\"");

        var result = SparqlEngine.Query(Store,
            "BASE <http://ex/x/> PREFIX : <> SELECT ?v WHERE { <a> :p ?m . ?m <p> ?v FILTER(?m = <b>) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"end\"", Assert.Single(result.Rows!)["v"]);
    }

    [Fact]
    public void Construct_WritesItsTemplatesRelativeIrisAbsolute()
    {
        Store.AddCurrent("<http://ex/x/a>", "<http://ex/x/p>", "<http://ex/x/b>");

        var result = SparqlEngine.Query(Store,
            "BASE <http://ex/x/> CONSTRUCT { ?s <q> <c> } WHERE { ?s <p> <b> }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var triple = Assert.Single(result.Triples!);
        Assert.Equal(("<http://ex/x/a>", "<http://ex/x/q>", "<http://ex/x/c>"), triple);
    }

    [Fact]
    public void Update_EachOperation_ReadsRelativeIrisAgainstItsOwnBase()
    {
        var update = SparqlEngine.Update(Store,
            "BASE <http://ex/one/> INSERT DATA { <a> <p> <b> } ; BASE <http://ex/two/> INSERT DATA { GRAPH <g> { <a> <p> <b> } }",
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        var result = SparqlEngine.Query(Store,
            "SELECT ?g ?s WHERE { { <http://ex/one/a> <http://ex/one/p> <http://ex/one/b> BIND(\"default\" AS ?g) BIND(<http://ex/one/a> AS ?s) } " +
            "UNION { GRAPH ?g { ?s <http://ex/two/p> <http://ex/two/b> } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Contains(result.Rows!, row => row["g"] == "<http://ex/two/g>" && row["s"] == "<http://ex/two/a>");
    }
}
